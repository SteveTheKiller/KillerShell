using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace KillerShell.Shell
{
    // ═══════════════════════════════════════════════════════════
    //  KEYBOARD SHORTCUTS  -  single source of truth
    // ═══════════════════════════════════════════════════════════
    // ONE table feeds BOTH views on the shortcuts card: the grouped list here, and the visual
    // keyboard in KeyboardMapOverlay.cs. The card used to be 22 hand-written XAML rows, which
    // meant every new binding had to be added in three places and the list drifted the moment
    // one was missed - F12 was in the code and nowhere on the card.
    //
    // Until this table grew a Scope column there were still TWO tables: KsRows here for the
    // list, and KbMap in KeyboardMapOverlay.cs for the keyboard. They had already drifted from
    // each other and from the handler:
    //   * the map lit bare F7 as "add a filter" when both the list and Window_PreviewKeyDown
    //     had moved that to Shift+F7 and given bare F7 to "edit the selected file";
    //   * the map lit Ctrl+Shift+C as case-sensitive matching when that had moved to Alt+C and
    //     the chord had become Windows' Copy as path;
    //   * Ctrl+Shift+F8 (elevated CMD) was on the map and missing from the list entirely.
    // Both views now read KsAll and nothing restates it. That is the Killendar shape - one
    // table, list and map read it, and a key cannot be described two different ways.
    //
    // TWO dimensions, and they are ORTHOGONAL. Do not fold either into the other:
    //
    //   Cat   = what KIND of action it is: Search / Nav / Tabs / View / File / Edit / Help.
    //           A category's color is a theme brush (KsCat* in Themes/*.xaml) shared with the
    //           keyboard's keycap borders and bars, so a group reads the same in both views.
    //           Adding a category costs a KsCat* brush in thirteen themes and a Str_Ks_Cat*
    //           string in ten locale files, which is why the list is short and stays short.
    //
    //   Scope = WHERE the binding applies: everywhere, or inside one applet. This window is
    //           eight applets in one frame and a flat table could not say so - tab-local rows
    //           were filed under whichever global category happened to fit, bare D / M / C read
    //           as global when they only exist in the Storage Analyzer, and a keycap that meant
    //           two different things in two applets had to be left OFF the map rather than
    //           mislabelled. Scope carries no color of its own: it is the section a group of
    //           categories sits in, not a seventh category.
    //
    // Scope is what settles the two collisions that were previously unrepresentable, and it
    // settles them without renaming anything - they simply stop being one binding:
    //   Ctrl+Shift+C : Copy full path in the file browser, Copy in a terminal.
    //   Ctrl+Shift+A : add a search term globally, Run as administrator on a process row,
    //                  Select all in a terminal.
    public partial class MainWindow
    {
        /// <summary>
        /// Which applet a binding belongs to. <see cref="KsScope.Global"/> means the window
        /// itself runs it whatever tab is showing; every other value means the binding only
        /// reaches its handler while that applet owns the keyboard.
        /// </summary>
        /// <remarks>
        /// The Performance Monitor has no keys of its own and so has no member here - it reads
        /// as Global, which is the truth: everything that works on it is the window's.
        /// </remarks>
        private enum KsScope { Global, Files, Terminal, Editor, Processes, Events, Registry, Storage }

        /// <summary>
        /// One binding, as both views read it.
        /// </summary>
        /// <remarks>
        /// <para><c>Keys</c> is the gesture as it should READ on the list, so aliases share a
        /// row ("Ctrl+L / Alt+D") rather than restating the same sentence twice and costing a
        /// second locale key to do it. An EMPTY <c>Keys</c> marks a map-only alias row: it
        /// lights its keycap on another layer and prints nothing on the list, which is how one
        /// action reaches two layers of the board without appearing twice.</para>
        /// <para><c>Caps</c> are keycap ids from KbRows (KeyboardMapOverlay.cs) - several
        /// because one action can own several keys, as Ctrl+X / C / V and Alt+1-0 do. An empty
        /// <c>Caps</c> means the binding is real but not drawable: Ctrl+Alt+E has no Ctrl+Alt
        /// layer to sit on, and the board has no numpad.</para>
        /// </remarks>
        private readonly struct KsBinding(MainWindow.KsScope scope, string cat, string keys,
                                          string label, MainWindow.KbLayer layer, params string[] caps)
        {
            public readonly KsScope  Scope = scope;
            public readonly string   Cat   = cat;     // KsCatOrder value; colors the group
            public readonly string   Keys  = keys;    // reads on the list; "" = map-only alias
            public readonly string   Label = label;   // Str_* resource key
            public readonly KbLayer  Layer = layer;   // which layer of the board lights it
            public readonly string[] Caps  = caps;    // keycap ids to light, may be empty
        }

        // ── THE table ─────────────────────────────────────────────────────────────────────────
        // Mirrors Window_PreviewKeyDown (MainWindow.xaml.cs) plus each applet's own local
        // handler; if a key changes there, change it here.
        private static readonly KsBinding[] KsAll =
        [
            // ══ GLOBAL ══════════════════════════════════════════════════════════════════════
            // The window runs these whatever tab is showing. An applet that owns the keyboard
            // still lets them through - that is what IsWindowChord (TerminalTabs.cs) is for.
            new(KsScope.Global, "Search", "Enter",          "Str_Ks_Run",          KbLayer.Base, "Enter"),
            new(KsScope.Global, "Search", "Ctrl+E",         "Str_Ks_FocusSearch",  KbLayer.Ctrl, "E"),
            new(KsScope.Global, "Search", "Ctrl+Shift+A",   "Str_Ks_AddTerm",      KbLayer.CtrlShift, "A"),
            // Shift+F7, not bare F7: bare F7 edits the selected file and this branch sits ahead
            // of it in the chain, so an unguarded F7 here used to swallow the key outright. The
            // map went on lighting bare F7 with this label long after the handler had stopped.
            new(KsScope.Global, "Search", "Shift+F7",       "Str_Ks_AddFilter",    KbLayer.Shift, "F7"),
            // Alt+C, not Ctrl+Shift+C: case-sensitivity gave that chord up to Windows' own
            // Copy as path. The map still lit it on the Ctrl+Shift layer until this merge.
            new(KsScope.Global, "Search", "Alt+C",          "Str_Ks_CaseSensitive", KbLayer.Alt, "C"),
            new(KsScope.Global, "Search", "Ctrl+F",         "Str_Ks_FilterResults", KbLayer.Ctrl, "F"),
            new(KsScope.Global, "Search", "Ctrl+Shift+F",   "Str_Ks_Pipe",         KbLayer.CtrlShift, "F"),

            new(KsScope.Global, "Nav",    "Alt+Left / Right", "Str_Ks_BackForward", KbLayer.Alt, "Left", "Right"),
            new(KsScope.Global, "Nav",    "Backspace",      "Str_Ks_Back",         KbLayer.Base, "Back"),
            new(KsScope.Global, "Nav",    "Alt+Up",         "Str_Ks_Up",           KbLayer.Alt, "Up"),
            // Bare F4 moved to the Storage Analyzer (BACKLOG.md reserved exactly this handover);
            // the address edit keeps its two other aliases, so nothing was lost.
            new(KsScope.Global, "Nav",    "Ctrl+L / Alt+D", "Str_Ks_Address",      KbLayer.Ctrl, "L"),
            new(KsScope.Global, "Nav",    "",               "Str_Ks_Address",      KbLayer.Alt, "D"),
            new(KsScope.Global, "Nav",    "Ctrl+O",         "Str_Ks_Folder",       KbLayer.Ctrl, "O"),
            new(KsScope.Global, "Nav",    "Ctrl+B",         "Str_Ks_Bookmarks",    KbLayer.Ctrl, "B"),
            new(KsScope.Global, "Nav",    "Alt+1-0",        "Str_Ks_JumpBookmark", KbLayer.Alt,
                                                                                   "D1", "D2", "D3", "D4", "D5",
                                                                                   "D6", "D7", "D8", "D9", "D0"),

            new(KsScope.Global, "Tabs",   "Ctrl+N",         "Str_Ks_NewWindow",    KbLayer.Ctrl, "N"),
            new(KsScope.Global, "Tabs",   "Ctrl+T",         "Str_Ks_NewTab",       KbLayer.Ctrl, "T"),
            new(KsScope.Global, "Tabs",   "Ctrl+W",         "Str_Ks_CloseTab",     KbLayer.Ctrl, "W"),
            new(KsScope.Global, "Tabs",   "Ctrl+Tab",       "Str_Ks_NextTab",      KbLayer.Ctrl, "Tab"),
            // Ctrl+Shift+Tab is the same action backwards, so it lights the same cap on the
            // Ctrl+Shift layer and prints no row of its own.
            new(KsScope.Global, "Tabs",   "",               "Str_Ks_NextTab",      KbLayer.CtrlShift, "Tab"),
            new(KsScope.Global, "Tabs",   "Ctrl+1-9",       "Str_Ks_JumpTab",      KbLayer.Ctrl,
                                                                                   "D1", "D2", "D3", "D4",
                                                                                   "D5", "D6", "D7", "D8", "D9"),

            // Shells open as tabs in the focused pane, so they are grouped with tabs rather
            // than given a category of their own - one more heading for four rows would cost
            // a Str_Ks_Cat key in ten locale files to say the same thing.
            // Ctrl+` and Ctrl+Alt+` are real aliases in the handler (MainWindow.xaml.cs), kept
            // because VS Code and Windows Terminal both train that chord. Listed on the same row
            // rather than their own, the way Ctrl+L / Alt+D already is - a second row would need
            // a second locale key to say the same sentence twice - and carried onto the board by
            // the empty-Keys alias row underneath.
            new(KsScope.Global, "Tabs",   "F8 / Ctrl+`",    "Str_Ks_Shell",        KbLayer.Base, "F8"),
            new(KsScope.Global, "Tabs",   "",               "Str_Ks_Shell",        KbLayer.Ctrl, "Grave"),
            new(KsScope.Global, "Tabs",   "Shift+F8",       "Str_Ks_ShellCmd",     KbLayer.Shift, "F8"),
            new(KsScope.Global, "Tabs",   "Ctrl+F8 / Ctrl+Alt+`", "Str_Ks_ShellAdmin", KbLayer.Ctrl, "F8"),
            // Ctrl+Shift+F8 is the elevated CMD. The handler has always had it (F8 with ctrl for
            // admin and shift for CMD) and the map has always lit it; only the LIST was missing
            // it, which is exactly the drift one table removes.
            new(KsScope.Global, "Tabs",   "Ctrl+Shift+F8",  "Str_Ks_ShellCmdAdmin", KbLayer.CtrlShift, "F8"),

            // F9: moved off F11 - F11 briefly held the Processes tab after Dual Pane moved to
            // plain F10, then F11 was dropped entirely, so it ended up here instead, freeing F9
            // in turn by pushing export onto Ctrl+Alt+E below. Singleton same as the rail icon
            // (TaskManagerRailBtn in MainWindow.xaml, OpenTaskManager in ProcessTabs.cs). Two
            // rows, same shape as F8 / Ctrl+F8 for the shell: Ctrl+F9 relaunches elevated and
            // lands on the same tab (Elevation.cs RelaunchElevatedProcesses). F11, freed up by
            // this move, went to the Performance tab below rather than staying unbound.
            new(KsScope.Global, "Tabs",   "F9",             "Str_Ks_TaskManager",      KbLayer.Base, "F9"),
            new(KsScope.Global, "Tabs",   "Ctrl+F9",        "Str_Ks_TaskManagerAdmin", KbLayer.Ctrl, "F9"),

            // F11: the Performance Monitor tab, singleton same as the rail icon
            // (PerformanceRailBtn in MainWindow.xaml, OpenPerformanceMonitor in
            // PerformanceTabs.cs). No Ctrl+F11 row - unlike Processes/Event Viewer, Performance
            // needs no elevated variant, since every counter it reads is available to an ordinary
            // user account (PerformanceTabs.cs has the full reasoning). BACKLOG.md's reservation
            // note originally described Ctrl+F11 as the elevated variant, written before this tab
            // existed and before that turned out not to be true.
            new(KsScope.Global, "Tabs",   "F11",            "Str_Ks_Performance",  KbLayer.Base, "F11"),

            // F4: the Storage Analyzer tab, singleton same as the rail icon (StorageRailBtn in
            // MainWindow.xaml, OpenStorageAnalyzer in StorageTabs.cs). Same open/admin pair as
            // F9 / Ctrl+F9 - the elevated scan sees folders an ordinary token cannot open
            // (Elevation.cs RelaunchElevatedStorage).
            new(KsScope.Global, "Tabs",   "F4",             "Str_Ks_Storage",      KbLayer.Base, "F4"),
            new(KsScope.Global, "Tabs",   "Ctrl+F4",        "Str_Ks_StorageAdmin", KbLayer.Ctrl, "F4"),

            // Ctrl+F12 ONLY - no bare-F-key row, unlike F9/Ctrl+F9 above, because there is no
            // unelevated way in: bare F12 is locked family-wide to the About card, and the
            // Security log this tab reads refuses to open for a process that is not elevated
            // anyway (EventViewerTabs.cs, Elevation.cs RelaunchElevatedEventViewer). The Base
            // layer's F12 stays About and is untouched.
            new(KsScope.Global, "Tabs",   "Ctrl+F12",       "Str_Ks_EventViewer",  KbLayer.Ctrl, "F12"),

            // Ctrl+F11 ONLY - same shape as Ctrl+F12 above and for the same reason: there is no
            // unelevated way in, and bare F11 stays the Performance tab (Elevation.cs
            // RelaunchElevatedRegistryEditor).
            new(KsScope.Global, "Tabs",   "Ctrl+F11",       "Str_Ks_RegistryEditor", KbLayer.Ctrl, "F11"),

            new(KsScope.Global, "View",   "F5",             "Str_Ks_Refresh",      KbLayer.Base, "F5"),
            new(KsScope.Global, "View",   "Ctrl+F10",       "Str_Ks_MenuBar",      KbLayer.Ctrl, "F10"),
            new(KsScope.Global, "View",   "F10 / Ctrl+Shift+P", "Str_TT_DualPane", KbLayer.Base, "F10"),
            new(KsScope.Global, "View",   "",               "Str_TT_DualPane",     KbLayer.CtrlShift, "P"),
            new(KsScope.Global, "View",   "Ctrl+H",         "Str_TT_ShowHidden",   KbLayer.Ctrl, "H"),
            new(KsScope.Global, "View",   "Alt+P",          "Str_TT_DetailsPane",  KbLayer.Alt, "P"),
            new(KsScope.Global, "View",   "Ctrl+Shift+S",   "Str_Ks_SearchPanel",  KbLayer.CtrlShift, "S"),
            new(KsScope.Global, "View",   "Ctrl+Right",     "Str_Ks_ExpandAll",    KbLayer.Ctrl, "Right"),
            new(KsScope.Global, "View",   "Ctrl+Left",      "Str_Ks_CollapseAll",  KbLayer.Ctrl, "Left"),

            // Ctrl+Alt has no layer on the board and is not worth one for two rows, so these
            // print on the list and light nothing.
            new(KsScope.Global, "File",   "Ctrl+Alt+E",       "Str_Ks_ExportHtml", KbLayer.Base),
            new(KsScope.Global, "File",   "Ctrl+Alt+Shift+E", "Str_Ks_ExportCsv",  KbLayer.Base),
            // Ctrl+comma edits the preferred PowerShell host's $PROFILE (ProfileMenu.cs). It is
            // the window's even from inside a shell, which is why it sits here rather than under
            // Terminal.
            new(KsScope.Global, "File",   "Ctrl+,",         "Str_Prof_Edit",       KbLayer.Ctrl, "Comma"),

            new(KsScope.Global, "Edit",   "Esc",            "Str_Ks_Esc",          KbLayer.Base, "Esc"),

            new(KsScope.Global, "Help",   "F1",             "Str_Ks_Help",         KbLayer.Base, "F1"),
            new(KsScope.Global, "Help",   "F12",            "Str_Ks_About",        KbLayer.Base, "F12"),

            // ══ FILE BROWSER ════════════════════════════════════════════════════════════════
            // These act on the results list and the folder it is showing. They are handled in
            // Window_PreviewKeyDown like the global set, but they mean nothing on a terminal,
            // Processes or Registry tab, so the board stops advertising them there.
            //
            // Enter is deliberately NOT given a keycap here. On a file tab BOTH meanings are
            // live at once - with a selection it opens, with none it runs the search - and a cap
            // can only say one thing, so the always-true global meaning keeps it. The Storage
            // Analyzer's Enter DOES take the cap further down, because on that tab the window
            // stands down entirely and only the zoom meaning is left.
            new(KsScope.Files, "File",   "Enter",           "Str_Menu_OpenFile",   KbLayer.Base),
            // Bare F7 edits the selected file, or opens a blank document with nothing selected.
            new(KsScope.Files, "File",   "F7",              "Str_Menu_Edit",       KbLayer.Base, "F7"),
            new(KsScope.Files, "File",   "Ctrl+F7",         "Str_TT_NewDocAdmin",  KbLayer.Ctrl, "F7"),
            new(KsScope.Files, "File",   "Ctrl+Shift+O",    "Str_Menu_OpenWith",   KbLayer.CtrlShift, "O"),
            new(KsScope.Files, "File",   "Ctrl+Shift+Enter","Str_Menu_OpenAdmin",  KbLayer.CtrlShift, "Enter"),
            new(KsScope.Files, "File",   "F6",              "Str_Menu_ShowExplorer", KbLayer.Base, "F6"),
            new(KsScope.Files, "File",   "F3",              "Str_Menu_SearchHere", KbLayer.Base, "F3"),
            new(KsScope.Files, "File",   "Ctrl+Shift+E",    "Str_Menu_ExcludeFolder", KbLayer.CtrlShift, "E"),
            new(KsScope.Files, "File",   "Ctrl+D",          "Str_Menu_AddFavorite", KbLayer.Ctrl, "D"),
            new(KsScope.Files, "File",   "Alt+Enter",       "Str_Menu_Properties", KbLayer.Alt, "Enter"),
            new(KsScope.Files, "File",   "Shift+F10",       "Str_Menu_ShellMenu",  KbLayer.Shift, "F10"),

            new(KsScope.Files, "Edit",   "Ctrl+A",          "Str_Ks_SelectAll",    KbLayer.Ctrl, "A"),
            new(KsScope.Files, "Edit",   "Ctrl+X / C / V",  "Str_Ks_CutCopyPaste", KbLayer.Ctrl, "X", "C", "V"),
            new(KsScope.Files, "Edit",   "F2",              "Str_Ks_Rename",       KbLayer.Base, "F2"),
            new(KsScope.Files, "Edit",   "Delete",          "Str_Ks_Recycle",      KbLayer.Base, "Del"),
            new(KsScope.Files, "Edit",   "Shift+Delete",    "Str_Ks_DeleteForever", KbLayer.Shift, "Del"),
            new(KsScope.Files, "Edit",   "Ctrl+Shift+N",    "Str_Ks_NewFolder",    KbLayer.CtrlShift, "N"),
            new(KsScope.Files, "Edit",   "Ctrl+Shift+L",    "Str_Ks_Clear",        KbLayer.CtrlShift, "L"),
            // Windows 11's Copy as path. It could not go on the board at all before scope: the
            // list carried this chord AND case-sensitive matching, and a keycap can only say one
            // thing. Case-sensitivity has since moved to Alt+C (above), and the terminal's own
            // Copy is a different scope (below), so all three now coexist.
            new(KsScope.Files, "Edit",   "Ctrl+Shift+C",    "Str_Menu_CopyPath",   KbLayer.CtrlShift, "C"),
            new(KsScope.Files, "Edit",   "Ctrl+Shift+M",    "Str_Menu_CopyName",   KbLayer.CtrlShift, "M"),
            new(KsScope.Files, "Edit",   "Ctrl+Shift+D",    "Str_Menu_CopyFolder", KbLayer.CtrlShift, "D"),
            new(KsScope.Files, "Edit",   "Ctrl+Shift+Y",    "Str_Menu_CopyLines",  KbLayer.CtrlShift, "Y"),
            new(KsScope.Files, "Edit",   "Ctrl+Shift+H",    "Str_Menu_CopyHash",   KbLayer.CtrlShift, "H"),

            // ══ TERMINAL ════════════════════════════════════════════════════════════════════
            // Local to a live shell (Terminal/TerminalControl.cs HandleTerminalChord), reachable
            // only while it has focus - the window hands the keyboard over for everything that
            // is not in IsWindowChord. Only the two chords this scope column was built to settle
            // are tabled; the shell's own line editor bindings (Ctrl+A, Ctrl+E, Ctrl+R and the
            // rest of PSReadLine) belong to the shell, not to this app, and are deliberately not
            // restated here.
            new(KsScope.Terminal, "Edit", "Ctrl+Shift+C",   "Str_Term_Copy",       KbLayer.CtrlShift, "C"),
            new(KsScope.Terminal, "Edit", "Ctrl+Shift+A",   "Str_Term_SelectAll",  KbLayer.CtrlShift, "A"),

            // ══ EDITOR ══════════════════════════════════════════════════════════════════════
            // Reach the document rather than the window (IsEditorChord, EditorTabs.cs): these
            // two are the exceptions the window keeps hold of even while a document has focus.
            new(KsScope.Editor, "Edit",  "Ctrl+G",          "Str_TT_EdGoto",       KbLayer.Ctrl, "G"),
            new(KsScope.Editor, "Edit",  "Ctrl+S",          "Str_TT_EdSave",       KbLayer.Ctrl, "S"),

            // ══ PROCESSES / SERVICES ════════════════════════════════════════════════════════
            // Local to the grid (ProcessListControl.cs Grid_PreviewKeyDown), reachable only
            // while a row is selected there and the grid has focus. Reuses the row context
            // menu's own Str_Menu_* strings, the same "reuse the menu's own label" convention
            // the results rows follow, rather than a parallel Str_Ks_* set that would only
            // restate the same words in ten locale files. Restart and Open file location are one
            // row each because the same key does the same thing in both Processes and Services
            // mode; End/Stop needs two rows since Delete means a different action depending which
            // mode is showing, and Run as administrator/Start are each specific to one mode.
            // The Delete cap shows the Processes-mode label, the mode the tab opens in.
            new(KsScope.Processes, "Edit", "Del",           "Str_Menu_ProcKill",   KbLayer.Base, "Del"),
            new(KsScope.Processes, "Edit", "Del",           "Str_Menu_SvcStop",    KbLayer.Base),
            new(KsScope.Processes, "Edit", "Ctrl+R",        "Str_Menu_ProcRestart", KbLayer.Ctrl, "R"),
            new(KsScope.Processes, "Edit", "Ctrl+O",        "Str_Menu_ProcOpenLocation", KbLayer.Ctrl, "O"),
            // The other half of the Ctrl+Shift+A collision. Nothing was renamed: it is a
            // different scope from the global "add a search term", so both are true at once.
            new(KsScope.Processes, "Edit", "Ctrl+Shift+A",  "Str_Menu_ProcRunAsAdmin", KbLayer.CtrlShift, "A"),
            new(KsScope.Processes, "Edit", "Ctrl+S",        "Str_Menu_SvcStart",   KbLayer.Ctrl, "S"),
            new(KsScope.Processes, "Edit", "Ctrl+.",        "Str_Ks_ProcToggle",   KbLayer.Ctrl, "Period"),

            // ══ EVENT VIEWER ════════════════════════════════════════════════════════════════
            // No rows: the tab has no keys of its own (EventViewerControl.cs has no key handler)
            // and everything that works on it is the global set. The scope is declared anyway so
            // that the day it grows one there is a place to put it, and so KsActiveScope has a
            // value to return for that tab instead of pretending it is the file browser. An empty
            // scope prints no heading on the list at all (BuildShortcutsList skips it) and falls
            // back to Global on the board, which is exactly what is true there.

            // ══ REGISTRY EDITOR ═════════════════════════════════════════════════════════════
            // Local to the tree/grid (RegistryEditorControl.cs PreviewKeyDown), reachable only
            // while the tab has focus, same convention as the Processes rows above: reuse the
            // context menu's own Str_Menu_* strings as labels. F2 and Del are omitted since they
            // duplicate the file browser's F2 (rename) and Del (recycle) rows, and the Registry
            // Editor's own key handlers override those bindings when that tab has focus.
            new(KsScope.Registry, "Search", "Ctrl+F",       "Str_Ks_RegFind",      KbLayer.Ctrl, "F"),
            new(KsScope.Registry, "Edit",   "Ctrl+C",       "Str_Menu_RegCopyPath", KbLayer.Ctrl, "C"),
            new(KsScope.Registry, "Edit",   "Enter",        "Str_Menu_RegModify",  KbLayer.Base, "Enter"),
            new(KsScope.Registry, "View",   "F5",           "Str_TT_RegRefresh",   KbLayer.Base, "F5"),

            // ══ STORAGE ANALYZER ════════════════════════════════════════════════════════════
            // Local to the treemap (StorageAnalyzerControl.OnPreviewKeyDown), reachable only
            // while that tab has focus. These are the rows the flat table read worst: bare
            // D / M / C sat in the global VIEW group and lit as global keycaps, so the board
            // claimed three letters were bound everywhere when they exist on one tab. Enter,
            // Backspace and Home take their caps here rather than sharing the global meaning,
            // because on this tab the window stands down completely and the zoom meaning is the
            // only one left.
            new(KsScope.Storage, "Search", "Ctrl+Enter",    "Str_Ks_StorageScan",  KbLayer.Ctrl, "Enter"),
            new(KsScope.Storage, "View",   "D",             "Str_Ks_StorageDepth", KbLayer.Base, "D"),
            new(KsScope.Storage, "View",   "M",             "Str_Ks_StorageMin",   KbLayer.Base, "M"),
            new(KsScope.Storage, "View",   "C",             "Str_Ks_StorageColor", KbLayer.Base, "C"),
            new(KsScope.Storage, "Nav",    "Enter",         "Str_Ks_StorageZoomIn", KbLayer.Base, "Enter"),
            new(KsScope.Storage, "Nav",    "Backspace",     "Str_Ks_StorageZoomOut", KbLayer.Base, "Back"),
            new(KsScope.Storage, "Nav",    "Home",          "Str_Ks_StorageHome",  KbLayer.Base, "Home"),
            new(KsScope.Storage, "Edit",   "Del",           "Str_Ks_StorageRecycle", KbLayer.Base, "Del"),
        ];

        // Display order of the SCOPE sections. Global first because it is what is true wherever
        // you are, then the file browser because that is what the window opens as, then the
        // applets in the order their tabs came to exist.
        private static readonly KsScope[] KsScopeOrder =
        [
            KsScope.Global, KsScope.Files, KsScope.Terminal, KsScope.Editor,
            KsScope.Processes, KsScope.Events, KsScope.Registry, KsScope.Storage,
        ];

        // Display order of the category groups WITHIN a scope. Search first because that is what
        // the app is for; Help last because you already found it if you are reading this card.
        private static readonly string[] KsCatOrder =
            ["Search", "Nav", "Tabs", "View", "File", "Edit", "Help"];

        /// <summary>Resource key for a category's heading, e.g. "Nav" -> Str_Ks_CatNav.</summary>
        internal static string KsCatLabelKey(string cat) => "Str_Ks_Cat" + cat;

        /// <summary>
        /// Resource key for a scope's heading. Not a "Str_Ks_Scope" + name concatenation like
        /// the categories: four of these applets already have a tab title saying exactly the
        /// same words, and a parallel set of keys would only be the same nouns translated twice
        /// in ten locale files and free to drift apart.
        /// </summary>
        /// <remarks>
        /// private, not internal like KsCatLabelKey next to it: KsScope is a private nested
        /// enum, and an internal method may not take a less accessible parameter type (CS0051).
        /// Both callers are partials of this same class, so nothing is lost.
        /// </remarks>
        private static string KsScopeLabelKey(KsScope scope) => scope switch
        {
            KsScope.Files     => "Str_Ks_ScopeFiles",
            KsScope.Terminal  => "Str_Ks_ScopeTerminal",
            KsScope.Editor    => "Str_Ks_ScopeEditor",
            KsScope.Processes => "Str_TabTitle_TaskManager",
            KsScope.Events    => "Str_TabTitle_EventViewer",
            KsScope.Registry  => "Str_TabTitle_RegistryEditor",
            KsScope.Storage   => "Str_TabTitle_Storage",
            _                 => "Str_Ks_ScopeGlobal",
        };

        // ── Which scope is live ───────────────────────────────────────────────────────────────
        // Two different questions, and they need two different answers:
        //
        //   KsActiveScope  - what the ACTIVE TAB is. Asked by the keyboard map, which is drawn
        //                    while the shortcuts card has focus, so nothing inside the tab is
        //                    focused at the time and a focus test would always say Global.
        //   KsFocusScope   - who OWNS the keyboard right now. Asked by Window_PreviewKeyDown,
        //                    where the question is whether a chord has already been claimed by
        //                    a focused applet. Built from the same per-applet focus walks the
        //                    handler's own handover guards use (TerminalHasFocus,
        //                    StorageAnalyzerHasFocus and the rest), so there is one definition
        //                    of "focused" and not two.

        /// <summary>The scope the active tab puts the card in. Global when there is no tab.</summary>
        private KsScope KsActiveScope
        {
            get
            {
                var t = _active;
                if (t == null) return KsScope.Global;   // == null, as everywhere else here
                if (t.IsTerminal)        return KsScope.Terminal;
                if (t.IsEditor)          return KsScope.Editor;
                if (t.IsProcessList)     return KsScope.Processes;
                if (t.IsEventViewer)     return KsScope.Events;
                if (t.IsRegistryEditor)  return KsScope.Registry;
                if (t.IsStorageAnalyzer) return KsScope.Storage;
                // The Performance Monitor has no bindings of its own, so it reads as Global
                // rather than as a scope with nothing in it.
                if (t.IsPerformanceMonitor) return KsScope.Global;
                return KsScope.Files;
            }
        }

        /// <summary>
        /// The applet that currently owns the keyboard, or <see cref="KsScope.Global"/> when the
        /// window does. Order matters only in that these are mutually exclusive in practice -
        /// focus is in exactly one control.
        /// </summary>
        private KsScope KsFocusScope
        {
            get
            {
                if (TerminalHasFocus)        return KsScope.Terminal;        // TerminalTabs.cs
                if (EditorHasFocus)          return KsScope.Editor;          // EditorTabs.cs
                if (ProcessListHasFocus)     return KsScope.Processes;       // ProcessTabs.cs
                if (EventViewerHasFocus)     return KsScope.Events;          // EventViewerTabs.cs
                if (RegistryEditorHasFocus)  return KsScope.Registry;        // RegistryEditorTabs.cs
                if (StorageAnalyzerHasFocus) return KsScope.Storage;         // StorageTabs.cs
                return KsScope.Global;
            }
        }

        private bool _ksListBuilt;

        /// <summary>
        /// Fills the card's list from <see cref="KsAll"/>: a full-width heading per SCOPE, and
        /// under it the two-column, color-headed CATEGORY groups. Built once, lazily - the card
        /// is not open at startup and most sessions never open it. Every brush and label is
        /// wired with SetResourceReference so a theme or language switch repaints it live rather
        /// than needing a rebuild.
        /// </summary>
        private void BuildShortcutsList()
        {
            if (_ksListBuilt) return;
            _ksListBuilt = true;

            ShortcutListHost.Children.Clear();

            bool firstScope = true;
            foreach (var scope in KsScopeOrder)
            {
                // A scope with no rows prints nothing at all rather than an empty heading. The
                // Event Viewer is in that position today - it has no keys of its own - and a
                // bare heading over nothing reads as a missing section rather than an empty one.
                if (!KsAll.Any(b => b.Scope == scope && b.Keys.Length > 0)) continue;

                var heading = new TextBlock
                {
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize   = 11,
                    FontWeight = FontWeights.Bold,
                };
                heading.SetResourceReference(TextBlock.TextProperty, KsScopeLabelKey(scope));
                // TextBrush, not a KsCat* brush: the category colors mean "kind of action" in
                // both views and a scope heading borrowing one would read as a seventh category.
                heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

                var rule = new Border
                {
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin  = new Thickness(0, firstScope ? 2 : 18, 0, 0),
                    Padding = new Thickness(0, 0, 0, 3),
                    Child   = heading,
                };
                rule.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
                ShortcutListHost.Children.Add(rule);
                firstScope = false;

                ShortcutListHost.Children.Add(BuildScopeColumns(scope));
            }
        }

        /// <summary>
        /// The two-column body of one scope section. Columns are balanced INSIDE a scope rather
        /// than across the whole card, so a scope heading always sits above its own rows.
        /// </summary>
        private Grid BuildScopeColumns(KsScope scope)
        {
            // Two columns side by side rather than one long scroll. Categories are dealt out
            // whole (a group is never split across the fold), but by rendered WEIGHT rather than
            // raw row count - a plain count split looked balanced on paper and then rendered
            // lopsided, because several rows wrap their description to two lines (long ones like
            // "Second pane (F10) | right-click to flip side-by-side / stacked") while most don't,
            // so whichever column happened to collect more of the long rows ran tall while the
            // other sat half-empty. Each category goes to whichever column is currently lighter,
            // which keeps the two columns close in actual height instead of item count.
            var left  = new StackPanel();
            var right = new StackPanel();
            double leftWeight = 0, rightWeight = 0;

            var columns = new Grid();
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 2);
            columns.Children.Add(left);
            columns.Children.Add(right);

            foreach (string cat in KsCatOrder)
            {
                var rowsInCat = KsAll.Where(r => r.Scope == scope && r.Cat == cat && r.Keys.Length > 0).ToList();
                if (rowsInCat.Count == 0) continue;

                // ~1 weight unit per heading, plus ~1 per description line, estimated from the
                // resolved string's length against the desc column's rough character width at
                // its font size - not exact (real wrapping depends on live layout), but close
                // enough to stop one column from running away from the other.
                double catWeight = 1.0;
                foreach (var r in rowsInCat)
                {
                    string text = TryFindResource(r.Label) as string ?? "";
                    catWeight += Math.Max(1, Math.Ceiling(text.Length / 42.0));
                }

                var column = leftWeight <= rightWeight ? left : right;
                if (column == left) leftWeight += catWeight; else rightWeight += catWeight;

                var heading = new TextBlock
                {
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize   = 10,
                    FontWeight = FontWeights.Bold,
                    Margin     = new Thickness(0, 10, 0, 6),
                };
                heading.SetResourceReference(TextBlock.TextProperty, KsCatLabelKey(cat));
                heading.SetResourceReference(TextBlock.ForegroundProperty, "KsCat" + cat);
                column.Children.Add(heading);

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                int row = 0;
                foreach (var r in rowsInCat)
                {
                    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var keys = new TextBlock
                    {
                        Text       = r.Keys,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        FontSize   = 12,
                        Margin     = new Thickness(0, 0, 10, 7),
                        VerticalAlignment = VerticalAlignment.Top,
                    };
                    keys.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
                    Grid.SetRow(keys, row);
                    Grid.SetColumn(keys, 0);
                    grid.Children.Add(keys);

                    var desc = new TextBlock
                    {
                        FontSize     = 11,
                        TextWrapping = TextWrapping.Wrap,
                        Margin       = new Thickness(0, 1, 0, 7),
                    };
                    desc.SetResourceReference(TextBlock.TextProperty, r.Label);
                    desc.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                    Grid.SetRow(desc, row);
                    Grid.SetColumn(desc, 1);
                    grid.Children.Add(desc);

                    row++;
                }

                column.Children.Add(grid);
            }

            return columns;
        }
    }
}
