using System;
using System.Collections.Generic;
using System.Linq;

// The Performance Monitor's arrangement as the user left it: the order its cards were dragged
// into, which column of the panels each one went to, which cards are stacked under another,
// which are folded to their title line and which span both columns, for the top strip and for
// the panels below it separately. One setting holds all of it, so it survives restarts, and Reset
// layout clears it back to the designed dashboard.
//
// A slot is one place in the order: usually one card, or a card with others stacked under it.
// The panels flow down two columns; a slot that has never been moved takes the shorter column of
// its band, which alternates them from the designed order. Cards are named by a stable id ("cpu", "disks", "gpu:<adapter
// name>") rather than by position, so a saved layout still fits when hardware comes and goes: a
// card that is gone is skipped and keeps its saved place for when it returns, and a card that is
// new joins at the end.
namespace KillerShell.Services
{
    internal sealed class DashboardLayout
    {
        internal const string Strip = "strip";
        internal const string Panels = "panels";

        /// <summary>Where a dragged card lands relative to the card it is dropped on: in a slot of
        /// its own before or after that card's slot, or stacked at the bottom of that slot.</summary>
        internal enum Drop { Before, After, Below }

        private readonly Dictionary<string, List<string>> _order = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _stacked = new(StringComparer.Ordinal);   // sit under the card before them
        private readonly Dictionary<string, HashSet<string>> _folded = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _wide = new(StringComparer.Ordinal);     // span both columns
        private readonly Dictionary<string, HashSet<string>> _left = new(StringComparer.Ordinal);     // the column a moved card went to
        private readonly Dictionary<string, HashSet<string>> _right = new(StringComparer.Ordinal);

        internal bool IsDefault => _order.Count == 0 && _stacked.Count == 0 && _folded.Count == 0 && _wide.Count == 0
                                   && _left.Count == 0 && _right.Count == 0;

        /// <summary>The panel column a card was put in, 0 left and 1 right, or null when it has
        /// never been placed and takes the shorter column of its band.</summary>
        internal int? Column(string area, string id) => Has(_right, area, id) ? 1 : Has(_left, area, id) ? 0 : null;

        /// <summary>
        /// Moves <paramref name="id"/> within the panel columns: before or after the slot holding
        /// <paramref name="target"/> in that slot's column, or stacked at the bottom of it.
        /// <paramref name="shown"/> gives the column every card is in now; those are remembered
        /// with the new order, so the cards the user did not touch stay in their columns.
        /// </summary>
        internal List<List<string>> MoveInColumn(string area, IReadOnlyList<IReadOnlyList<string>> shownSlots,
            IReadOnlyDictionary<string, int> shown, string id, string target, Drop drop)
        {
            var slots = Rearranged(shownSlots, id, target, drop);
            if (!shown.TryGetValue(target, out int column)) return slots;
            var columns = Placed(shown, id, column);
            if (Same(slots, shownSlots) && columns.TryGetValue(id, out int now) && shown.TryGetValue(id, out int was) && now == was)
                return slots;
            Remember(area, slots);
            RememberColumns(area, slots, columns);
            return slots;
        }

        /// <summary>The columns after <paramref name="id"/> goes to <paramref name="column"/>.</summary>
        internal static Dictionary<string, int> Placed(IReadOnlyDictionary<string, int> shown, string id, int column)
        {
            var columns = shown.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            columns[id] = column;
            return columns;
        }

        // Every card of a slot is in the column of the slot's top card. A card that is gone keeps
        // the column it was in.
        private void RememberColumns(string area, List<List<string>> slots, IReadOnlyDictionary<string, int> columns)
        {
            var present = new HashSet<string>(slots.SelectMany(slot => slot), StringComparer.Ordinal);
            var left = Kept(_left, area, present);
            var right = Kept(_right, area, present);
            foreach (var slot in slots)
            {
                if (!columns.TryGetValue(slot[0], out int column)) continue;
                foreach (string card in slot) (column == 1 ? right : left).Add(card);
            }
            Store(_left, area, left);
            Store(_right, area, right);
        }

