using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;

using KillerShell.Shell;

namespace KillerShell.Models
{
    // One search tab: a complete, independent search - folder + terms + filters +
    // include/exclude + results + run state. The left panel and results pane always
    // show the ACTIVE tab; switching tabs swaps every ItemsSource/field over to the
    // incoming tab (see MainWindow ApplyTab/CaptureTab).
    public class SearchTab : INotifyPropertyChanged
    {
        public ObservableCollection<TermGroup>    Groups  { get; } = [];
        public ObservableCollection<SearchFilter> Filters { get; } = [];
        public ObservableCollection<SearchResult> Results { get; } = [];

        // Each tab owns its engine so searches on different tabs never share state.
        public SearchEngine Engine { get; } = new();
        public CancellationTokenSource? Cts;
        public bool IsSearching;

        // ── Shell (Terminal/) ────────────────────────────────────
        // A terminal tab owns its control, and the control owns the pty. Held here rather than
        // rebuilt on activation because a shell has STATE: rebuilding it on every tab switch
        // would kill whatever was running in it.
        // Internal, not public: TerminalControl is internal, and this model is public.
        internal KillerShell.Terminal.TerminalControl? Term;

        /// <summary>True when this tab is a shell rather than a folder or a search.</summary>
        public bool IsTerminal => Term != null;

        /// <summary>
        /// The shell's own exe (pwsh.exe/powershell.exe/cmd.exe), set alongside Term. Not used
        /// to run anything - CommandLine already did that - only so something wanting a REAL
        /// icon for a shell tab (the tab-strip overflow menu, Tabs.cs) can ask IconCache for the
        /// system's own PowerShell/cmd icon instead of an app-drawn glyph.
        /// </summary>
        internal string? TermExePath;

        // ── Editing (Editing/) ───────────────────────────────────
        // A document tab owns its editor, and the editor owns the text, the undo stack and the
        // caret. Held here rather than rebuilt on activation for the same reason the shell is:
        // rebuilding it on every tab switch would throw away all three, and unsaved changes with
        // them. Internal, not public: EditorControl is internal, and this model is public.
        internal KillerShell.Editing.EditorControl? Editor;

        /// <summary>True when this tab is a document rather than a folder, search or shell.</summary>
        public bool IsEditor => Editor != null;

        // ── Task Manager (Shell/ProcessListControl.cs) ────────────
        // A process-list tab owns its control the same way a shell owns its pty and a document
        // owns its editor: the control holds a live refresh timer, a filter and the grid's own
        // sort/scroll state, and rebuilding it on every tab switch would throw all of that away.
        // Internal, not public: ProcessListControl is internal, and this model is public.
        internal KillerShell.Shell.ProcessListControl? Procs;

        /// <summary>True when this tab is a Task Manager rather than a folder, search, shell or document.</summary>
        public bool IsProcessList => Procs != null;

        // ── Event Viewer (Shell/EventViewerControl.cs) ─────────────
        // Same "own host, own control" reasoning as Procs above: the control holds the log
        // source/level pickers, the filter text and the grid's own sort/scroll state, plus a
        // background load loop that would restart from nothing on every tab switch if this were
        // rebuilt instead of moved. Internal, not public: EventViewerControl is internal, and
        // this model is public.
        internal KillerShell.Shell.EventViewerControl? Events;

        /// <summary>True when this tab is an Event Viewer rather than a folder, search, shell,
        /// document or Task Manager.</summary>
        public bool IsEventViewer => Events != null;

        // ── Performance Monitor (Shell/PerformanceMonitorControl.cs) ────
        // Same "own host, own control" reasoning as Procs/Events above: the control holds the
        // live refresh timer, the sparkline history for each metric, and a one-time cache of the
        // static hardware info - rebuilding it on every tab switch would throw the graph history
        // away and re-run the WMI hardware query for no reason. Internal, not public:
        // PerformanceMonitorControl is internal, and this model is public.
        internal KillerShell.Shell.PerformanceMonitorControl? Perf;

        /// <summary>True when this tab is the Performance Monitor rather than a folder, search,
        /// shell, document, Task Manager or Event Viewer.</summary>
        public bool IsPerformanceMonitor => Perf != null;

        // ── Registry Editor (Shell/RegistryEditorControl.cs) ────────────
        // Same "own host, own control" reasoning as Procs/Events/Perf above: the control holds
        // the loaded tree (which keys are expanded, which values are cached for the selected
        // key) and rebuilding it on every tab switch would throw the whole browse position away.
        // Internal, not public: RegistryEditorControl is internal, and this model is public.
        internal KillerShell.Shell.RegistryEditorControl? Registry;

        /// <summary>True when this tab is the Registry Editor rather than a folder, search,
        /// shell, document, Task Manager, Event Viewer or Performance Monitor. Admin-only -
        /// see Shell/RegistryEditorTabs.cs.</summary>
        public bool IsRegistryEditor => Registry != null;

        // ── Browsing (Browse.cs) ─────────────────────────────────
        // A tab is either showing a folder's contents or a search's results, in the same
        // Results collection. IsBrowsing says which, so the sort can put folders first and the
        // nav buttons know whether they mean anything.
        //
        // Both notifying (2026-08-02, needed for the tab strip's real folder icon,
        // TabFolderIconConverter): a plain field never repaints a bound row on its own - the
        // same trap CLAUDE.md documents for KillerNotes' sidebar - and CurrentFolder changes
        // constantly without the tab itself being replaced, on every navigation within it.
        private bool _isBrowsing;
        public bool IsBrowsing
        {
            get => _isBrowsing;
            set { _isBrowsing = value; Notify(); }
        }

