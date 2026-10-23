using System;
using System.Collections.Generic;
using System.Linq;

// The Performance Monitor's arrangement as the user left it: the order its cards were dragged
// into, which cards are stacked in the gap under another, and which are folded to their title
// line, for the top strip and for the panels below it separately. One setting holds all of it, so
// it survives restarts, and Reset layout clears it back to the designed dashboard.
//
// A slot is one column of a row: usually one card, or a card with others stacked under it in the
// room a minimized card leaves. Cards are named by a stable id ("cpu", "disks", "gpu:<adapter
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

        internal bool IsDefault => _order.Count == 0 && _stacked.Count == 0 && _folded.Count == 0;

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

        internal bool IsFolded(string area, string id)
            => _folded.TryGetValue(area, out var folded) && folded.Contains(id);

        internal void SetFolded(string area, string id, bool folded)
        {
            if (folded)
            {
                if (!_folded.TryGetValue(area, out var set)) _folded[area] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(id);
            }
            else if (_folded.TryGetValue(area, out var set) && set.Remove(id) && set.Count == 0)
            {
                _folded.Remove(area);
            }
        }

        internal void Reset()
        {
            _order.Clear();
            _stacked.Clear();
            _folded.Clear();
        }

        /// <summary>Two slots to a row, in order; an odd last slot has its row to itself.</summary>
        internal static List<T[]> Rows<T>(IReadOnlyList<T> slots)
        {
            var rows = new List<T[]>((slots.Count + 1) / 2);
            for (int i = 0; i < slots.Count; i += 2)
                rows.Add(i + 1 < slots.Count ? [slots[i], slots[i + 1]] : [slots[i]]);
            return rows;
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
            }
            return layout;
        }

        private static string? Area(string key, string prefix)
            => key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length ? key.Substring(prefix.Length) : null;
    }
}
