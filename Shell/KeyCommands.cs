using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell.Models;   // SearchResult - the selected row the nickname keys act on

// Keyboard routes for commands that used to be mouse-only. Partial of MainWindow.
//
// Window_PreviewKeyDown calls HandleCommandKey after its applet handover guards, so a focused
// shell, document or tool tab still keeps every key that is not a window chord, and before its
// own chain, so a focused folder tree can claim the results menu's keys for its own selection.
//
// Every key here is also on the F1 card (KsAll, ShortcutsOverlay.cs), on the menu row that runs
// the same command (InputGestureText), and in the tooltip of the button that runs it.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        // Ctrl+Shift +/- steps the app size by this much, the same step KillerNotes and
        // KillerScan use. The wheel over the wordmark keeps its finer AppScaleStep.
        private const double AppScaleKeyStep = 0.05;

        /// <summary>
        /// Runs the command bound to this key, if there is one. True when the key was used.
        /// </summary>
        private bool HandleCommandKey(KeyEventArgs e, bool ctrl, bool shift, bool alt)
        {
            // Alt chords and F10 arrive as Key.System with the real key in SystemKey.
            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            // The sort flyout is a Popup declared in the pane, so its keys tunnel through this
            // window handler first. Enter has to pick the focused row rather than run a search,
            // and Esc has to close the flyout rather than offer to quit.
            if (Pane.SortPopup.IsOpen)
            {
                if (key == Key.Escape)
                {
                    Pane.SortPopup.IsOpen = false;
                    e.Handled = true;
                    return true;
                }
                if (key == Key.Enter && Keyboard.FocusedElement is Button row)
                {
                    row.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, row));
                    e.Handled = true;
                    return true;
                }
            }

            if (HandleTreeKey(key, ctrl, shift, alt)) { e.Handled = true; return true; }

            // ── Window ───────────────────────────────────────────
            if (ctrl && shift && !alt)
            {
                switch (key)
                {
                    // App size, the family chord (KillerPDF, KillerNotes, KillerScan). The wheel
                    // over the wordmark drives the same scale.
                    case Key.OemPlus: case Key.Add:
                        ApplyAppScale(_appScale + AppScaleKeyStep, persist: true);   // AppScale.cs
                        e.Handled = true; return true;
                    case Key.OemMinus: case Key.Subtract:
                        ApplyAppScale(_appScale - AppScaleKeyStep, persist: true);
                        e.Handled = true; return true;
                    case Key.D0: case Key.NumPad0:
                        ApplyAppScale(1.0, persist: true);
                        e.Handled = true; return true;
                    case Key.B:
                        ToggleTreePanel();   // TreePanel.cs
                        e.Handled = true; return true;
                }
            }

            if (alt && !ctrl && !shift)
            {
                switch (key)
                {
                    case Key.T:
                        ThemeButton_Click(this, new RoutedEventArgs());   // ThemeFlyout.cs
                        e.Handled = true; return true;
                    case Key.L:
                        LangButton_Click(this, new RoutedEventArgs());    // Language.cs
                        e.Handled = true; return true;
                    case Key.H:
                        ToggleTreeHidden();   // FolderTree.cs
                        e.Handled = true; return true;
                }
            }

            // Alt+Shift+G, the family Film Grain chord.
            if (alt && shift && !ctrl && key == Key.G)
            {
                FilmGrainCheck.IsChecked = !Services.FilmGrain.Enabled;
                FilmGrainCheck_Click(this, new RoutedEventArgs()); // ThemeFlyout.cs
                e.Handled = true; return true;
            }

            // Alt+Z wraps the document while one is showing, and compresses the selection on a
            // file tab. The two can never both apply, so they share the key.
            if (alt && !ctrl && !shift && key == Key.Z && _active?.Editor != null)
            {
                EditorWrap_Click(this, new RoutedEventArgs());   // EditorBar.cs
                e.Handled = true;
                return true;
            }

            // ── File browser ─────────────────────────────────────
            // Only while the tab is showing a listing: these mean nothing on a shell, a document
            // or a tool tab, and those keep the key.
            if (_active == null || !CanShowListing(_active)) return false;   // Bookmarks.cs

            if (ctrl && shift && !alt)
            {
                switch (key)
                {
                    case Key.D1: SetResultsView(0); e.Handled = true; return true;   // ResultsView.cs
                    case Key.D2: SetResultsView(1); e.Handled = true; return true;
                    case Key.D3: SetResultsView(2); e.Handled = true; return true;
                    case Key.D4: Density_Click(this, new RoutedEventArgs()); e.Handled = true; return true;   // Density.cs
                }
            }

            if (ctrl && !shift && !alt && e.OriginalSource is not TextBox)
            {
                // The Ctrl+wheel ladder: icon size, then the next view at either end.
                if (key is Key.OemPlus or Key.Add)      { StepResultsSize(+1); e.Handled = true; return true; }
                if (key is Key.OemMinus or Key.Subtract) { StepResultsSize(-1); e.Handled = true; return true; }
            }

            if (alt && !ctrl)
            {
                switch (key)
                {
                    case Key.O when !shift:
                        FoldersTop_Click(this, new RoutedEventArgs());   // ViewOptions.cs
                        e.Handled = true; return true;
                    case Key.S when !shift:
                        OpenSortMenuFromKeyboard();
                        e.Handled = true; return true;
                    case Key.S:
                        SortDir_Click(this, new RoutedEventArgs());   // Results.cs
                        e.Handled = true; return true;
                    case Key.R when !shift:
                        Recents_Click(Pane);   // Recents.cs
                        e.Handled = true; return true;
                }
            }

            // The rest act on the selected rows, so they need the list to hold the keyboard.
            if (!ResultsListHasFocus()) return false;   // ResultsMenu.cs

            if (shift && !ctrl && !alt && key == Key.F4)
            {
                FromKeyboard(MenuAnalyze_Click);   // ResultsMenu.cs
                e.Handled = true;
                return true;
            }

            if (alt && !ctrl)
            {
                switch (key)
                {
                    case Key.N:
                        // The nickname rows act on the row the menu opened on, so the selection
                        // stands in for it here.
                        _menuSeed = Pane.ResultsList.SelectedItem as SearchResult;
                        if (_menuSeed == null) return false;
                        if (shift) MenuNicknameRemove_Click(this, new RoutedEventArgs());   // NicknameCommands.cs
                        else       MenuNickname_Click(this, new RoutedEventArgs());
                        _menuSeed = null;
                        e.Handled = true; return true;
                    case Key.Z when !shift:
                        FromKeyboard(MenuCompress_Click);      // ArchiveEdit.cs
                        e.Handled = true; return true;
                    case Key.X:
                        FromKeyboard(shift ? MenuExtractTo_Click : MenuExtractHere_Click);
                        e.Handled = true; return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The folder tree's menu verbs, on the selected node while the tree holds the keyboard.
        /// The keys are the ones the results menu uses for the same verbs, so a hand does not
        /// have to learn a second set for the tree.
        /// </summary>
        private bool HandleTreeKey(Key key, bool ctrl, bool shift, bool alt)
        {
            if (!FolderTree.IsKeyboardFocusWithin) return false;
            if (FolderTree.SelectedItem is not FolderNode node || node.IsGroup) return false;

            Action<object, RoutedEventArgs>? cmd = null;
            if (!ctrl && !shift && !alt)
            {
                if (key == Key.F6) cmd = TreeShowInExplorer_Click;
                else if (key == Key.F3) cmd = TreeSearchHere_Click;
                else if (key == Key.F8) cmd = TreeTerminal_Click;
            }
            else if (ctrl && !shift && !alt)
            {
                if (key == Key.F8) cmd = TreeTerminalAdmin_Click;
                else if (key == Key.D) cmd = TreeFavorite_Click;
                else if (key == Key.Enter) cmd = TreeOpenNewTab_Click;
            }
            else if (shift && !ctrl && !alt)
            {
                if (key == Key.F4) cmd = TreeAnalyze_Click;
                else if (key == Key.F10) cmd = TreeShellMenu_Click;
            }
            else if (ctrl && shift && !alt)
            {
                if (key == Key.C) cmd = TreeCopyPath_Click;
            }
            else if (alt && !ctrl)
            {
                if (key == Key.Enter && !shift) cmd = TreeProperties_Click;
                else if (key == Key.N) cmd = shift ? TreeNicknameRemove_Click : TreeNickname_Click;
            }

            if (cmd == null) return false;
            _treeMenuNode = node;   // FolderTree.cs - what the menu rows act on
            cmd(this, new RoutedEventArgs());
            return true;
        }

        /// <summary>
        /// Opens the sort flyout and puts the keyboard on the current sort key, so the arrows and
        /// Enter can pick another one without the mouse.
        /// </summary>
        private void OpenSortMenuFromKeyboard()
        {
            SortMenu_Click(this, new RoutedEventArgs());   // Results.cs
            if (!Pane.SortPopup.IsOpen) return;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, (Action)(() =>
            {
                Button? on = null;
                foreach (var b in new[] { Pane.SortFoundItem, Pane.SortNameItem, Pane.SortFolderItem,
                                          Pane.SortSizeItem, Pane.SortModifiedItem, Pane.SortTypeItem,
                                          Pane.SortCreatedItem })
                    if (b?.Tag as string == "on") { on = b; break; }
                (on ?? Pane.SortNameItem)?.Focus();
            }));
        }
    }
}
