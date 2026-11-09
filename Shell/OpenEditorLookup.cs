using System;
using System.Collections.Generic;
using System.Linq;

namespace KillerShell.Shell
{
    /// <summary>
    /// Finds the editor tab that already has a file open, in any pane. A hidden second pane
    /// keeps its tabs, so it is searched too, after the panes on screen.
    /// </summary>
    internal static class OpenEditorLookup
    {
        internal static (TPane Pane, TTab Tab)? Find<TPane, TTab>(IEnumerable<TPane> panes, Func<TPane, bool> shown,
            Func<TPane, IEnumerable<TTab>> tabs, Func<TTab, string?> pathOf, string path)
        {
            var ordered = panes.ToList();
            foreach (var pane in ordered.Where(shown).Concat(ordered.Where(p => !shown(p))))
                foreach (var tab in tabs(pane))
                    if (string.Equals(pathOf(tab), path, StringComparison.OrdinalIgnoreCase))
                        return (pane, tab);
            return null;
        }
    }
}