        private static HashSet<string> Kept(Dictionary<string, HashSet<string>> flags, string area, HashSet<string> present)
            => flags.TryGetValue(area, out var old)
                ? new HashSet<string>(old.Where(id => !present.Contains(id)), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

        private static void Store(Dictionary<string, HashSet<string>> flags, string area, HashSet<string> ids)
        {
            if (ids.Count > 0) flags[area] = ids;
            else flags.Remove(area);
        }

        /// <summary>The cards that exist now, in the user's slots.</summary>
        internal List<List<string>> Slots(string area, IEnumerable<string> present)
        {
            var now = present.ToList();
            var here = new HashSet<string>(now, StringComparer.Ordinal);
            var slots = new List<List<string>>();
            if (_order.TryGetValue(area, out var saved))
            {
                // Slots are found from the saved order with the missing cards still in it, so a
                // card stacked under one that is gone stands alone rather than joining the slot
                // before.
                List<string>? slot = null;
                foreach (string id in saved)
                {
                    if (slot == null || !IsStacked(area, id)) slots.Add(slot = []);
                    if (here.Contains(id)) slot.Add(id);
                }
                slots.RemoveAll(s => s.Count == 0);
            }
            var placed = new HashSet<string>(slots.SelectMany(s => s), StringComparer.Ordinal);
            foreach (string id in now) if (placed.Add(id)) slots.Add([id]);
            return slots;
        }

        /// <summary>Moves <paramref name="id"/> as <paramref name="drop"/> says, relative to the
        /// slot holding <paramref name="target"/>, and remembers the result.</summary>
        internal List<List<string>> Move(string area, IReadOnlyList<IReadOnlyList<string>> shown, string id, string target, Drop drop)
        {
            var slots = Rearranged(shown, id, target, drop);
            if (!Same(slots, shown)) Remember(area, slots);
            return slots;
        }

        /// <summary>The slots a drop would make, without remembering them: the same as
        /// <paramref name="shown"/> when the card would land where it already is.</summary>
        internal static List<List<string>> Rearranged(IReadOnlyList<IReadOnlyList<string>> shown, string id, string target, Drop drop)
        {
            var slots = shown.Select(slot => slot.ToList()).ToList();
            bool present = slots.Any(slot => slot.Contains(id)), there = slots.Any(slot => slot.Contains(target));
            if (id == target || !present || !there) return slots;

            // Taking the top card of a stack leaves the rest of the stack where it is.
            foreach (var slot in slots) slot.Remove(id);
            slots.RemoveAll(slot => slot.Count == 0);
            int at = slots.FindIndex(slot => slot.Contains(target));
            switch (drop)
            {
                case Drop.Before: slots.Insert(at, [id]); break;
                case Drop.After: slots.Insert(at + 1, [id]); break;
                default: slots[at].Add(id); break;
            }
            return slots;
        }

        internal static bool Same(IReadOnlyList<IReadOnlyList<string>> a, IReadOnlyList<IReadOnlyList<string>> b)
            => a.Count == b.Count && a.Zip(b, (x, y) => x.SequenceEqual(y)).All(same => same);

        /// <summary>
        /// Writes the new order over the saved one slot by slot, so a card that is missing right
        /// now (an unplugged adapter) keeps its saved place among the others rather than being
        /// pushed to the end by a rearrangement made while it was away.
        /// </summary>
        private void Remember(string area, List<List<string>> slots)
        {
            var shown = slots.SelectMany(slot => slot).ToList();
            var present = new HashSet<string>(shown, StringComparer.Ordinal);
            var merged = new List<string>(shown.Count);
            int next = 0;
            if (_order.TryGetValue(area, out var saved))
                foreach (string id in saved) merged.Add(present.Contains(id) ? shown[next++] : id);
            while (next < shown.Count) merged.Add(shown[next++]);
            _order[area] = merged;

            // A card that is gone keeps whether it was stacked; every card shown is stacked
            // exactly when it is not the top of its slot.
            var stacked = _stacked.TryGetValue(area, out var old)
                ? new HashSet<string>(old.Where(id => !present.Contains(id)), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            foreach (var slot in slots) foreach (string id in slot.Skip(1)) stacked.Add(id);
            if (stacked.Count > 0) _stacked[area] = stacked;
            else _stacked.Remove(area);
        }

        private bool IsStacked(string area, string id)
            => _stacked.TryGetValue(area, out var stacked) && stacked.Contains(id);

        internal bool IsFolded(string area, string id) => Has(_folded, area, id);
        internal void SetFolded(string area, string id, bool folded) => Set(_folded, area, id, folded);

        /// <summary>A card that spans both columns rather than one of them.</summary>
        internal bool IsWide(string area, string id) => Has(_wide, area, id);
        internal void SetWide(string area, string id, bool wide) => Set(_wide, area, id, wide);

        private static bool Has(Dictionary<string, HashSet<string>> flags, string area, string id)
            => flags.TryGetValue(area, out var set) && set.Contains(id);

        // An area with nothing flagged is dropped, so a layout put back by hand reads as the default.
        private static void Set(Dictionary<string, HashSet<string>> flags, string area, string id, bool on)
        {
            if (on)
            {
                if (!flags.TryGetValue(area, out var set)) flags[area] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(id);
            }
            else if (flags.TryGetValue(area, out var set) && set.Remove(id) && set.Count == 0)
            {
                flags.Remove(area);
            }
        }

        internal void Reset()
        {
            _order.Clear();
            _stacked.Clear();
            _folded.Clear();
            _wide.Clear();
            _left.Clear();
            _right.Clear();
        }

        /// <summary>
        /// The slots in bands, in order. A slot that spans both columns is a band of one column to
        /// itself. Between those, the slots go down a left and a right column that each run on
        /// their own, so a slot sits right under the one above it in its column rather than
        /// waiting for a taller slot in the other column to end. A slot goes to the column
        /// <paramref name="column"/> remembers for it, and one with none to the shorter column of
        /// its band, which alternates them. A lone slot before a spanning one keeps the left
        /// column of its band rather than stretching across it.
        /// </summary>
        internal static List<List<T>[]> Bands<T>(IReadOnlyList<T> slots, Func<T, bool> wide, Func<T, int?>? column = null)
        {
            var bands = new List<List<T>[]>();
            List<T>[]? columns = null;
            foreach (var slot in slots)
            {
                if (wide(slot))
                {
                    bands.Add([[slot]]);
                    columns = null;
                    continue;
                }
                if (columns == null)
                {
                    columns = [[], []];
                    bands.Add(columns);
                }
                int side = column?.Invoke(slot) ?? (columns[0].Count <= columns[1].Count ? 0 : 1);
                columns[side == 1 ? 1 : 0].Add(slot);
            }
            return bands;
        }

        // ── The setting ─────────────────────────────────────────
        // "order.panels=cpu,ram,disks;stacked.panels=ram;folded.strip=gpu", every id escaped so an
        // adapter name with a comma, semicolon or equals sign in it cannot split the line.
        internal string Serialize()
        {
            var parts = new List<string>();
            Write(parts, "order.", _order.Select(area => (area.Key, (IEnumerable<string>)area.Value)));
            Write(parts, "stacked.", _stacked.Select(area => (area.Key, (IEnumerable<string>)area.Value)));
            Write(parts, "folded.", _folded.Select(area => (area.Key, (IEnumerable<string>)area.Value)));
            Write(parts, "wide.", _wide.Select(area => (area.Key, (IEnumerable<string>)area.Value)));
            Write(parts, "left.", _left.Select(area => (area.Key, (IEnumerable<string>)area.Value)));
            Write(parts, "right.", _right.Select(area => (area.Key, (IEnumerable<string>)area.Value)));
            return string.Join(";", parts);
        }

        private static void Write(List<string> parts, string prefix, IEnumerable<(string Area, IEnumerable<string> Ids)> areas)
        {
            foreach (var (area, ids) in areas)
                parts.Add(prefix + area + "=" + string.Join(",", ids.Select(Uri.EscapeDataString)));
        }

        /// <summary>Anything it cannot read is left out, so a damaged setting falls back to the
        /// designed layout instead of failing the tab.</summary>
        internal static DashboardLayout Parse(string? saved)
        {
            var layout = new DashboardLayout();
            if (string.IsNullOrEmpty(saved)) return layout;
            foreach (string part in saved!.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq);
                var ids = part.Substring(eq + 1).Split(',')
                    .Where(id => id.Length > 0).Select(Uri.UnescapeDataString).Distinct(StringComparer.Ordinal).ToList();
                if (ids.Count == 0) continue;
                if (Area(key, "order.") is { } order) layout._order[order] = ids;
                else if (Area(key, "stacked.") is { } stacked) layout._stacked[stacked] = new HashSet<string>(ids, StringComparer.Ordinal);
                else if (Area(key, "folded.") is { } folded) layout._folded[folded] = new HashSet<string>(ids, StringComparer.Ordinal);
                else if (Area(key, "wide.") is { } wide) layout._wide[wide] = new HashSet<string>(ids, StringComparer.Ordinal);
                else if (Area(key, "left.") is { } left) layout._left[left] = new HashSet<string>(ids, StringComparer.Ordinal);
                else if (Area(key, "right.") is { } right) layout._right[right] = new HashSet<string>(ids, StringComparer.Ordinal);
            }
            return layout;
        }

        private static string? Area(string key, string prefix)
            => key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length ? key.Substring(prefix.Length) : null;
    }
}
