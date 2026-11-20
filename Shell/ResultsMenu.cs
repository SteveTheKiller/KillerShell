using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell.Models;

namespace KillerShell.Shell
{
    // The results context menu. Partial of MainWindow.
    //
    // One menu serves all three views, declared once as a resource, and none of its items carry a
    // per-item Tag binding. Instead the right-click records what was under the pointer and every
    // command asks FilesForCommand (ResultsInteraction.cs) what to act on. That is what lets a
    // command work on a whole selection - "copy 12 paths" - which a Tag binding cannot express,
    // and it keeps one menu definition instead of three that drift apart.
    public partial class MainWindow
    {
        private SearchResult? _menuSeed;
        private bool _menuBackground;

        /// <summary>
        /// True when keyboard focus is inside the results list. The file commands and Enter only
        /// take over there - in the search panel or the address bar those keys mean what they
        /// always meant.
        /// </summary>
        private bool ResultsListHasFocus() => Pane.ResultsList.IsKeyboardFocusWithin;

        /// <summary>
        /// Run a context-menu command from the KEYBOARD rather than from the menu.
        ///
        /// Clearing the seed first is the whole point: _menuSeed still holds whatever was last
        /// RIGHT-CLICKED, and FilesForCommand deliberately prefers a seed that sits outside the
        /// selection (that is what makes right-clicking an unselected row act on that row). Left
        /// set, a keyboard command would act on a row that was right-clicked ages ago instead of
        /// on the current selection. Null seed makes FilesForCommand fall through to exactly the
        /// selection, which is what a keypress means.
        /// </summary>
        private void FromKeyboard(Action<object, RoutedEventArgs> cmd)
        {
            _menuSeed = null;
            _menuBackground = false;
            cmd(this, new RoutedEventArgs());
        }

        internal void ResultsList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var item = ItemUnder(e.OriginalSource as System.Windows.DependencyObject);
            _menuSeed = DataFor(item);
            _menuBackground = _menuSeed == null;

            // Right-clicking outside the selection moves the selection there first, so the menu
            // always acts on what you are pointing at. Right-clicking inside it leaves the
            // selection alone, so a multi-file command still sees all of them.
            if (_menuSeed != null && !Pane.ResultsList.SelectedItems.OfType<SearchResult>().Contains(_menuSeed))
            {
                Pane.ResultsList.SelectedItems.Clear();
                Pane.ResultsList.SelectedItems.Add(_menuSeed);
            }
        }

        // The seed again, resolved as the menu opens rather than only on the press. Mouse
        // .DirectlyOver at this instant is the element the menu is opening for, which covers the
        // cases the press handler cannot see - a menu raised from the keyboard, or a press that
        // landed on something the visual walk did not resolve to a container.
        internal void ResultsList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            bool keyboard = e.CursorLeft < 0 && e.CursorTop < 0;
            var seed = keyboard
                ? Pane.ResultsList.SelectedItem as SearchResult
                : DataFor(ItemUnder(Mouse.DirectlyOver as System.Windows.DependencyObject))
                    ?? DataFor(ItemUnder(e.OriginalSource as System.Windows.DependencyObject));
            _menuSeed = seed;
            _menuBackground = seed == null;

            // Same rule as the press handler: pointing outside the selection moves it here,
            // pointing inside it leaves a multi-file selection intact.
            if (_menuSeed != null && !Pane.ResultsList.SelectedItems.OfType<SearchResult>().Contains(_menuSeed))
            {
                Pane.ResultsList.SelectedItems.Clear();
                Pane.ResultsList.SelectedItems.Add(_menuSeed);
            }

