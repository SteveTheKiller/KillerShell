using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Models;

// Tab lifecycle + the KillerPDF-style tab strip physics. Partial of MainWindow.
// Each SearchTab is a complete search; the left panel and results pane always show
// the ACTIVE tab (ActivateTab points every ItemsSource/field at it).
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        // ═══════════════════════════════════════════════════════════
        //  TAB LIFECYCLE
        // ═══════════════════════════════════════════════════════════
        private SearchTab CreateTab()
        {
            var tab = new SearchTab(Loc("Str_Tab_New"));
            var group = new TermGroup();
            group.Terms.Add(new SearchTerm());
            tab.Groups.Add(group);
            tab.StatusMessage = Loc("Str_Status_Ready");
            tab.StatusKey     = "Str_Status_Ready";

            // Each tab owns its engine; callbacks carry the tab so a background
            // tab's search never paints over the active tab's UI.
            tab.Engine.ResultsBatch    += batch => OnResultsBatch(tab, batch);
            tab.Engine.StatusChanged   += msg   => OnStatusChanged(tab, msg);
            tab.Engine.ProgressChanged += n     => OnProgressChanged(tab, n);

            _tabs.Add(tab);
            UpdateTabBar();
            return tab;
        }

        // The tab bar only exists once there are 2+ tabs (like KillerPDF). While it is
        // visible, each top corner squares off ONLY when the tab sitting on it is the active
        // one - first tab owns the top-left, last tab owns the top-right (the tabs fill the
        // strip edge to edge, so those two always reach the pane's corners). The active tab
        // is painted in PaneBrush, so it and the pane have to read as one surface: the strip
        // covers just the 1px the pane is pulled up by (its -1 top margin), and a 6px radius
        // under it leaves ~5px of curve - plus the border stroke along it - showing as a
        // rounded step and a tab-shaped outline. An inactive tab is window-colored, so it is
        // a different surface anyway and the pane keeps its rounding under it. One tab
        // collapses the strip and the pane takes its fully rounded top back. Re-run on tab
        // switch and after a drag-reorder, since either can change which tab owns a corner.
        // Runs for EVERY live pane, not just the focused one: the band takes real height, so a
        // pane that shows it sits lower than a pane that does not, and the two pane tops stopped
        // lining up the moment their tab counts differed.
        private void UpdateTabBar()
        {
            ForEachPane(UpdateTabBarInPane);   // Panes.cs

            // The pane's top margin depends on whether the strip is showing, which is exactly
            // what just changed (DualPane.cs).
            ApplyPaneMargins();

            // Which tab owns the strip's left and right edge can change on any add, close or
            // drag-reorder, and the ring's verticals follow it too.
            UpdatePaneFocusRing();
        }

        private void UpdateTabBarInPane()
        {
            // With two panes open the band shows in BOTH as soon as EITHER has more than one
            // tab. A single-tab pane then shows its one tab, which is still a thing you can
            // click; reserving blank strip height instead would line the tops up with dead
            // space. With one pane LivePanes() yields only that pane, so this is the old
            // "2+ tabs" rule unchanged.
            bool show = LivePanes().Any(p => p.Tabs.Count > 1);
            Pane.TabBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            // Which tabs fit at this width, before anything below asks which is on an edge.
            ApplyTabWindow(Pane);

            // First and last VISIBLE, not first and last in the list. Both are about the strip's
            // own edges: IsLast drops the divider that would otherwise land on the right edge as
            // a stray rule, and IsFirst/IsLast keep the tab from drawing the outer ring side that
            // the band already draws (SearchTab, FilePane.xaml). With tabs windowed out, the tab
            // sitting on an edge is not the one at the end of the collection.
            //
            // And NOT the last visible tab when the chevron is showing: the chevron is what sits
            // on the band's right edge then, so the tab is a middle tab in every way that
            // matters. Told otherwise it dropped the divider that separates it from the chevron
            // AND handed its right side to the band, which drew that side at the band's edge -
            // past the chevron, as a green stripe up the far right with nothing under it.
            bool chevron = Pane.TabOverflowBtn.Visibility == Visibility.Visible;

            var strip = _tabs.Where(t => t.IsStripVisible).ToList();
            foreach (var t in _tabs) { t.IsFirst = false; t.IsLast = false; }
            if (strip.Count > 0)
            {
                strip[0].IsFirst              = true;
                strip[^1].IsLast = !chevron;
            }

            if (show)
            {
                // Off the tab, like the ring reads them (DualPane.UpdatePaneFocusRing) - NOT
                // recomputed from the strip. Recomputed, "last" means last visible and misses
                // the chevron, so the pane squared its top-right corner under a chevron that is
                // sitting on that corner instead of the tab: a hard edge on the details header
                // with no tab above it to explain the join.
                //
                // Pattern-matched rather than dereferenced: F11 creates the second pane EMPTY
                // and this runs before SeedPane gives it a tab, so Active is genuinely null for
                // one pass. `Active` is declared non-nullable ("set before anything reads it"),
                // which is true of every other caller and was not true of this one - it threw a
                // NullReferenceException the moment the second pane opened.
                var act = Pane.Active;
                bool firstActive = act is { IsFirst: true };
                bool lastActive  = act is { IsLast:  true };
                double r = PaneRadius, b = BarRadius;
                Pane.ResultsPane.CornerRadius = new CornerRadius(firstActive ? 0 : r, lastActive ? 0 : r, r, r);
                Pane.ScopeBar.CornerRadius    = new CornerRadius(firstActive ? 0 : b, 0, 0, 0);
                // The details header is the top of the pane whenever the location row is hidden,
                // so it has to nest inside the pane's curve the same way. Left at a fixed 5,5 it
                // kept its own curve under a squared pane corner, and the sliver of pane showing
                // outside the curve but inside the square border read as a hard edge.
                Pane.DetailsHeader.CornerRadius =
                    new CornerRadius(firstActive ? 0 : b, lastActive ? 0 : b, 0, 0);
                // The ring line in the band IS the pane's top border, so it curves where the
                // pane curves. Left flat and full-width it overshot the corner and read as a
                // rule laid across the pane rather than as its edge (FilePane.xaml).
                Pane.TabBarRing.CornerRadius  = new CornerRadius(firstActive ? 0 : r, lastActive ? 0 : r, 0, 0);
            }
            else
            {
                double r = PaneRadius, b = BarRadius;
                Pane.ResultsPane.CornerRadius   = new CornerRadius(r);
                Pane.ScopeBar.CornerRadius      = new CornerRadius(b, 0, 0, 0);
                Pane.DetailsHeader.CornerRadius = new CornerRadius(b, b, 0, 0);
            }

            // The content clip mirrors ResultsPane.CornerRadius per corner now
            // (FilePane.xaml.cs PaneContent_SizeChanged), so it has to re-run whenever the
            // corners just changed - otherwise a last-active tab squared the pane's border while
            // the clip kept rounding the bar under it.
            Pane.RefreshPaneClip();
        }

        /// <summary>
        /// The pane's outer corner radius, from the theme: 6 on the twelve rounded themes, 0 on a
        /// flat one. Every radius in this file used to be the literal 6 or 5, which is why the
        /// terminal and the listing kept three rounded corners on 98SE however many CornerRadius
        /// attributes in the markup were zeroed - these are assigned from code and overwrite
        /// whatever the XAML said, on every tab change.
        /// </summary>
        private static double PaneRadius => RadiusOf("PaneCornerRadiusValue", 6.0);

        /// <summary>The nested-bar radius: the scope bar and the details header, 5 / 0.</summary>
        private static double BarRadius => RadiusOf("BarCornerRadiusValue", 5.0);

        // A Double, not a CornerRadius resource: these are built per corner from first/last-tab
        // state, so what is needed is the scalar, not a ready-made set of four.
        private static double RadiusOf(string key, double fallback)
            => Application.Current?.TryFindResource(key) is double d && d >= 0 ? d : fallback;

        // ═══════════════════════════════════════════════════════════
        //  OVERFLOW
        // ═══════════════════════════════════════════════════════════
        // The strip is a UniformGrid, so every tab takes an equal share of the band whatever the
        // count - right up to a point, and then off a cliff. Eight tabs in a half-width pane came
        // out around forty pixels each: "~\D_ x", which is not a label, it is a shape. A tab you
        // cannot read is a tab you have to click to identify, and at that point the strip has
        // stopped being navigation.
        //
        // So the COUNT is capped rather than the width. As many tabs as fit at TabFloorWidth are
        // shown and the rest are collapsed - UniformGrid ignores a collapsed child when it
        // divides the band, so the survivors still fill it edge to edge with no arithmetic here.
        // The chevron at the right end lists every tab, so nothing is unreachable.
        //
        // Scrolling was the other option and is what a browser does. It lost because the band is
        // a bordered surface the pane's focus ring runs along, and a scrolled band cannot be edge
        // to edge - the ring would have to stop somewhere that is not a corner. The chevron keeps
        // the strip one complete object and puts the overflow in a list, which is how the
        // family's toolbars already shed their groups (KillerPDF SettingsPanel.cs).

        /// <summary>Narrowest a tab may get before the strip stops taking more.</summary>
        /// <remarks>
        /// Picked from what it has to hold rather than off a grid: 120px of Consolas 11.5 is
        /// about sixteen characters once the glyph, the close x and the padding are paid for -
        /// "Backup-Nightl...", enough to tell two scripts apart. Much below a hundred and the
        /// ellipsis starts eating the part that distinguishes them, which is the whole job.
        /// </remarks>
        private const double TabFloorWidth = 120;

        /// <summary>What the chevron takes out of the band while it is showing.</summary>
        private const double TabChevronWidth = 26;

        /// <summary>
        /// Decide which of <paramref name="p"/>'s tabs are in the strip at its current width, and
        /// show or hide the chevron. Called from UpdateTabBarInPane, before anything reads which
        /// tab is on an edge.
        /// </summary>
        /// <remarks>
        /// The window is a contiguous RUN, not a set: tabs keep their order and their neighbors,
        /// so a strip that has moved still reads like the tab bar it was. It shifts the least it
        /// can to keep the active tab on screen, which is the one invariant that matters - a tab
        /// you just switched to and cannot see is worse than no strip at all.
        /// </remarks>
        private void ApplyTabWindow(FilePane p)
        {
            var tabs = p.Tabs;
            int n = tabs.Count;
            if (n == 0)
            {
                p.TabOverflowBtn.Visibility = Visibility.Collapsed;
                return;
            }

            // ActualWidth is 0 until the band has been measured once - on the first pass, and on
            // any pass that runs while the pane is collapsed. Falling back to the pane's own
            // width keeps the answer sane instead of capping the strip at one tab and having to
            // be undone by the SizeChanged that follows.
            double avail = p.TabBar.ActualWidth > 0 ? p.TabBar.ActualWidth : p.ActualWidth;

            // Two passes, because the chevron's width changes the answer that decides whether
            // there is a chevron. Asked without it first: if everything fits there is none, and
            // the whole band belongs to the strip.
            int cap = (int)(avail / TabFloorWidth);
            bool overflow = cap < n;
            if (overflow)
            {
                cap = Math.Max(1, (int)((avail - TabChevronWidth) / TabFloorWidth));
                if (cap >= n) overflow = false;
            }

            p.TabOverflowBtn.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;

            int start = 0;
            if (overflow)
            {
                // Clamped before the active tab is considered, so a window left pointing past the
                // end by a close does not survive as a scroll nobody asked for.
                start = Math.Max(0, Math.Min(p.TabWindow, n - cap));

                int active = p.Active == null ? -1 : tabs.IndexOf(p.Active);
                if      (active >= 0 && active < start)           start = active;
                else if (active >= 0 && active > start + cap - 1) start = active - cap + 1;

                p.TabWindow = start;
            }
            else
            {
                p.TabWindow = 0;
                cap = n;
            }

            for (int i = 0; i < n; i++)
                tabs[i].IsStripVisible = i >= start && i < start + cap;
        }

        /// <summary>The band was resized, so the strip may hold a different number of tabs.</summary>
        /// <remarks>
        /// Goes through UpdateTabBar rather than calling ApplyTabWindow alone: a different set of
        /// visible tabs is a different first and last tab, and those are the pane's corner
        /// rounding and the focus ring's outer verticals as much as they are the strip.
        /// </remarks>
        internal void TabBarResized(FilePane p)
        {
            if (p.Tabs.Count == 0 || _inTabResize) return;

            // Reentrancy guard, not an optimization. UpdateTabBar reaches ApplyPaneMargins and
            // flips the band's own Visibility, either of which can raise SizeChanged again from
            // inside this call - and a layout loop in WPF is not a slow app, it is a hung one.
            // The pass that follows would compute the same answer anyway.
            _inTabResize = true;
            try     { UpdateTabBar(); }
            finally { _inTabResize = false; }
        }

        private bool _inTabResize;

        /// <summary>The chevron: every tab in this pane, hidden ones included, in strip order.</summary>
        /// <remarks>
        /// EVERY tab, not only the overflowed ones. A list that shows just what is off screen
        /// makes you work out which those are before you can use it, and the visible ones cost
        /// nothing to include. Built on each open rather than kept: titles change on every save,
        /// navigation and rename.
        /// </remarks>
        internal void TabOverflowMenu(FilePane p)
        {
            FocusPane(p);   // Panes.cs - the click already did this, but the menu acts on p

            var menu = new ContextMenu
            {
                Placement       = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                PlacementTarget = p.TabOverflowBtn,
            };

            foreach (var t in p.Tabs)
            {
                var tab = t;   // captured per row, not per loop

                // Doubled, because a lone underscore in a MenuItem header is an access-key
                // marker: "Backup_Nightly.ps1" would draw as "BackupNightly.ps1" with an N
                // underlined, and file names carry underscores all the time.
                // A close X at the END of the row, like KillerPDF's recent-documents dropdown, so
                // a tab can be shut from the list without switching to it first. The header
                // becomes a DockPanel rather than a string: the title takes the remaining width
                // and the X docks right, so every row's X lines up.
                var closeBtn = new Button
                {
                    Content = "x",
                    Style = (Style)FindResource("DangerButton"),
                    Width = 16,
                    Height = 16,
                    Margin = new Thickness(12, 0, 0, 0),
                    FocusVisualStyle = null,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = TryFindResource("Str_TT_CloseTab"),
                };

                var label = new TextBlock
                {
                    // Doubled, because a lone underscore in a MenuItem header is an access-key
                    // marker: "Backup_Nightly.ps1" would draw as "BackupNightly.ps1" with an N
                    // underlined, and file names carry underscores all the time.
                    Text = tab.Title.Replace("_", "__"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    // Bold rather than a check mark: IsChecked draws into the Icon slot, which
                    // the tab's own icon is already using, and the two cannot both show.
                    FontWeight = tab.IsActive ? FontWeights.Bold : FontWeights.Normal,
                };

                var row = new DockPanel { LastChildFill = true, MinWidth = 160 };
                DockPanel.SetDock(closeBtn, Dock.Right);
                row.Children.Add(closeBtn);
                row.Children.Add(label);

                var item = new MenuItem
                {
                    Header = row,
                    Icon = OverflowRowIcon(tab)   // below
                };

                var capturedTab = tab;
                closeBtn.Click += (s, e) =>
                {
                    // Handled, or the click bubbles to the MenuItem underneath and switches to
                    // the very tab that is being closed before the close runs.
                    e.Handled = true;
                    menu.IsOpen = false;
                    FocusPane(p);
                    CloseTab(capturedTab);          // Tabs.cs
                };

                item.Click += (_, _) => { FocusPane(p); SwitchToTab(capturedTab); };
                menu.Items.Add(item);
            }

            menu.IsOpen = true;
        }

        /// <summary>
        /// A tab's icon for the overflow dropdown. Real shell icons where a real path exists
        /// (a folder or an open document), a plain color swatch otherwise.
        /// </summary>
        /// <remarks>
        /// NOT an MDL2 glyph TextBlock (2026-08-02, third attempt at this row's icon):
        /// two different glyph techniques - a plain Style assignment, then a
        /// SetResourceReference wrapped in a Viewbox - both rendered every row as the exact same
        /// shape regardless of which codepoint was actually requested, which is not something a
        /// resource-lookup or a clipping bug explains. Recents.cs's folder icons in this same
        /// MenuItem.Icon slot, built from IconCache rather than a font glyph, were never reported
        /// broken - so this sidesteps font rendering in this spot entirely rather than trying a
        /// fourth variation of a technique that has now failed twice.
        /// </remarks>
        private static FrameworkElement OverflowRowIcon(SearchTab tab)
        {
            if (tab.IsBrowsing && !string.IsNullOrEmpty(tab.CurrentFolder)
                && System.IO.Directory.Exists(tab.CurrentFolder))
            {
                return new Image
                {
                    Width = 16, Height = 16,
                    Source = Services.IconCache.For(tab.CurrentFolder, 16, isDirectory: true),
                };
            }

            if (tab.Editor != null && !tab.Editor.IsUntitled && System.IO.File.Exists(tab.Editor.FilePath))
            {
                return new Image
                {
                    Width = 16, Height = 16,
                    Source = Services.IconCache.For(tab.Editor.FilePath, 16),
                };
            }

            // A shell tab gets the SYSTEM's own PowerShell/cmd icon - the exact exe TermExePath
            // names (TerminalProfile.ExePath, set alongside Term in TerminalTabs.cs) - rather
            // than an app-drawn glyph. Unset in demo mode on purpose (CreateDemoTerminalTab):
            // that path is real and local, and a demo screenshot fabricates everything else
            // about a shell tab specifically to avoid putting anything of THIS machine on screen.
            if (tab.IsTerminal && tab.TermExePath is string exePath && exePath.Length > 0
                && System.IO.File.Exists(exePath))
            {
                return new Image
                {
                    Width = 16, Height = 16,
                    Source = Services.IconCache.For(exePath, 16),
                };
            }

            // A Processes tab, an Event Viewer tab, a Performance tab, or a document/shell with no
            // real file to ask Explorer about (untitled, demo mode, a folder that has since been
            // deleted/unplugged): nothing real to show an icon of, so a small color dot stands
            // in - a shell in the accent, a Processes tab in DangerRed, an Event Viewer tab in
            // WarningAmber, a Performance tab in InfoBlue (all fixed, non-theme keys already used
            // for status color elsewhere - Controls.xaml), anything else muted.
            var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(4) };
            string brush = tab.IsTerminal ? "PrimaryBrush"
                : tab.IsProcessList         ? "DangerRed"
                : tab.IsEventViewer         ? "WarningAmber"
                : tab.IsPerformanceMonitor  ? "InfoBlue"
                : tab.IsRegistryEditor      ? "PrimaryBrush"
                : tab.IsStorageAnalyzer     ? "InfoBlue"
                : "MutedTextBrush";
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
            return dot;
        }

        // Save the left panel's editable fields into the outgoing tab.
        private void CaptureTab(SearchTab t)
        {
            t.RootPath        = Pane.RootPathBox.Text;
            t.IncludePatterns = IncludePatternsBox.Text;
            t.ExcludePatterns = ExcludePatternsBox.Text;
            t.CaseSensitive   = CaseSensitiveCheck.IsChecked == true;

            // ...and which rows were selected, as PATHS (SearchTab.SelectedPaths). Nothing else
            // remembers a tab's selection, so without this, switching to another tab and coming
            // back leaves the list - and the details strip with it - blank: the return trip
            // re-binds ResultsList.ItemsSource, and assigning ItemsSource empties the ListBox's
            // selection. Paths rather than the SearchResult rows themselves because a browsing
            // tab re-lists its folder on activation, so the objects that were selected do not
            // survive either.
            //
            // Guarded on the list actually showing THIS tab's results. CaptureTab is always
            // called for the focused pane's active tab, so it normally is; reading the selection
            // off a list bound to something else would store another tab's rows here, and
            // storing an empty list instead would throw away what this tab had.
            if (ReferenceEquals(Pane.ResultsList.ItemsSource, t.Results))
                t.SelectedPaths = [.. Pane.ResultsList.SelectedItems
                    .OfType<SearchResult>().Select(r => r.FilePath)];
        }

        // Point the whole UI at a tab: collections, config boxes, status, counters, button label.
        private void ActivateTab(SearchTab t)
        {
            // Cancel a half-typed address edit before the switch. Clicking a tab does not move
            // keyboard focus off the address TextBox (a Border press takes no focus), so
            // LostFocus never fired and the box stayed visible carrying the OLD tab's path over
            // the new tab. Same cancel-not-commit rule as clicking away.
            if (Pane.AddressBox.Visibility == Visibility.Visible)
            {
                Pane.AddressBox.Visibility     = Visibility.Collapsed;
                Pane.ScopePathLabel.Visibility = Visibility.Visible;
            }

            _active = t;
            foreach (var tab in _tabs) tab.IsActive = tab == t;

            // The whole strip, not just the ring. Switching tabs can move the overflow window
            // (the incoming tab may be behind the chevron), which changes which tab sits on each
            // edge, which is the pane's corner rounding and the ring's outer verticals - and the
            // ring is the last thing UpdateTabBar does anyway.
            UpdateTabBar();

            TermsList.ItemsSource   = t.Groups;
            FiltersList.ItemsSource = t.Filters;

            // Only re-bind when the collection actually CHANGES. Assigning ItemsSource resets
            // the ListBox's selection even when handed the very same collection back, so
            // re-activating a tab that this pane is already showing threw away whatever row was
            // selected - which is what happened clicking pane 2 and then clicking pane 1's tab
            // again: the file you had selected, and the details pane with it, went blank
            // (2026-08-09). Nothing else here depends on the assignment happening every time.
            bool rebound = !ReferenceEquals(Pane.ResultsList.ItemsSource, t.Results);
            if (rebound)
            {
                Pane.ResultsList.ItemsSource = t.Results;

                // Switching to a DIFFERENT tab and back genuinely re-binds, and the guard above
                // cannot help there - that assignment really does empty the selection. So put the
                // rows CaptureTab stashed on the tab back on the list.
                //
                // DISPATCHED, never inline. Two reasons, either one enough on its own:
                //   - the ListBox has only just been handed the collection, so no layout pass has
                //     run and its item containers do not exist yet; a selection applied here
                //     selects nothing.
                //   - the rest of ActivateTab still has to run, and ApplySort/ApplyFilter both
                //     re-shape the collection view underneath the list, so even a selection that
                //     did stick would not survive the same call.
                // Background priority sits below Render and Loaded, so layout has happened by the
                // time this runs.
                //
                // Restored only on a real re-bind, never on a plain re-activation of the tab this
                // pane is already showing: there the LIVE selection is still on the list, and
                // re-applying the stored paths would overwrite it with whatever was selected the
                // last time the tab was switched away from.
                //
                // The pane is captured into a local rather than read off `Pane` inside the
                // closure - focus can move again before this runs, and the restore belongs to the
                // pane that just re-bound, not to whichever one happens to be focused then.
                var reboundPane = Pane;
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => RestoreTabSelection(reboundPane, t)));
            }

            ApplyTerminalView(t);     // TerminalTabs.cs     - a shell tab shows a pty, not a listing
            ApplyEditorView(t);       // EditorTabs.cs       - and a document tab shows a document
            ApplyProcessListView(t);       // ProcessTabs.cs      - and a Task Manager tab shows the process grid
            ApplyEventViewerView(t);       // EventViewerTabs.cs  - and an Event Viewer tab shows the log grid
            ApplyPerformanceMonitorView(t);// PerformanceTabs.cs  - and a Performance tab shows the gauges
            ApplyRegistryEditorView(t);    // RegistryEditorTabs.cs - and a Registry Editor tab shows the tree
            ApplyStorageAnalyzerView(t);   // StorageTabs.cs      - and a Storage tab shows the treemap
            ApplyPaneBars(t);              // PaneBars.cs         - each kind wears its own bar

            Pane.RootPathBox.Text             = t.RootPath;
            Pane.ScopePathLabel.Text          = t.PipeFiles != null ? t.PipeLabel
                : string.IsNullOrEmpty(t.RootPath) ? Loc("Str_Scope_Empty") : t.RootPath;
            IncludePatternsBox.Text      = t.IncludePatterns;
            ExcludePatternsBox.Text      = t.ExcludePatterns;
            CaseSensitiveCheck.IsChecked = t.CaseSensitive;

            SetFooterStatus(t.StatusMessage);           // window footer - the live line
            // The light belongs to whichever tab is showing: switching from a running tab to an
            // idle one has to drop it back off amber, and vice versa.
            ApplyStatusTone(t.StatusKey);
            Pane.QueryText.Text    = t.QueryLabel;
            SetExpandAllLabel(t.Results.Count > 0 && t.Results.All(r => r.IsExpanded));
            ApplySort(t);
            Pane.ResultFilterBox.Text     = t.FilterText;
            Pane.ResultFilterBar.Visibility = t.FilterText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilter(t);
            Pane.ScannedText.Text       = t.ScannedLabel;
            Pane.ScannedText.Visibility = t.IsSearching ? Visibility.Visible : Visibility.Collapsed;
            Pane.StatsText.Text         = t.StatsLabel;
            UpdatePaneStatusBar();      // the incoming tab may have nothing to report
            UpdateLocationColumn();   // ViewOptions.cs - a browsing tab hides the folder column
            SearchButton.Content   = t.IsSearching ? Loc("Str_Btn_Stop") : Loc("Str_Btn_Search");
            Pane.ResultsHeader.Text = t.Results.Count > 0
                ? string.Format(Loc("Str_Lbl_ResultsCount"), t.Results.Count)
                : Loc("Str_Lbl_Results");
            UpdateNavButtons();   // Browse.cs - back/forward/up belong to the incoming tab

            // Only the active tab is watched (BrowseWatcher.cs), so the watch moves with it and
            // the incoming tab gets a silent refresh to catch anything that changed on disk while
            // it sat in the background.
            StopWatching();
            if (t.IsBrowsing && System.IO.Directory.Exists(t.CurrentFolder))
                _ = RefreshBrowsingTab(Pane, t, restoreSelection: rebound);

            UpdateTabBar();       // corner rounding follows which tab is active

            // Every path that OPENS a tool tab ends here (each tool's CreateXTab finishes with
            // ActivateTab), so this is where a newly-opened Event Viewer / Processes /
            // Performance / Registry / Storage tab first lights its rail icon.
            UpdateToolRailLights();
        }

        /// <summary>
        /// The silent refresh an incoming BROWSING tab gets, with the selection restore that has
        /// to wait for it.
        /// </summary>
        /// <remarks>
        /// NavigateTo clears tab.Results and refills it with brand new SearchResult instances, so
        /// on a browsing tab it wipes any selection already sitting on the list. The dispatched
        /// restore in ActivateTab is therefore racing the listing task: the task's continuation
        /// comes back at Normal priority and the restore is queued at Background, so the listing
        /// usually lands first, but "usually" is not the same as always - a network share or a
        /// drive spinning up loses the race and the selection goes with it. Awaiting the refresh
        /// and restoring again makes the order certain.
        ///
        /// Restoring twice for one activation costs nothing: RestoreTabSelection matches on path
        /// and leaves the tab's stored paths alone, so the second pass re-applies the same rows.
        ///
        /// restoreSelection is false when the pane did NOT re-bind - see ActivateTab for why a
        /// plain re-activation must keep the live selection rather than the stored one. That case
        /// is what keepSelection covers, and it is the ONLY thing that covers it: the stored paths
        /// are written by CaptureTab on a real tab SWITCH, so on a mere focus change between panes
        /// they are stale or empty and there is nothing to restore from. keepSelection has
        /// NavigateTo carry the LIVE selection across its own refill instead (Browse.cs).
        ///
        /// The refresh itself stays. It is not decoration: only the focused pane's active tab is
        /// watched (BrowseWatcher.cs), so the pane being returned to is precisely the one that has
        /// been running unwatched and may be showing a folder that has changed since. ActivateTab
        /// also drops the watcher unconditionally right before this, and NavigateTo's StartWatching
        /// is what arms it again - skip the refresh and that pane would not only be stale, it would
        /// stay stale until the next real navigation.
        /// </remarks>
        private async System.Threading.Tasks.Task RefreshBrowsingTab(FilePane pane, SearchTab t, bool restoreSelection)
        {
            await NavigateTo(t.CurrentFolder, record: false, keepSelection: true);   // Browse.cs
            if (restoreSelection) RestoreTabSelection(pane, t);
        }

        /// <summary>
        /// The paths of the rows selected in <paramref name="pane"/> at this instant, or null when
        /// its list is not showing <paramref name="t"/>'s results.
        /// </summary>
        /// <remarks>
        /// Paths rather than the rows themselves for the same reason RestoreTabSelection matches on
        /// them: the caller is about to destroy every SearchResult in the collection, so holding
        /// the objects would hold exactly the instances that are on their way out.
        ///
        /// The guard is the same one RestoreTabSelection uses - reading a selection off a list that
        /// is bound to some other tab's results would carry the wrong rows entirely.
        /// </remarks>
        private static System.Collections.Generic.List<string>? LiveSelectedPaths(FilePane pane, SearchTab t)
        {
            var list = pane.ResultsList;
            if (!ReferenceEquals(list.ItemsSource, t.Results)) return null;
            return [.. list.SelectedItems.OfType<SearchResult>().Select(r => r.FilePath)];
        }

        /// <summary>
        /// Put a tab's remembered selection back on <paramref name="pane"/>'s results list,
        /// matching the rebuilt rows by PATH.
        /// </summary>
        /// <remarks>
        /// Paths rather than the SearchResult objects themselves because by the time this runs
        /// the rows are different instances - the list was re-bound, and a browsing tab re-listed
        /// its folder from disk - so the object that was selected is not in the list to hand back.
        ///
        /// Idempotent and non-destructive: the tab's stored paths stay put, so this can run twice
        /// for one activation (once dispatched from ActivateTab, once after the browsing refresh)
        /// and the second pass simply re-applies the same rows.
        ///
        /// _restoringSelection holds off the two things a live selection change drives, because
        /// the rows go in ONE AT A TIME and SelectedItems.Add raises SelectionChanged on every
        /// one of them: the footer path line (ResultsView.cs), which would otherwise end up
        /// showing a file path over the tab status ActivateTab has just restored, and the details
        /// strip (DetailsPane.cs), which would otherwise repaint - bumping its generation counter
        /// and starting a stat and an image decode - once per row, on part-built selections. The
        /// strip is then repainted in ApplySelectionByPath ONCE, after the flag drops, so it
        /// describes the WHOLE restored selection instead of being left blank or showing only the
        /// first row.
        /// </remarks>
        private void RestoreTabSelection(FilePane pane, SearchTab t)
            => ApplySelectionByPath(pane, t, t.SelectedPaths);

        /// <summary>
        /// Select the rows of <paramref name="t"/> whose paths are in <paramref name="paths"/>, on
        /// <paramref name="pane"/>'s results list, and repaint that pane's details strip once.
        /// </summary>
        /// <remarks>
        /// Split out of RestoreTabSelection so the two callers cannot drift: the tab-activation
        /// restore above, which feeds it the paths CaptureTab stored on the tab, and NavigateTo's
        /// silent-refresh path (Browse.cs), which feeds it the LIVE selection it read one statement
        /// before the refill destroyed it. The stored paths are no use to the second of those - see
        /// RefreshBrowsingTab - but putting rows back by path is identical work either way.
        /// </remarks>
        private void ApplySelectionByPath(FilePane pane, SearchTab t,
                                          System.Collections.Generic.ICollection<string> paths)
        {
            if (paths.Count == 0) return;

            var list = pane.ResultsList;

            // The pane moved on between the dispatch and now - another tab was activated in it,
            // or this tab was dragged into the other pane. Its selection is no longer this tab's
            // business, and forcing it would fight whatever is showing.
            if (!ReferenceEquals(list.ItemsSource, t.Results)) return;

            // OrdinalIgnoreCase because these are Windows paths, and a row can come back from a
            // re-listing cased differently than the string that was stored.
            var wanted = new System.Collections.Generic.HashSet<string>(
                paths, StringComparer.OrdinalIgnoreCase);

            _restoringSelection = true;
            try
            {
                list.SelectedItems.Clear();
                // Every match, not just the first: ResultsList is SelectionMode="Extended", so a
                // multi-row selection has to come back as a multi-row selection.
                foreach (var r in t.Results)
                    if (wanted.Contains(r.FilePath)) list.SelectedItems.Add(r);
            }
            finally { _restoringSelection = false; }

            // Repainted explicitly rather than left to the SelectionChanged that was just
            // suppressed, so the strip ends up describing what is now selected instead of the
            // empty list the re-bind - or the refill - left behind. Animated like any other
            // selection change - by
            // the time this runs the re-bind's own collapse has already played, and snapping the
            // strip open on top of it reads worse than letting it grow.
            UpdateDetailsPaneForSelection(pane);   // DetailsPane.cs - no-ops while the strip is closed
        }

        /// <summary>
        /// True only while a selection is being put back on a results list rather than made by the
        /// user: ApplySelectionByPath adding the rows one at a time, and the clear-and-refill in
        /// NavigateTo that a carried selection is about to survive (Browse.cs). Read by
        /// ResultsList_SelectionChanged (ResultsView.cs) and UpdateDetailsPaneForSelection
        /// (DetailsPane.cs) - see the remark on RestoreTabSelection for why both stand down.
        /// </summary>
        private bool _restoringSelection;

        /// <summary>
        /// Light the rail icon of every tool that has a tab open in a live pane, and unlight the
        /// ones that do not, through the same Tag="on" accent the search, bookmarks and dual-pane
        /// toggles use (RailButton, Controls.xaml).
        /// </summary>
        // Recomputed from the tabs themselves on every activation and every close, rather than
        // switched on in each tool's Open... and off in its Close...: per-open/per-close
        // bookkeeping has to be right at a dozen call sites AND in every path that moves a tab
        // between panes or windows (PaneDrag.cs, TabTearOut.cs, TabHandoff.cs, session restore),
        // and one missed path leaves an icon lit with nothing behind it or dark with a tab still
        // open. Asking the panes what they hold cannot drift, and the walk is a couple of
        // collections of a handful of items.
        //
        // The question is only "does a tab of this kind exist anywhere in the window" - NOT
        // whether it is the active tab, and not whether it is in the focused pane. A Task Manager
        // sitting behind another tab, or in the other pane, is still open; an icon that went dark
        // every time you clicked a different tab would read as the tool having closed.
        //
        // LivePanes() (Panes.cs) is what makes "in the window" mean on screen: while the split is
        // shut RightPane keeps its tabs but shows none of them, so it yields only LeftPane.
        // Reopening the split runs FocusPane -> ActivateTab, which lands back here.
        private void UpdateToolRailLights()
        {
            bool procs = false, events = false, perf = false, registry = false, storage = false;

            foreach (var pane in LivePanes())
                foreach (var t in pane.Tabs)
                {
                    // Each Is* is "this tab's control is not null" (Models/SearchTab.cs) - the
                    // same tell ActivateTab's ApplyXView calls and the tab-strip dot read.
                    procs    |= t.IsProcessList;
                    events   |= t.IsEventViewer;
                    perf     |= t.IsPerformanceMonitor;
                    registry |= t.IsRegistryEditor;
                    storage  |= t.IsStorageAnalyzer;
                }

            // null, not "off": the trigger fires on the literal string "on" and treats everything
            // else as the unlit default, and null is what the other rail toggles clear to.
            TaskManagerRailBtn.Tag    = procs    ? "on" : null;
            EventViewerRailBtn.Tag    = events   ? "on" : null;
            PerformanceRailBtn.Tag    = perf     ? "on" : null;
            RegistryEditorRailBtn.Tag = registry ? "on" : null;
            StorageRailBtn.Tag        = storage  ? "on" : null;
        }

        private void SwitchToTab(SearchTab t)
        {
            if (t == _active) return;
            // Deliberately instant: blending two result lists reads as flicker.
            CaptureTab(_active);
            ActivateTab(t);
        }

        // ── Pane crossfade (tab CLOSE only) ────────────────────────
        // Closing the active tab yanks its content away, so a short ghost fade
        // softens it. Plain tab switches are instant by design.

        private System.Windows.Media.ImageSource? SnapshotPane()
        {
            if (Pane.ResultsPane.ActualWidth < 1 || Pane.ResultsPane.ActualHeight < 1) return null;
            try
            {
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(Pane.ResultsPane.ActualWidth  * dpi.DpiScaleX),
                    (int)Math.Ceiling(Pane.ResultsPane.ActualHeight * dpi.DpiScaleY),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                    System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(Pane.ResultsPane);
                rtb.Freeze();
                return rtb;
            }
            catch { return null; }   // cosmetic only - the close still happens
        }

        private void RunPaneCrossfade(System.Windows.Media.ImageSource? snap)
        {
            if (snap == null) return;
            Pane.TabFadeGhost.BeginAnimation(OpacityProperty, null);
            Pane.TabFadeGhost.Source     = snap;
            Pane.TabFadeGhost.Opacity    = 1;
            Pane.TabFadeGhost.Visibility = Visibility.Visible;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            };
            fade.Completed += (_, _) =>
            {
                Pane.TabFadeGhost.BeginAnimation(OpacityProperty, null);
                Pane.TabFadeGhost.Visibility = Visibility.Collapsed;
                Pane.TabFadeGhost.Source     = null;
            };
            Pane.TabFadeGhost.BeginAnimation(OpacityProperty, fade);
        }

        private void NewTab_Click(object sender, RoutedEventArgs e)
        {
            CaptureTab(_active);
            ActivateTab(CreateTab());

            // A tab is a place now, not a blank search form. Opening at Home means the location
            // row is never empty and the results pane never starts as a prompt (AddressBar.cs).
            _ = NavigateTo(HomeFolder);   // Browse.cs
        }

        internal void Tab_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Left-click switching happens on mouse-UP (Tab_DragUp) so a press can
            // begin a drag without switching first - the KillerPDF tab physics.
            if (sender is not FrameworkElement fe || fe.DataContext is not SearchTab t) return;
            if (e.ChangedButton == System.Windows.Input.MouseButton.Middle) { CloseTab(t); e.Handled = true; }
        }

        internal void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is SearchTab t) CloseTab(t);
        }

        private void CloseActiveTab_Click(object sender, RoutedEventArgs e) => CloseTab(_active);

        private void CloseTab(SearchTab t)
        {
            // The one close that can destroy something: unsaved typing exists nowhere else, so
            // a document tab gets asked first (EditorTabs.cs). Ahead of the fade, because the
            // dialog has to be able to call the whole thing off.
            if (!ConfirmDiscard(t)) return;

            t.Cts?.Cancel();   // stop its search; the engine winds down gracefully

            // Fade the tab CHIP out first (when the bar is visible), then remove.
            var cont = TabContainer(t);
            if (cont != null && _tabs.Count > 1)
            {
                cont.IsHitTestVisible = false;   // no clicks on a dying tab
                var chipFade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(110))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
                };
                chipFade.Completed += (_, _) => FinishCloseTab(t);
                cont.BeginAnimation(OpacityProperty, chipFade);
                return;
            }
            FinishCloseTab(t);
        }

        private void FinishCloseTab(SearchTab t)
        {
            if (!_tabs.Contains(t)) return;   // guard against a double-fire

            CloseTerminal(t);     // TerminalTabs.cs    - a shell tab has a pty to end
            CloseEditor(t);       // EditorTabs.cs      - a document tab has a control to unhook
            CloseProcessList(t);       // ProcessTabs.cs     - a Task Manager tab has a refresh timer to stop
            CloseEventViewer(t);       // EventViewerTabs.cs - an Event Viewer tab has a background load to cancel
            ClosePerformanceMonitor(t);// PerformanceTabs.cs - a Performance tab has a refresh timer and counters to stop
            CloseRegistryEditor(t);    // RegistryEditorTabs.cs - a Registry Editor tab has a status-clear timer to stop
            CloseStorageAnalyzer(t);   // StorageTabs.cs     - a Storage tab may have a scan mid-walk to cancel

            // Only closing the ACTIVE tab changes what the pane shows - fade that.
            var snap = t == _active ? SnapshotPane() : null;

            int idx = _tabs.IndexOf(t);
            _tabs.Remove(t);

            // Here, not at the tail: closing a tab that was NOT the active one falls straight
            // through to UpdateTabBar without ever reaching ActivateTab, and two of the branches
            // below return early. This is the first point where the tab is out of the pane's
            // collection, which is what the walk asks about, so it is correct wherever the close
            // goes next. The branches that do re-activate simply run it a second time.
            UpdateToolRailLights();

            if (_tabs.Count == 0)
            {
                // Closing the LAST tab of the second pane closes the pane. A pane with nothing
                // in it is not a thing you can be looking at, and handing back a fresh blank tab
                // instead left the only way out of dual pane being to go and find the toggle.
                // The first pane keeps the always-one-tab rule - there is nothing to fall back
                // to there. CloseSecondPane moves focus to the survivor before it hides this one.
                if (DualPane && ReferenceEquals(Pane, RightPane))
                {
                    CloseSecondPane();   // DualPane.cs
                    return;
                }

                ActivateTab(CreateTab());
                RunPaneCrossfade(snap);
                return;
            }
            if (t == _active)
            {
                ActivateTab(_tabs[Math.Min(idx, _tabs.Count - 1)]);
                RunPaneCrossfade(snap);
            }
            UpdateTabBar();
        }

        // ── Keyboard tab navigation (Ctrl+Tab / Ctrl+Shift+Tab / Ctrl+1-9) ──
        private void CycleTab(int dir)
        {
            if (_tabs.Count < 2) return;
            int idx = (_tabs.IndexOf(_active) + dir + _tabs.Count) % _tabs.Count;
            SwitchToTab(_tabs[idx]);
        }

        private void JumpToTab(int oneBased)
        {
            if (_tabs.Count == 0) return;
            int idx = oneBased >= 9 ? _tabs.Count - 1 : oneBased - 1;   // Ctrl+9 = last, browser-style
            if (idx >= 0 && idx < _tabs.Count) SwitchToTab(_tabs[idx]);
        }

        // ═══════════════════════════════════════════════════════════
        //  TAB STRIP PHYSICS (ported from KillerPDF Tabs.cs, adapted to the
        //  ItemsControl strip): arm on press; past the threshold the grabbed tab
        //  glues to the cursor and neighbors glide aside as it crosses their
        //  layout-slot midpoints. A plain click still switches on release.
        // ═══════════════════════════════════════════════════════════
        private SearchTab? _tabDragTab;
        private Point  _tabDragStart;
        private double _tabGrabDX;
        private bool   _tabDragging;

        private FrameworkElement? TabContainer(SearchTab t)
            => Pane.TabStrip.ItemContainerGenerator.ContainerFromItem(t) as FrameworkElement;

        private static bool InsideButton(object src)
        {
            var d = src as DependencyObject;
            while (d != null && d is not Button && d is not Window)
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            return d is Button;
        }

        private static double LayoutMidX(FrameworkElement fe)
        {
            var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(fe);
            return slot.X + slot.Width / 2;
        }

        private static void SetTabOffsetX(FrameworkElement tab, double x)
        {
            if (tab.RenderTransform is not System.Windows.Media.TranslateTransform tt)
            {
                tt = new System.Windows.Media.TranslateTransform();
                tab.RenderTransform = tt;
            }
            tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            tt.X = x;
        }

        private static void AnimateTabSlide(FrameworkElement? tab, double fromX)
        {
            if (tab == null) return;
            if (tab.RenderTransform is not System.Windows.Media.TranslateTransform tt)
            {
                tt = new System.Windows.Media.TranslateTransform();
                tab.RenderTransform = tt;
            }
            tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            var anim = new System.Windows.Media.Animation.DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            };
            tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
        }

        internal void Tab_DragDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement bd || bd.DataContext is not SearchTab t) return;
            if (InsideButton(e.OriginalSource)) return;   // the close x handles its own click
            _tabDragTab   = t;
            _tabDragStart = e.GetPosition(Pane.TabStrip);
            _tabGrabDX    = e.GetPosition(bd).X;
            _tabDragging  = false;
            bd.CaptureMouse();
            e.Handled = true;
        }

        internal void Tab_DragMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is not FrameworkElement bd || !bd.IsMouseCaptured || _tabDragTab is null) return;
            var cont = TabContainer(_tabDragTab);
            if (cont == null) return;

            double x = e.GetPosition(Pane.TabStrip).X;
            if (!_tabDragging && Math.Abs(x - _tabDragStart.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            _tabDragging = true;
            Panel.SetZIndex(cont, 3);   // grabbed tab rides above its neighbors

            // Over the other pane the real tab cannot follow the hand - it is still parked in
            // the strip it came from - so a ghost takes over and the reorder stands down
            // (PaneDrag.cs). Coming back into this pane hands control straight back.
            var over = DropTargetPane(e);
            UpdateDragFeedback(_tabDragTab, e, over);
            if (over != null) return;

            // Cleared the window's own frame entirely? Tell whichever OTHER KillerShell window
            // is under the pointer to light up its own drop caret, live (TabHandoff.cs) - the
            // cross-window twin of the ghost/caret feedback UpdateDragFeedback just gave for the
            // in-process other-pane case above.
            UpdateCrossWindowHover(e);   // TabHandoff.cs

            int cur = _tabs.IndexOf(_tabDragTab);
            double slide   = cont.ActualWidth + 1;               // +1 = tab margin gap
            double rawLeft = x - _tabGrabDX;
            double leftEdge  = rawLeft;
            double rightEdge = rawLeft + cont.ActualWidth;
            double maxLeft = Math.Max(0, Pane.TabStrip.ActualWidth - slide);
            double renderLeft = Math.Min(Math.Max(0, rawLeft), maxLeft);

            // Swap when the ADVANCING edge crosses a neighbor's layout-slot midpoint
            // (edge-vs-midpoint gives natural hysteresis, no bounce).
            bool swapped = false;
            if (cur + 1 < _tabs.Count && TabContainer(_tabs[cur + 1]) is { } right && rightEdge > LayoutMidX(right))
            {
                _tabs.Move(cur + 1, cur);
                AnimateTabSlide(TabContainer(_tabs[cur]), slide);    // it jumped left; glide it in from the right
                swapped = true;
            }
            else if (cur - 1 >= 0 && TabContainer(_tabs[cur - 1]) is { } left && leftEdge < LayoutMidX(left))
            {
                _tabs.Move(cur - 1, cur);
                AnimateTabSlide(TabContainer(_tabs[cur]), -slide);   // it jumped right; glide it in from the left
                swapped = true;
            }

            if (swapped) Pane.TabStrip.UpdateLayout();
            var dragged = TabContainer(_tabDragTab);
            if (dragged == null) return;
            var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(dragged);
            SetTabOffsetX(dragged, renderLeft - slot.X);
        }

        internal void Tab_DragUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement bd || !bd.IsMouseCaptured) return;
            bd.ReleaseMouseCapture();
            bool wasDragging = _tabDragging;
            var  t = _tabDragTab;
            _tabDragTab  = null;
            _tabDragging = false;
            HideDragFeedback();       // PaneDrag.cs - the ghost goes whatever the drop turns out to be
            ClearCrossWindowHover();  // TabHandoff.cs - and so does any OTHER window's caret

            if (!wasDragging)
            {
                if (t != null) SwitchToTab(t);
                return;
            }

            // Dropped over the OTHER pane? Then this was a move, not a reorder (PaneDrag.cs).
            // Checked on release rather than mid-drag on purpose: moving a tab between panes
            // rebuilds its container, which would pull the mouse capture out from under the
            // drag that is still running.
            if (t != null && DropTargetPane(e) is { } target)
            {
                MoveTabToPane(t, target, e);
                return;
            }

            // Let go outside the window entirely? Browser-style, same as a browser tab: land on
            // another KillerShell window and the tab MERGES into it (TabHandoff.cs); land on
            // nothing recognized and it tears out into a new window instead (TabTearOut.cs).
            // Checked after the other-pane case for the same reason as above - and because a
            // drop truly outside the window can never also land on the other pane.
            if (t != null && OutsideWindow(e))
            {
                var screenPt = PointToScreen(e.GetPosition(this));
                var otherHwnd = FindOtherKillerShellWindowAt(screenPt);   // TabHandoff.cs
                if (otherHwnd != IntPtr.Zero) MergeTabIntoWindow(t, otherHwnd);   // TabHandoff.cs
                else TearOutTab(t);                                              // TabTearOut.cs
                return;
            }

            UpdateTabBar();   // a reorder may have moved the active tab on/off the corner

            // Settle the grabbed tab from its dragged offset into its final slot.
            var cont = t != null ? TabContainer(t) : null;
            if (cont?.RenderTransform is System.Windows.Media.TranslateTransform tt && Math.Abs(tt.X) > 0.5)
            {
                var settle = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(120))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase
                        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
                };
                settle.Completed += (_, _) => CleanupTabTransforms();
                tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, settle);
            }
            else CleanupTabTransforms();
        }

        private void CleanupTabTransforms()
        {
            foreach (var tab in _tabs)
                if (TabContainer(tab) is { } c)
                {
                    c.RenderTransform = null;
                    Panel.SetZIndex(c, 0);
                }
        }
    }
}
