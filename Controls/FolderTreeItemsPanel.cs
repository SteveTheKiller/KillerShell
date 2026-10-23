using System.Windows.Controls;

namespace KillerShell.Controls
{
    public sealed class FolderTreeItemsPanel : VirtualizingStackPanel
    {
        // .NET Framework exposes this only to subclasses. Reveal a single row without
        // creating containers for every preceding sibling.
        internal void Reveal(int index) => BringIndexIntoView(index);
    }
}