            if (Pane.ResultsList.ContextMenu is { } card) FillResultCard(card, seed != null);
        }

        /// <summary>
        /// The file card, the family tile card (Controls/CardMenus.cs): what was right-clicked in the
        /// header, with Delete at its right end for an item, then three-across tiles, most used first.
        /// A tile that does not apply to this selection keeps its place, dimmed. The copy variants and
        /// the archive verbs are flyout tiles. Every tile runs the same handler the old rows ran, so
        /// the selection rules and status messages are unchanged. Built per opening: what applies
        /// depends on the selection, and the card is a handful of buttons.
        /// </summary>
        private void FillResultCard(ContextMenu card, bool onItem)
        {
            Controls.CardMenus.Prepare(card);
            var rows = onItem ? MenuRows() : [];
            bool filesOnly = rows.Count > 0 && rows.All(row => !row.IsDirectory);
            bool lines = filesOnly && rows.Any(row => row.Matches.Any(match => match.Lines.Count > 0));
            bool browsing = _active.IsBrowsing;
            string? folder = MenuFolder();
            bool favorite = IsBookmarked(folder);

            KillerFamily.Controls.CardTile Tile(string id, string glyph, string captionKey, string? shownKey, string? keys,
                Action<object, RoutedEventArgs> handler, bool enabled = true)
                => new(glyph, Loc(captionKey), keys, () => handler(this, new RoutedEventArgs()), false, enabled)
                   { Id = id, Shown = shownKey == null ? null : Loc(shownKey) };

            var header = ResultCardHeader(rows);
            card.Items.Add(onItem
                ? Controls.CardMenus.HeaderSlot(card, header, Loc("Str_Menu_Delete"), "Delete", () => MenuDelete_Click(this, new RoutedEventArgs()))
                : Controls.CardMenus.HeaderSlot(card, header));

            var folderVerbs = new[]
            {
                Tile("explorer", "\uE838", "Str_Menu_ShowExplorer", "Str_Card_Explorer", "F6", MenuShowInExplorer_Click, onItem),
                Tile("terminal", "\uE756", "Str_Menu_OpenTerminal", "Str_Card_Terminal", "F8", MenuTerminal_Click, folder != null),
                Tile("search", "\uE721", "Str_Menu_SearchHere", "Str_Card_Search", "F3", MenuSearchHere_Click, folder != null),
            };
            var folderMore = new[]
            {
                Tile("terminaladmin", "\uE7EF", "Str_Menu_OpenTerminalAdmin", null, "Ctrl+F8", MenuTerminalAdmin_Click, folder != null),
                Tile("analyze", "\uEDA2", "Str_Menu_AnalyzeStorage", null, "Shift+F4", MenuAnalyze_Click, folder != null),
                Tile("exclude", "\uE738", "Str_Menu_ExcludeFolder", null, "Ctrl+Shift+E", MenuExcludeFolder_Click, folder != null),
                Tile("fav", favorite ? "\uE735" : "\uE734", favorite ? "Str_Menu_RemoveFavorite" : "Str_Menu_AddFavorite", null, "Ctrl+D",
                    MenuFavorite_Click, folder != null),
            };
            var create = new[]
            {
                Tile("paste", "\uE77F", "Str_Menu_Paste", "Str_Card_Paste", "Ctrl+V", MenuPaste_Click, browsing),
                Tile("newfolder", "\uE8F4", "Str_Menu_NewFolder", null, "Ctrl+Shift+N", MenuNewFolder_Click, browsing),
            };

            if (!onItem)
            {
                // Empty space in a folder: the card names the folder, and its own verbs lead.
                card.Items.Add(Controls.CardMenus.Tiles(card, create, folderVerbs.Skip(1), folderMore));
                return;
            }

            var copyRows = new List<object>
            {
                Controls.CardMenus.FlyoutRow(Loc("Str_Menu_CopyPath"), "Ctrl+Shift+C", "\uE8C8", MenuCopyPath_Click, "path"),
                Controls.CardMenus.FlyoutRow(Loc("Str_Menu_CopyName"), "Ctrl+Shift+M", "\uE8C8", MenuCopyName_Click, "name"),
                Controls.CardMenus.FlyoutRow(Loc("Str_Menu_CopyFolder"), "Ctrl+Shift+D", "\uE8C8", MenuCopyFolder_Click, "folder"),
            };
            if (lines) copyRows.Add(Controls.CardMenus.FlyoutRow(Loc("Str_Menu_CopyLines"), "Ctrl+Shift+Y", "\uE8C8", MenuCopyLines_Click, "lines"));
            if (filesOnly) copyRows.Add(Controls.CardMenus.FlyoutRow(Loc("Str_Menu_CopyHash"), "Ctrl+Shift+H", "\uE8C8", MenuCopyHash_Click, "hash"));

            // Archive verbs (ArchiveEdit.cs) where they do something: Compress for real files and
            // folders, Extract when every selected row is a readable archive. The rows name the
            // archive or folder they will create, so each says where things land.
            var archiveRows = new List<object>();
            var archives = MenuArchives();
            var diskItems = MenuDiskItems();
            if (diskItems.Count > 0)
                foreach (var (tag, extension, keys) in new[] { ("zip", ".zip", "Alt+Z"), ("tar", ".tar", (string?)null), ("targz", ".tar.gz", null) })
                    archiveRows.Add(Controls.CardMenus.FlyoutRow(string.Format(Loc("Str_Menu_AddToArchive"), ArchiveOutputName(diskItems[0], extension)),
                        keys, "\uF012", MenuCompress_Click, tag));
            if (archives.Count > 0 && archives.Count == MenuFiles().Count)
            {
                archiveRows.Add(Controls.CardMenus.FlyoutRow(Loc("Str_Menu_ExtractHere"), "Alt+X", "\uE8B7", MenuExtractHere_Click, "unziphere"));
                string name = archives.Count == 1 ? ArchiveStem(archives[0]) + "\\" : "*\\";
                archiveRows.Add(Controls.CardMenus.FlyoutRow(string.Format(Loc("Str_Menu_ExtractTo"), name), "Alt+Shift+X", "\uE8DA", MenuExtractTo_Click, "unzipto"));
            }

            // A nickname belongs to the one item under the pointer (NicknameCommands.cs).
            string? seedPath = _menuSeed?.FilePath;
            bool nicknamed = !string.IsNullOrEmpty(seedPath) && Services.Nicknames.Get(seedPath!).Length > 0;

            // The tile grid is the costly part of an opening, and it only changes when what applies
            // changes, so a grid built for the same answers is put back instead of built again. The
            // tiles' commands read the selection when they run, so a kept grid acts on the new one.
            string signature = string.Join("|", filesOnly, lines, browsing, folder != null, favorite, nicknamed,
                string.IsNullOrEmpty(seedPath), DemoMode, System.Globalization.CultureInfo.CurrentUICulture.Name,
                Services.ThemeManager.GetSetting("Card.Results.Hidden"),
                string.Join(";", copyRows.OfType<MenuItem>().Select(row => row.Tag)),
                string.Join(";", archiveRows.OfType<MenuItem>().Select(row => row.Tag)));
            if (_resultTiles.TryGetValue(card, out var kept) && kept.Signature == signature)
            {
                // The archive rows name the archive they make, so the kept rows take the new names.
                foreach (var (old, fresh) in kept.ArchiveRows.Zip(archiveRows.OfType<MenuItem>(), (a, b) => (a, b)))
                    old.Header = fresh.Header;
                card.Items.Add(kept.Slot);
                return;
            }
            var tiles = Controls.CardMenus.EditableTiles(card, "Results",
                locked: ["open", "copy"],
                spares: ["openadmin", "paste", "newfolder", "terminaladmin", "analyze", "exclude", "fav", "nick", "nickdel", "archive"],
                [
                    Tile("open", "\uE8E5", "Str_Btn_Open", null, "Enter", MenuOpen_Click),
                    // Edit opens the file in a TAB rather than through its association, which is the
                    // point on a script: opening a .ps1 the normal way runs it (MenuEdit_Click).
                    Tile("edit", "\uE70F", "Str_Menu_Edit", "Str_Card_Edit", "F7", MenuEdit_Click, filesOnly),
                    Tile("openwith", "\uE7AC", "Str_Menu_OpenWith", null, "Ctrl+Shift+O", MenuOpenWith_Click, filesOnly),
                    Tile("openadmin", "\uE7EF", "Str_Menu_OpenAdmin", null, "Ctrl+Shift+Enter", MenuOpenAdmin_Click, filesOnly),
                ],
                [
                    Tile("cut", "\uE8C6", "Str_Menu_Cut", "Str_Card_Cut", "Ctrl+X", MenuCut_Click),
                    Tile("copy", "\uE8C8", "Str_Menu_Copy", "Str_Card_Copy", "Ctrl+C", MenuCopy_Click),
                    Controls.CardMenus.Flyout("copyas", "\uE8C8", Loc("Str_Card_CopyAs"), null, copyRows),
                    Tile("rename", "\uE8AC", "Str_Menu_Rename", "Str_Card_Rename", "F2", MenuRename_Click),
                ],
                create,
                folderVerbs,
                folderMore,
                [
                    Tile("nick", "\uE8EC", nicknamed ? "Str_Menu_ChangeNickname" : "Str_Menu_AddNickname", null, "Alt+N",
                        MenuNickname_Click, !string.IsNullOrEmpty(seedPath)),
                    Tile("nickdel", "\uE738", "Str_Menu_RemoveNickname", null, "Alt+Shift+N", MenuNicknameRemove_Click, nicknamed),
                    Controls.CardMenus.Flyout("archive", "\uF012", Loc("Str_Menu_ArchiveOptions"), null, archiveRows),
                ],
                [
                    Tile("properties", "\uE946", "Str_Menu_Properties", null, "Alt+Enter", MenuProperties_Click),
                    // The real Windows menu needs a real item under the pointer: it opens FOR that item.
                    Tile("shellmenu", "\uE712", "Str_Menu_ShellMenu", null, "Shift+F10", MenuShell_Click, !DemoMode),
                ]);
            _resultTiles.Remove(card);
            _resultTiles.Add(card, new ResultTiles(signature, tiles, archiveRows.OfType<MenuItem>().ToList()));
            card.Items.Add(tiles);
        }

        private sealed record ResultTiles(string Signature, MenuItem Slot, List<MenuItem> ArchiveRows);

        // One kept tile grid per pane's card (FillResultCard).
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ContextMenu, ResultTiles> _resultTiles = new();

        /// <summary>
        /// The card's header: what the menu is about. One row shows its icon, name and type, size and
        /// date; a selection shows how many; empty space names the folder being browsed.
        /// </summary>
        private FrameworkElement ResultCardHeader(List<SearchResult> rows)
        {
            string title, meta;
            System.Windows.Media.ImageSource? icon = null;
            if (rows.Count == 1)
            {
                var row = rows[0];
                title = string.IsNullOrEmpty(row.FileName) ? Path.GetFileName(row.FilePath) : row.FileName;
                meta = string.Join(" · ", new[] { row.TypeName, row.IsDirectory ? string.Empty : row.SizeLabel, row.ModifiedLabel }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));
                icon = row.Icon;
            }
            else if (rows.Count > 1)
            {
                // No icon: the first row's would read as if the card were about that one file.
                int folders = rows.Count(row => row.IsDirectory);
                title = string.Format(Loc("Str_Status_Selected"), rows.Count.ToString("N0"));
                meta = string.Format(Loc("Str_Card_FilesFolders"), (rows.Count - folders).ToString("N0"), folders.ToString("N0"));
            }
            else
            {
                // Empty space: the folder being browsed, with its full path under the name.
                string folder = _active.IsBrowsing && !IsThisPc(_active.CurrentFolder) ? _active.CurrentFolder : string.Empty;
                title = _active.Title;
                meta = folder;
                if (folder.Length > 0 && !DemoMode) icon = Services.IconCache.For(folder, 32, true);
            }

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var detail = new TextBlock { Text = meta, FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            text.Children.Add(name);
            if (meta.Length > 0) text.Children.Add(detail);

            var header = new DockPanel { Margin = new Thickness(4, 2, 4, 6) };
            if (icon != null)
            {
                var image = new Image { Source = icon, Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(image, Dock.Left);
                header.Children.Add(image);
            }
            header.Children.Add(text);
            System.Windows.Automation.AutomationProperties.SetName(header, title);

            // Folders only: one folder row, or empty space in a folder being browsed.
            string? starFolder = rows.Count == 1 ? (rows[0].IsDirectory ? rows[0].FilePath : null)
                               : rows.Count == 0 && _active.IsBrowsing && !IsThisPc(_active.CurrentFolder) ? _active.CurrentFolder
                               : null;
            if (CardFavoriteStar(starFolder) is { } star) Controls.CardMenus.AddTrailing(header, star);
            return header;
        }

        // ── Shells ───────────────────────────────────────────────
        // Same folder rule as Favorites below: a shell opens ON the seed when it is a folder and
        // in the PARENT when it is a file, which is what MenuFolder already answers. Right
        // clicking a file and asking for a terminal means "a terminal where this file lives" -
        // nobody wants a shell cd'd into a .txt.
        internal void MenuTerminal_Click(object sender, RoutedEventArgs e)
            => OpenShellAt(MenuFolder(), elevated: false);

        internal void MenuTerminalAdmin_Click(object sender, RoutedEventArgs e)
            => OpenShellAt(MenuFolder(), elevated: true);

        private void OpenShellAt(string? folder, bool elevated)
        {
            OpenShell(elevated ? Terminal.TerminalProfile.PowerShell(true) : Terminal.TerminalProfileStore.ForFolder(folder), folder);
        }

        // Favorites are folders, so this acts on the seed itself when it is one and on the
        // parent when it is a file - which is what MenuFolder already means (Bookmarks.cs).
        internal void MenuFavorite_Click(object sender, RoutedEventArgs e)
        {
            string? folder = MenuFolder();
            if (folder == null) return;

            if (IsBookmarked(folder)) RemoveBookmark(folder);
            else                      AddBookmark(folder);
        }

        private List<SearchResult> MenuRows()
        {
            var rows = Pane.ResultsList.SelectedItems.OfType<SearchResult>().ToList();
            if (_menuSeed != null && !rows.Contains(_menuSeed)) return [_menuSeed];
            return rows.Count > 0 ? rows : _menuSeed != null ? [_menuSeed] : [];
        }

        private List<string> MenuFiles() => MenuRows().Select(row => row.FilePath).ToList();
        private string?      MenuFile()  => MenuFiles().FirstOrDefault();

        // Browsing put folders in this list, and every command below used to guard on
        // File.Exists - which is false for a directory, so right-clicking a folder ran the
        // command and silently did nothing. Anything that works on either kind asks this
        // instead; the few that are genuinely file-only say so out loud rather than no-op.
        private static bool Exists(string? p)
            => !string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p));

        // Clipboard writes fail if another process is holding the clipboard open. Nothing we can
        // do about that, but silently doing nothing is worse than saying so.
        private void PutOnClipboard(string text, string statusKey, params object[] args)
        {
            try
            {
                Clipboard.SetText(text);
                SetTabStatusKey(_active, statusKey, args);
            }
            catch { SetTabStatusKey(_active, "Str_Status_ClipboardBusy"); }
        }

        // ── Open ─────────────────────────────────────────────────
        internal Func<string, Services.ShellOpenResult> OpenAssociatedPath { get; set; }
            = path => Services.ShellOpen.Open(path);

        internal void MenuOpen_Click(object sender, RoutedEventArgs e)
        {
            if (_active.IsBrowsing && !DemoMode)
            {
                var entry = MenuRows().FirstOrDefault();
                if (entry != null) ActivateEntry(entry);
                return;
            }
            if (DemoMode)
            {
                var row = MenuRows().FirstOrDefault();
                if (row == null) return;
                if (row.IsDirectory || Services.DemoArchives.Contains(row.FilePath)) _ = NavigateTo(row.FilePath);
                else OpenForEditing(row.FilePath);
                return;
            }
            string? p = MenuFile();
            if (!Exists(p)) return;

            // A folder opens the way it does everywhere else in the app: you go into it.
            if (Directory.Exists(p) && _active.IsBrowsing)
            {
                _ = NavigateTo(p!);   // Browse.cs
                return;
            }

            ReportShellOpen(_active, OpenAssociatedPath(p!));
        }

        /// <summary>Puts a declined or refused open on the tab's status line.</summary>
        internal void ReportShellOpen(SearchTab t, Services.ShellOpenResult result)
        {
            if (result == Services.ShellOpenResult.Declined) SetTabStatusKey(t, "Str_Status_ElevationDeclined");
            else if (result == Services.ShellOpenResult.Failed) SetTabStatusKey(t, "Str_Status_ShellFailed");
        }

        // Opens the file in an editor TAB (EditorTabs.cs), never through its association. That
        // distinction is the whole point on a .ps1: a script opened through the shell RUNS, and
        // "let me look at this" quietly executing it is not a risk worth taking to save a line.
        // File only - there is nothing to edit about a folder.
        internal void MenuEdit_Click(object sender, RoutedEventArgs e)
        {
            string? p = MenuFile();

            // The existence test is skipped under --demo, where by design nothing in the results
            // is on disk. Without this, Edit and F7 were dead in the one mode built for showing
            // the editor off: every fabricated result failed File.Exists and the command stopped
            // at a status line. OpenForEditing has its own demo branch and never touches disk.
            if (p == null || (!DemoMode && !File.Exists(p)))
            {
                SetTabStatusKey(_active, "Str_Status_FileOnly");
                return;
            }
            OpenForEditing(p);
        }

        internal void MenuOpenWith_Click(object sender, RoutedEventArgs e)
        {
            // OpenAs_RunDLL takes the rest of the command line as the path - no quotes, even
            // when the path has spaces in it. Genuinely file-only: there is no "open a folder
            // with" chooser.
            string? p = MenuFile();
            if (p == null || !File.Exists(p)) { SetTabStatusKey(_active, "Str_Status_FileOnly"); return; }
            System.Diagnostics.Process.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {p}");
        }

        internal void MenuOpenAdmin_Click(object sender, RoutedEventArgs e)
        {
            string? p = MenuFile();
            if (p == null || !File.Exists(p)) { SetTabStatusKey(_active, "Str_Status_FileOnly"); return; }
            // A declined UAC prompt is an answer, not a failure; anything else Windows refused
            // is reported as a refusal rather than as a decline.
            ReportShellOpen(_active, Services.ShellOpen.Open(p, "runas"));
        }

        // /select works for a folder too - it opens the parent with the folder highlighted,
        // which is what "show me where this is" means for either kind.
        internal void MenuShowInExplorer_Click(object sender, RoutedEventArgs e)
        {
            string? p = MenuFile();
            if (Exists(p)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{p}\"");
        }

        // The real Windows properties dialog, not a copy of it: Security, Details, Previous
        // Versions and whatever tabs the installed shell extensions add. Works on folders too.
        //
        // Deferred to after the menu has finished closing. Both shell commands were being called
        // straight out of the Click handler, while the WPF ContextMenu was still dismissing and
        // still holding mouse capture - which is exactly the state the shell cannot open a dialog
        // or track a popup in, so both silently did nothing.
        internal void MenuProperties_Click(object sender, RoutedEventArgs e)
        {
            string? p = MenuFile();
            if (!Exists(p)) return;

            AfterMenuCloses(() =>
            {
                if (!Services.ShellContextMenu.ShowProperties(p!))
                    SetTabStatusKey(_active, "Str_Status_ShellFailed");
            });
        }

        /// <summary>
        /// Runs an action once the context menu is fully closed and has given up capture.
        /// Background priority is late enough that the menu's own teardown has run.
        /// </summary>
        private void AfterMenuCloses(Action work)
            => Dispatcher.BeginInvoke(work, System.Windows.Threading.DispatcherPriority.Background);

        // ── Copy ─────────────────────────────────────────────────
        // All of these act on the whole selection, one entry per line, because pasting twelve
        // paths into a ticket is the actual use.
        internal void MenuCopyPath_Click(object sender, RoutedEventArgs e)
        {
            var files = MenuFiles();
            if (files.Count == 0) return;
            PutOnClipboard(string.Join(Environment.NewLine, files),
                           "Str_Status_Copied", files.Count.ToString("N0"));
        }

        internal void MenuCopyName_Click(object sender, RoutedEventArgs e)
        {
            var names = MenuRows().Select(row =>
            {
                string name = Path.GetFileName(row.FilePath);
                if (!string.IsNullOrEmpty(name)) return name;
                return !string.IsNullOrWhiteSpace(row.FileName) ? row.FileName
                    : row.FilePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }).Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
            if (names.Count == 0) return;
            PutOnClipboard(string.Join(Environment.NewLine, names),
                           "Str_Status_Copied", names.Count.ToString("N0"));
        }

        internal void MenuCopyFolder_Click(object sender, RoutedEventArgs e)
        {
            // Distinct: several results from one folder should not paste that folder five times.
            var folders = MenuRows()
                .Select(row => row.IsDirectory ? row.FilePath : Path.GetDirectoryName(row.FilePath) ?? string.Empty)
                .Where(f => f.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (folders.Count == 0) return;
            PutOnClipboard(string.Join(Environment.NewLine, folders),
                           "Str_Status_Copied", folders.Count.ToString("N0"));
        }

        // Content hits only. A filename match has no lines behind it, so this is empty for one.
        internal void MenuCopyLines_Click(object sender, RoutedEventArgs e)
        {
            var results = Pane.ResultsList.SelectedItems.OfType<SearchResult>().ToList();
            if (results.Count == 0 && _menuSeed != null) results.Add(_menuSeed);

            var sb = new StringBuilder();
            int lines = 0;

            foreach (var r in results)
            {
                var content = r.Matches.Where(m => m.Lines.Count > 0).ToList();
                if (content.Count == 0) continue;

                sb.AppendLine(r.FilePath);
                foreach (var m in content)
                    foreach (var l in m.Lines)
                    {
                        sb.AppendLine($"  {l.LineNumber}: {l.LineText}");
                        lines++;
                    }
                sb.AppendLine();
            }

            if (lines == 0) { SetTabStatusKey(_active, "Str_Status_NoLines"); return; }
            PutOnClipboard(sb.ToString(), "Str_Status_CopiedLines", lines.ToString("N0"));
        }

        // MenuCopyFile_Click lived here: CF_HDROP plus the Preferred DropEffect blob, put on the
        // clipboard so Ctrl+V in Explorer pastes real copies. It was character for character what
        // FileCommands.PutFilesOnClipboard(DragDropEffects.Copy) already does for Ctrl+C - same
        // drop list, same blob, same Str_Status_CopiedFiles - so the menu carried one command
        // under two names. Removed; Copy (Ctrl+C) is the one that stays.

        // ── Hash ─────────────────────────────────────────────────
        // Off the UI thread: this reads the whole file, and results can be gigabytes.
        internal async void MenuCopyHash_Click(object sender, RoutedEventArgs e)
        {
            string? p = MenuFile();
            if (p == null || !File.Exists(p)) { SetTabStatusKey(_active, "Str_Status_FileOnly"); return; }

            var tab = _active;   // the tab that asked, not whichever is in front when hashing ends
            await HashCopy.Run(p,
                file => Task.Run(() =>
                {
                    try
                    {
                        using var sha = SHA256.Create();
                        using var fs  = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536);
                        return (string?)BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", string.Empty);
                    }
                    catch { return null; }
                }),
                (key, args) => SetTabStatusKey(tab, key, args),
                text =>
                {
                    try { Clipboard.SetText(text); return true; }
                    catch { return false; }
                });
        }

        // ── Turning a result into the next search ────────────────
        // Right-clicking a FOLDER means that folder, not the one it sits in. Right-clicking a
        // file means the folder holding it. Getting this backwards for folders would send you up
        // a level, which is the opposite of what the command says.
        private string? MenuFolder()
        {
            if (_menuBackground) return _active.IsBrowsing ? _active.CurrentFolder : null;
            string? p = MenuFile();
            if (p == null) return null;
            if (DemoMode)
                return Services.DemoFs.Has(p) ? p : Path.GetDirectoryName(p);
            if (Directory.Exists(p)) return p;
            string? parent = Path.GetDirectoryName(p);
            return Directory.Exists(parent) ? parent : null;
        }

        /// <summary>Analyze storage for the folder the menu was opened on - the row itself when
        /// it is a folder, otherwise the folder being browsed.</summary>
        internal void MenuAnalyze_Click(object sender, RoutedEventArgs e)
        {
            // MenuFolder() is the same "the row if it is a folder, otherwise the folder being
            // browsed" resolution Search here and Exclude folder already use, so all three rows
            // in this group act on the same target.
            if (MenuFolder() is { } folder) AnalyzeFolder(folder);   // StorageTabs.cs
        }

        internal void MenuSearchHere_Click(object sender, RoutedEventArgs e)
        {
            string? folder = MenuFolder();
            if (folder == null) return;

            ScopeToFolder(folder);   // ResultsInteraction.cs - same path a dropped folder takes
        }

        // Appends the folder NAME rather than its full path, because that is what the exclude
        // syntax matches on: a bare name kills that folder anywhere in the tree, which is the
        // point of excluding bin or node_modules (SearchEngine.IsExcluded).
        internal void MenuExcludeFolder_Click(object sender, RoutedEventArgs e)
        {
            string? folder = MenuFolder();
            if (string.IsNullOrEmpty(folder)) return;

            string name = Path.GetFileName(folder!.TrimEnd(Path.DirectorySeparatorChar));
            if (name.Length == 0) return;

            string current = ExcludePatternsBox.Text.Trim();
            var parts = current.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (parts.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)))
            {
                SetTabStatusKey(_active, "Str_Status_AlreadyExcluded", name);
                return;
            }

            parts.Add(name);
            ExcludePatternsBox.Text  = string.Join(";", parts);
            _active.ExcludePatterns  = ExcludePatternsBox.Text;
            SetTabStatusKey(_active, "Str_Status_Excluded", name);
        }

        // ── The real Windows menu ────────────────────────────────
        internal Func<Window, string[], bool> ShowWindowsMenu { get; set; } = Services.ShellContextMenu.Show;

        internal void MenuShell_Click(object sender, RoutedEventArgs e)
        {
            var files = MenuFiles().ToArray();
            if (files.Length == 0) return;
            if (!Services.ShellContextMenu.SupportsSelection(files))
            {
                SetTabStatusKey(_active, "Str_Status_ShellSelectionNeedsOneFolder");
                return;
            }
            if (files.Any(path => !Exists(path)))
            {
                SetTabStatusKey(_active, "Str_Status_ShellFailed");
                return;
            }

            // TrackPopupMenuEx needs the mouse, and the WPF menu still has it at Click time.
            AfterMenuCloses(() =>
            {
                if (!ShowWindowsMenu(this, files))
                    SetTabStatusKey(_active, "Str_Status_ShellFailed");
            });
        }
    }
}
