using KillerShell.Models;

// Which bar a pane wears. Partial of MainWindow.
//
// Three kinds of tab, three bars, and only ever one of them up:
//
//   listing   LocationRow      back / forward / up / star / address / view / sort / filter
//   shell     TerminalBar      where the shell is, plus the shell verbs   (TerminalBar.cs)
//   document  the editor bar   save / undo / redo / find / go to / wrap / gear   (EditorBar.cs)
//
// They used to be one row for all three, with the listing tools hidden on a shell tab. That was
// fine while a shell was the only other kind, because a shell does have a working directory and
// the address row could just about carry it. A DOCUMENT has nothing the row can say: back and
// forward have no history to walk, up has nowhere to go, the star saves a folder you are not
// looking at, and the view and sort buttons act on a list that is not on screen. Chrome you have
// to read past to reach the two controls you wanted is worse than no chrome.
//
// The shell and document bars live inside their own hosts (FilePane.xaml), so they appear and
// disappear with the thing they belong to. The only decision left here is the location row.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        /// <summary>
        /// Show the location row only on a listing tab. Called from ActivateTab.
        /// </summary>
        /// <remarks>
        /// F10's per-pane hide still wins (MenuBar.cs): a pane whose row the user has put away
        /// keeps it away when they switch back to a folder tab, rather than having it handed
        /// back by a tab switch they did not think of as a request for chrome.
        ///
        /// The animated path is deliberately not used. F10 slides because the row is the thing
        /// you are looking at when you press it; a tab switch replaces the whole pane at once,
        /// and a row sliding shut underneath that reads as lag rather than as motion.
        /// </remarks>
        private void ApplyPaneBars(SearchTab t)
        {
            bool listing = !t.IsTerminal && !t.IsEditor;
            SetLocationRow(Pane, hidden: !listing || Pane.MenuBarHidden, animate: false);
        }
    }
}
