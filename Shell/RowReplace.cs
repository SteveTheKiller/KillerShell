using System;
using System.Collections;
using System.Collections.Generic;

namespace KillerShell.Shell
{
    /// <summary>
    /// Swaps a listing row for a fresh copy (new size or date) without losing the selection.
    /// A list control drops a replaced item from its selection, so the fresh row is selected
    /// again when the old one was.
    /// </summary>
    internal static class RowReplace
    {
        /// <returns>True when the row was found and replaced.</returns>
        internal static bool Replace<T>(IList<T> rows, IList? selected, T old, T fresh, Action? onReplaced = null)
        {
            int at = rows.IndexOf(old);
            if (at < 0) return false;
            bool wasSelected = selected != null && selected.Contains(old);
            rows[at] = fresh;
            onReplaced?.Invoke();
            if (wasSelected)
            {
                selected!.Remove(old);
                if (!selected.Contains(fresh)) selected.Add(fresh);
            }
            return true;
        }
    }
}