        private string _currentFolder = string.Empty;
        public string CurrentFolder
        {
            get => _currentFolder;
            set { _currentFolder = value; Notify(); }
        }

        // Back / forward, browser-style: a list of visited folders plus a cursor into it, rather
        // than two stacks, so Forward survives going Back several steps.
        public List<string> History      = [];
        public int          HistoryIndex = -1;

        // Search config captured from the left panel when switching away.
        public string RootPath        = string.Empty;
        public string IncludePatterns = "*.*";
        public string ExcludePatterns = string.Empty;
        public bool   CaseSensitive;

        // Last-known footer/status text so a tab switch restores what this search showed.
        public string StatusMessage = string.Empty;
        public string ScannedLabel  = string.Empty;
        public string StatsLabel    = string.Empty;

        // Raw pieces behind the rendered lines above: resource key + args instead of
        // final text, so a live language switch can re-render EVERY tab's status
        // (RelocalizeDynamicUi). Null/-1 = nothing stored (transient text stays as-is).
        public string?   StatusKey;
        public object[]? StatusArgs;
        public long      ScannedCount = -1;
        public object[]? PipeArgs;     // {count, source title, query} for Str_Pipe_Scope
        // Human-readable summary of what this tab last searched for, shown in the
        // results header so old tabs stay self-explanatory.
        public string QueryLabel    = string.Empty;

        // Results sort (mirrors the HTML report): 0 = as found, 1 = name,
        // 2 = location, 3 = size, 4 = modified.
        public int  SortIndex;
        public bool SortAsc = true;

        // Quick filter (Ctrl+F) narrowing the visible results by name/path.
        public string FilterText = string.Empty;

        // Piped scope: when set, this tab searches THIS file list (a snapshot of another
        // tab's results) instead of walking RootPath. Picking a folder clears it.
        public List<string>? PipeFiles;
        public string PipeLabel = string.Empty;   // breadcrumb shown in the location row

        public SearchTab(string title) { _title = title; }

        private string _title;
        public string Title
        {
            get => _title;
            set { _title = value; Notify(); }
        }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; Notify(); }
        }

        // True only for the ACTIVE tab of the FOCUSED pane, and only while two panes are open.
        // The focus ring has to continue around the active tab - the tab and the pane are one
        // surface, so a ring that stops at the tab strip reads as broken. Notifying, because it
        // is bound in the tab template and changes without the row being rebuilt (see the note
        // in CLAUDE.md about non-notifying bound properties on this model).
        private bool _paneFocused;
        public bool PaneFocused
        {
            get => _paneFocused;
            set { _paneFocused = value; Notify(); }
        }

        // Active tab of the pane that does NOT have focus. Its accent lip drops to the dimmed
        // TabEdgeBrush - two lips at full accent both claim to be the live pane. Not simply
        // !PaneFocused: with one pane open there is no focused/unfocused distinction to draw,
        // and the single pane's lip stays bright.
        private bool _paneDimmed;
        public bool PaneDimmed
        {
            get => _paneDimmed;
            set { _paneDimmed = value; Notify(); }
        }

        // MDL2 glyph shown before the title, empty for a folder or search tab. Notifying,
        // because it is bound in the tab template (see the note in CLAUDE.md about
        // non-notifying bound properties on this model).
        private string _tabGlyph = string.Empty;
        public string TabGlyph
        {
            get => _tabGlyph;
            set { _tabGlyph = value; Notify(); }
        }

        // Sitting on the strip's right EDGE - which is the last visible tab, but only while the
        // overflow chevron is hidden. The tab's 1px right border is a divider BETWEEN tabs, so a
        // tab on the edge has to drop it, where it would read as a stray rule; a tab with the
        // chevron beside it still wants it. It also decides who owns the ring's right vertical
        // (see IsFirst). UpdateTabBar sets it on every add, close, drag-reorder and resize.
        private bool _isLast;
        public bool IsLast
        {
            get => _isLast;
            set { _isLast = value; Notify(); }
        }

        // Leftmost tab in the strip. Only the focus ring reads this: the band draws the ring's
        // outermost verticals itself (FilePane.xaml TabEdgeLeft / TabEdgeRight), because a tab's
        // own outer border sits on the ScrollViewer's clip edge and survives or vanishes
        // depending on how the UniformGrid divided a fractional band width. Without this the
        // first and last tab drew that side TOO, so the outer edge of the ring was 2px wherever
        // the clip happened to spare it and 1px everywhere else - the same width the pane border
        // and the band line are. Set beside IsLast on every add, close and drag-reorder.
        private bool _isFirst;
        public bool IsFirst
        {
            get => _isFirst;
            set { _isFirst = value; Notify(); }
        }

        // In the strip right now, as opposed to behind the chevron. The strip caps the NUMBER of
        // tabs rather than letting them shrink without limit (Tabs.cs ApplyTabWindow), and a tab
        // outside the window collapses - UniformGrid ignores a collapsed child when it divides
        // the band, so the ones left still fill it edge to edge. True by default: a tab is in the
        // strip until something works out that it does not fit.
        private bool _isStripVisible = true;
        public bool IsStripVisible
        {
            get => _isStripVisible;
            set { _isStripVisible = value; Notify(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
