using System;
using System.Collections.Generic;
using System.Linq;

// The Performance Monitor's arrangement as the user left it: the order its cards were dragged
// into and which ones are folded to their title line, for the top strip and for the panels
// below it separately. One setting holds all of it, so it survives restarts, and Reset layout
// clears it back to the designed dashboard.
//
// Cards are named by a stable id ("cpu", "disks", "gpu:<adapter name>") rather than by position,
// so a saved layout still fits when hardware comes and goes: a card that is gone is skipped and
// keeps its saved place for when it returns, and a card that is new joins at the end.
namespace KillerShell.Services
{
    internal sealed class DashboardLayout
    {
        internal const string Strip = "strip";
        internal const string Panels = "panels";

        private readonly Dictionary<string, List<string>> _order = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _folded = new(StringComparer.Ordinal);

        internal bool IsDefault => _order.Count == 0 && _folded.Count == 0;

        /// <summary>The cards that exist now, in the user's order.</summary>
        internal List<string> Arrange(string area, IEnumerable<string> present)
        {
            var now = present.ToList();
            if (!_order.TryGetValue(area, out var saved)) return now;
            var arranged = saved.Where(now.Contains).ToList();
            foreach (string id in now) if (!arranged.Contains(id)) arranged.Add(id);
            return arranged;
        }

        /// <summary>Moves <paramref name="id"/> to just before or after <paramref name="target"/>
        /// in the order the area <paramref name="shown"/> now, and remembers the result.</summary>
        internal List<string> Move(string area, IReadOnlyList<string> shown, string id, string target, bool after)
        {
            var order = Reordered(shown, id, target, after);
            if (!order.SequenceEqual(shown)) Remember(area, order);
            return order;
        }

        /// <summary>The order a drop would make, without remembering it: equal to
        /// <paramref name="shown"/> when the card would land where it already is.</summary>
        internal static List<string> Reordered(IReadOnlyList<string> shown, string id, string target, bool after)
        {
            if (id == target || !shown.Contains(id) || !shown.Contains(target)) return [.. shown];
            var order = shown.Where(card => card != id).ToList();
            order.Insert(order.IndexOf(target) + (after ? 1 : 0), id);
            return order;
        }

        /// <summary>
        /// Writes the new order over the saved one slot by slot, so a card that is missing right
        /// now (an unplugged adapter) keeps its saved slot among the others rather than being
        /// pushed to the end by a rearrangement made while it was away.
        /// </summary>
        private void Remember(string area, List<string> shown)
        {
            var present = new HashSet<string>(shown, StringComparer.Ordinal);
            var merged = new List<string>(shown.Count);
            int next = 0;
            if (_order.TryGetValue(area, out var saved))
                foreach (string id in saved) merged.Add(present.Contains(id) ? shown[next++] : id);
            while (next < shown.Count) merged.Add(shown[next++]);
            _order[area] = merged;
        }

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
            _folded.Clear();
        }

        /// <summary>Two cards to a row, in order; an odd last card has its row to itself.</summary>
        internal static List<string[]> Rows(IReadOnlyList<string> order)
        {
            var rows = new List<string[]>((order.Count + 1) / 2);
            for (int i = 0; i < order.Count; i += 2)
                rows.Add(i + 1 < order.Count ? [order[i], order[i + 1]] : [order[i]]);
            return rows;
        }

        // ── The setting ─────────────────────────────────────────
        // "order.panels=cpu,ram,disks;folded.strip=gpu", every id escaped so an adapter name with
        // a comma, semicolon or equals sign in it cannot split the line.
        internal string Serialize()
        {
            var parts = new List<string>();
            foreach (var area in _order)
                parts.Add("order." + area.Key + "=" + string.Join(",", area.Value.Select(Uri.EscapeDataString)));
            foreach (var area in _folded)
                parts.Add("folded." + area.Key + "=" + string.Join(",", area.Value.Select(Uri.EscapeDataString)));
            return string.Join(";", parts);
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
                if (key.StartsWith("order.", StringComparison.Ordinal) && key.Length > 6)
                    layout._order[key.Substring(6)] = ids;
                else if (key.StartsWith("folded.", StringComparison.Ordinal) && key.Length > 7)
                    layout._folded[key.Substring(7)] = new HashSet<string>(ids, StringComparer.Ordinal);
            }
            return layout;
        }
    }
}
