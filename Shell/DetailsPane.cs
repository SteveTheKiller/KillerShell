using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using KillerShell.Models;

// The details/preview strip that slides out of the BOTTOM of a results pane (Controls/FilePane.
// xaml, "DetailsPane" - deliberately NOT a side panel like Explorer's). File details on the left,
// an image preview - or the shell's own big icon for everything else - on the right. Partial of
// MainWindow. Open/closed, dragged height, and content are ALL per-pane (FilePane.DetailsPaneOpen/
// DetailsPaneUserSized/DetailsPaneHeight) - each pane tracks its own selection, so there is no
// reason left for the two strips to move together; they used to open and close as a pair when
// they should be independent. Unlike ShowHidden/FoldersOnTop, which genuinely are
// meant to agree across both panes.
//
// Updates once per selection change (FilePane.xaml.cs forwards ResultsList_SelectionChanged
// here), never on a timer - the folder listing's own file-watcher already keeps the rows
// themselves current, so re-reading "whichever file is now selected" is all this needs to do.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        // BitmapImage.DecodePixelWidth cap for the real image preview - a downscaled decode, not
        // a full-resolution load, so scrolling through a folder of large photos never hitches.
        private const int DetailsPreviewPx = 220;

        // The preview column's WIDTH is no longer independently draggable (the old
        // side-splitter proved unhelpful in practice) - it now tracks the strip's own HEIGHT,
        // which is what the horizontal grip below the results list actually resizes. A taller
        // strip reads as wanting a bigger preview, so width = height * ratio; 1.4 is a plain
        // landscape-ish multiplier (wider than tall, like most photos). Its CEILING is no longer
        // a fixed pixel cap - a wide/landscape image should expand until it hits the info on
        // the left - see ApplyDetailsPreviewWidth, which computes the real
        // leftover space instead.
        private const double DetailsPreviewWidthRatio = 1.4;
        private const double DetailsPreviewMinWidth   = 100;

        // What ApplyDetailsPreviewWidth reserves for the fields column so the preview can never
        // crowd it: the label column's own fixed 86px (FilePane.xaml) plus enough for a value to
        // read as more than a sliver. Short values (size/dates) never need it; it exists for a
        // wrapped attributes line or a partially-trimmed path.
        private const double DetailsFieldsMinWidth = 210;
        // DetailsPaneContent's own middle column (FilePane.xaml) - the static 1px divider plus
        // its margin either side.
        private const double DetailsPaneDividerWidth = 16;

        // The strip's thin collapsed state - no reason to eat half the screen for one
        // dash-filled row. Roughly a status-bar line: enough for the "No item
        // selected" text (or nothing at all, for a selected folder) to sit vertically centered.
        private const double DetailsPaneCollapsedHeight = 30;

        // Bounds for the strip's HEIGHT (the thing the new grip actually drags). No fixed max
        // constant - the ceiling is 50% of the results pane's own height (DetailsPaneCeiling),
        // computed live because that pane's height itself varies with the window. The FLOOR is
        // no longer a guessed constant either - the strip should never shrink below what its
        // details actually need, with the grab handle only allowing growth from there
        // - see DetailsPaneContentFloor, which measures the field rows that are actually showing.
        // The pre-layout starting point (before anything real has been measured) is now
        // FilePane.DetailsPaneHeight's own field default (160) rather than a constant here, since
        // the height itself moved onto the pane.
        // Before the results pane has ever laid out (first paint), there is nothing real to take
        // 50% of - same problem BookmarksCeiling has with TreePanel.ActualHeight, same fallback
        // shape: a generous fixed number rather than clamping everything to zero.
        private const double DetailsPaneHeightFallbackCeiling = 400;

        private static readonly HashSet<string> DetailsImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico",
        };

        // Open/closed, user-sized-or-not, and the dragged height itself all live on FilePane now
        // (DetailsPaneOpen/DetailsPaneUserSized/DetailsPaneHeight) - each pane opens, closes and
        // remembers its own. Settings persist per pane under a PaneKey-
        // suffixed key, same convention as the view-state settings in ResultsView.cs.
        private void InitDetailsPane()
        {
            foreach (var p in new[] { LeftPane, RightPane })
            {
                string key = PaneKey(p);

                p.DetailsPaneOpen = Services.ThemeManager.GetSetting("DetailsPaneOpen" + key) == "1";
                p.DetailsPaneUserSized = Services.ThemeManager.GetSetting("DetailsPaneUserSized" + key) == "1";

                string? rawHeight = Services.ThemeManager.GetSetting("DetailsPaneHeight" + key);
                if (!string.IsNullOrEmpty(rawHeight) &&
                    double.TryParse(rawHeight, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                {
                    p.DetailsPaneHeight = parsed;   // re-clamped against the live pane in ApplyDetailsPane
                }

                ApplyDetailsPane(p, animate: false);   // no slide-open flash at launch
            }
        }

        internal void DetailsPaneToggle_Click(FilePane pane)
        {
            pane.DetailsPaneOpen = !pane.DetailsPaneOpen;
            Services.ThemeManager.SetSetting("DetailsPaneOpen" + PaneKey(pane), pane.DetailsPaneOpen ? "1" : "0");
            ApplyDetailsPane(pane, animate: true);
        }

        /// <summary>
        /// The horizontal grip at the top of the strip (FilePane.xaml DetailsPaneGrip) is being
        /// dragged. Same Thumb approach as BookmarksGrip (Bookmarks.cs) rather than a GridSplitter
        /// - the strip's Height is also the property the open/close animation drives, and a
        /// splitter would be fighting that. Dragging UP grows the strip, so the delta is
        /// subtracted: a Thumb reports downward movement as positive.
        /// </summary>
        internal void DetailsPaneGrip_DragDelta(FilePane pane, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (!pane.DetailsPaneOpen) return;

            pane.DetailsPaneUserSized = true;

            pane.DetailsPaneHeight = ClampDetailsPaneHeight(pane, pane.DetailsPane.ActualHeight - e.VerticalChange);

            pane.DetailsPane.BeginAnimation(FrameworkElement.HeightProperty, null);
            pane.DetailsPane.Height = pane.DetailsPaneHeight;
            ApplyDetailsPreviewWidth(pane, pane.DetailsPaneHeight);   // recompute live, every tick of the drag
        }

        internal void DetailsPaneGrip_DragCompleted(FilePane pane, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            string key = PaneKey(pane);
            Services.ThemeManager.SetSetting("DetailsPaneHeight" + key,
                pane.DetailsPaneHeight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            Services.ThemeManager.SetSetting("DetailsPaneUserSized" + key, "1");
            // No ForEachPane here any more - only the pane whose grip actually moved changes.
        }

        /// <summary>
        /// How tall the strip is allowed to get for this pane: never more than half of the whole
        /// results pane, so the file listing
        /// above always keeps at least the other half.
        /// </summary>
        private double DetailsPaneCeiling(FilePane pane)
            => pane.ResultsPane.ActualHeight > 0 ? pane.ResultsPane.ActualHeight * 0.5 : DetailsPaneHeightFallbackCeiling;

        /// <summary>
        /// Measures just the FIELD content (or the empty-state text) exactly as it stands right
        /// now - whichever of Empty/Single/Multi just painted it - and returns the exact height
        /// its rows need. This is the real floor the drag grip can never go below - the strip
        /// must not shrink smaller than its visible details, the grab handle only allows
        /// growth - recomputed on every call rather than cached,
        /// since a different file's populated fields (a wrapped attributes line, a longer path)
        /// need a different floor.
        ///
        /// Deliberately measures ONLY DetailsFieldsGrid/DetailsEmptyText, never the preview
        /// column (DetailsPreviewImage/DetailsPreviewIcon) or the DetailsPaneContent grid as a
        /// whole - the preview is decorative and adapts to whatever height it is given
        /// (Stretch="Uniform"), it never NEEDS a height. Measuring it too was the runaway-growth
        /// bug: with the preview column's width fixed but height passed as
        /// double.PositiveInfinity, a Uniform-stretch Image scales to fill that width and lets
        /// its height run free - desiredHeight = availWidth / aspectRatio - so a portrait or tall
        /// image measured multiple thousand pixels tall, and that reading became the "floor",
        /// which ClampDetailsPaneHeight then had no way to shrink back down from.
        ///
        /// +20 accounts for DetailsPaneContent's own top+bottom Margin (14,10,14,10 in
        /// FilePane.xaml), which is included automatically when DetailsPaneContent itself is
        /// measured but not when a child of it is measured directly. +1 rounds long, never
        /// short, same as every other Measure()-based estimate here.
        /// </summary>
        private double DetailsPaneContentFloor(FilePane pane)
        {
            FrameworkElement content = pane.DetailsFieldsGrid.Visibility == Visibility.Visible
                ? (FrameworkElement)pane.DetailsFieldsGrid
                : pane.DetailsEmptyText;

            double width = content.ActualWidth > 0
                ? content.ActualWidth
                : (pane.DetailsPane.ActualWidth > 0 ? pane.DetailsPane.ActualWidth : double.PositiveInfinity);

            content.Measure(new Size(width, double.PositiveInfinity));
            return Math.Ceiling(content.DesiredSize.Height) + 20 + 1;
        }

        /// <summary>
        /// Clamps a strip height to the results-pane-derived ceiling and the MEASURED content
        /// floor - the same shape as Bookmarks.ClampBookmarks, letting the floor come down to
        /// meet the ceiling on a very short window rather than forcing a height that cannot fit.
        /// </summary>
        private double ClampDetailsPaneHeight(FilePane pane, double h)
        {
            double contentFloor = DetailsPaneContentFloor(pane);
            double ceiling = Math.Max(contentFloor, DetailsPaneCeiling(pane));
            double floor = Math.Min(contentFloor, ceiling);
            return Math.Max(floor, Math.Min(ceiling, h));
        }

        /// <summary>
        /// Sets the preview column's width from the strip's current height - the width-follows-
        /// height relationship the drag replaces the old side-splitter with. The ceiling is no
        /// longer a fixed pixel cap - a wide/landscape image should expand until it hits the
        /// info on the left - it is whatever is actually left of the strip's
        /// width once the fields column has its reserved minimum, so a wide pane genuinely lets
        /// a landscape image grow. Stretch="Uniform" on the Image elements (FilePane.xaml) keeps
        /// aspect ratio regardless, so a portrait image still ends up narrower even with room to
        /// spare - that is expected, not a bug.
        /// </summary>
        private void ApplyDetailsPreviewWidth(FilePane pane, double stripHeight)
        {
            double preferred = Math.Max(DetailsPreviewMinWidth, stripHeight * DetailsPreviewWidthRatio);

            double totalContentWidth = pane.DetailsPaneContent.ActualWidth;
            double max = totalContentWidth > 0
                ? Math.Max(DetailsPreviewMinWidth, totalContentWidth - DetailsFieldsMinWidth - DetailsPaneDividerWidth)
                : preferred;   // before the first layout pass there is nothing real to measure against

            pane.DetailsPreviewCol.Width = new GridLength(Math.Min(max, preferred));
        }

        /// <summary>Whether the strip currently has nothing meaningful to show - no selection, or
        /// a folder (ShowDetailsEmpty covers both, see UpdateDetailsPaneForSelection). Read
        /// straight off which content block is visible, so this can never disagree with what was
        /// just painted.</summary>
        private static bool IsDetailsPaneEmpty(FilePane pane) => pane.DetailsEmptyText.Visibility == Visibility.Visible;

        /// <summary>
        /// The height the strip wants when it DOES have something meaningful to show: the user's
        /// last-dragged height once they have ever touched the grip, otherwise the live measured
        /// content height - the same estimate ApplyDetailsPane always used, now doing double duty
        /// as the drag floor too (DetailsPaneContentFloor/ClampDetailsPaneHeight).
        /// </summary>
        private double NormalDetailsPaneHeight(FilePane pane)
            => pane.DetailsPaneUserSized
             ? ClampDetailsPaneHeight(pane, pane.DetailsPaneHeight)
             : ClampDetailsPaneHeight(pane, DetailsPaneContentFloor(pane));

        /// <summary>
        /// Grows or shrinks the strip between its thin collapsed line and its normal (dragged or
        /// measured) height, purely from whether the content just painted is meaningful
        /// (select nothing or a folder -> one thin line; select a file -> back to the
        /// normal/last-dragged height). Independent of DetailsPaneUserSized - a user-dragged
        /// height is remembered and restored, but the collapse itself is automatic and never
        /// needs a manual resize.
        /// </summary>
        private void SyncDetailsPaneCollapse(FilePane pane, bool animate)
        {
            if (!pane.DetailsPaneOpen || pane.DetailsPane.Visibility != Visibility.Visible) return;

            // The collapsed target is ALSO the real measured floor, not the bare constant - the
            // constant is only a sanity minimum for a degenerate pre-layout measurement, so the
            // "No item selected" line (or the empty state's own margins) is never clipped either.
            bool empty = IsDetailsPaneEmpty(pane);
            double target = empty
                ? Math.Max(DetailsPaneCollapsedHeight, DetailsPaneContentFloor(pane))
                : NormalDetailsPaneHeight(pane);

            if (empty == pane.DetailsPaneCollapsed && Math.Abs(pane.DetailsPane.ActualHeight - target) < 0.5) return;

            pane.DetailsPaneCollapsed = empty;
            ApplyDetailsPreviewWidth(pane, target);
            AnimateDetailsPane(pane, target, animate, collapseWhenDone: false);
        }

        /// <summary>Shows/hides one pane's strip and (re)paints it for that pane's own selection.</summary>
        /// <remarks>
        /// The strip only ever belongs to a FILE LISTING - it describes the pane's file selection,
        /// which a terminal, a document, Task Manager, Event Viewer, Performance and Registry
        /// Editor do not have. This is the one place that owns DetailsPane.Visibility, so the guard
        /// belongs HERE rather than in a caller.
        ///
        /// It was in a caller: ApplyPaneToolbarMode(true) collapses the whole ListingOnlyTools set
        /// on a shell tab. That works right up until anything calls ApplyDetailsPane afterwards -
        /// InitDetailsPane at startup, or DualPane's per-pane push when the split opens - because
        /// this method looked only at DetailsPaneOpen and cheerfully put the strip back. With the
        /// setting remembered as open from a previous session, a shell tab came up carrying a
        /// "No item selected" strip along its bottom edge, and it survived three attempts at
        /// fixing it in the callers.
        ///
        /// ResultsList.Visibility is the test rather than the tab kind: every non-listing kind
        /// collapses it on activation and all of them run before this, so it cannot drift from
        /// them the way an enumerated list of kinds would.
        /// </remarks>
        private void ApplyDetailsPane(FilePane pane, bool animate)
        {
            bool listing = pane.ResultsList.Visibility == Visibility.Visible;

            pane.DetailsPaneBtn.Tag = pane.DetailsPaneOpen ? "on" : null;

            if (pane.DetailsPaneOpen && listing)
            {
                pane.DetailsPane.Visibility = Visibility.Visible;
                // Paints the fields/preview for the current selection, then (below) grows or
                // collapses the strip to match - same call UpdateDetailsPaneForSelection makes on
                // every live selection change, so open and select behave identically.
                UpdateDetailsPaneForSelection(pane, animate);
            }
            else
            {
                AnimateDetailsPane(pane, 0, animate, collapseWhenDone: true);
            }
        }

        private static void AnimateDetailsPane(FilePane pane, double target, bool animate, bool collapseWhenDone)
        {
            var el = pane.DetailsPane;

            void Settle()
            {
                el.BeginAnimation(FrameworkElement.HeightProperty, null);
                el.Height = target;
                if (collapseWhenDone) el.Visibility = Visibility.Collapsed;
            }

            if (!animate) { Settle(); return; }

            var anim = new DoubleAnimation
            {
                From = double.IsNaN(el.ActualHeight) ? 0 : el.ActualHeight,
                To = target,
                Duration = new Duration(TimeSpan.FromMilliseconds(160)),
                EasingFunction = new QuadraticEase { EasingMode = collapseWhenDone ? EasingMode.EaseIn : EasingMode.EaseOut },
            };
            anim.Completed += (_, _) => Settle();
            el.BeginAnimation(FrameworkElement.HeightProperty, anim);
        }

        /// <summary>
        /// Re-measures the strip against its CURRENT content and snaps to that instead of
        /// whatever the open animation guessed - the same estimate-then-correct lesson as the
        /// bookmarks drawer (Bookmarks.cs): a fixed target drifts from a variable-length path or
        /// attributes string, or a preview image with a different aspect ratio than the last one.
        /// Called from DetailsPaneContent's own SizeChanged, and again once the async Created/
        /// Attributes stat and the async image decode land.
        /// </summary>
        internal void CorrectDetailsPaneHeight(FilePane pane)
        {
            if (!pane.DetailsPaneOpen || pane.DetailsPane.Visibility != Visibility.Visible) return;

            // The collapsed thin line is owned entirely by SyncDetailsPaneCollapse - correcting
            // against measured content here would fight that animation every SizeChanged tick.
            if (IsDetailsPaneEmpty(pane)) return;

            // Once the user has dragged the grip, the strip keeps whatever height they chose -
            // but NEVER below what the content actually needs. The old unconditional early
            // return assumed the field rows were fixed height; a long filename WRAPS to a second
            // line, and a user-sized strip then clipped it top and bottom - the filename must
            // never get cut off in the details pane. The chosen height still wins
            // whenever it fits; the measured floor only ever lifts it.
            if (pane.DetailsPaneUserSized)
            {
                double floor = ClampDetailsPaneHeight(pane, DetailsPaneContentFloor(pane));
                if (pane.DetailsPane.ActualHeight >= floor - 0.5) return;
                pane.DetailsPane.BeginAnimation(FrameworkElement.HeightProperty, null);
                pane.DetailsPane.Height = floor;
                ApplyDetailsPreviewWidth(pane, floor);
                return;
            }

            double needed = ClampDetailsPaneHeight(pane, DetailsPaneContentFloor(pane));
            if (Math.Abs(pane.DetailsPane.ActualHeight - needed) < 0.5) return;

            pane.DetailsPane.BeginAnimation(FrameworkElement.HeightProperty, null);
            pane.DetailsPane.Height = needed;
            ApplyDetailsPreviewWidth(pane, needed);
        }

        /// <summary>
        /// Repaints one pane's strip for whatever is now selected in it, then grows or collapses
        /// it to match (SyncDetailsPaneCollapse). No-ops when the strip is closed - no reason to
        /// stat a file or decode a thumbnail nobody can see. A folder gets the same collapsed
        /// treatment as no selection (a folder's dash-filled fields are not
        /// worth the strip's usual height).
        /// </summary>
        internal void UpdateDetailsPaneForSelection(FilePane pane, bool animate = true)
        {
            // Stands down while a selection is being put back rather than made (Tabs.cs
            // ApplySelectionByPath). The rows go in one at a time, so this would repaint the strip
            // - bumping DetailsGen and starting a stat and an image decode - once per row, every
            // time on a part-built selection. That method calls this itself once the whole
            // selection is in, so the strip still ends up describing all of it.
            //
            // The same flag also covers the clear-and-refill of a browsing tab's silent refresh
            // (Browse.cs), where the selection is carried across and lands again a moment later.
            // Without it the empty middle of that refill would reach here and collapse the strip
            // to its empty state, so a selection that never actually went away would blink.
            if (_restoringSelection) return;

            if (!pane.DetailsPaneOpen) return;
            // ...and no-ops on a non-listing tab too, for the same reason ApplyDetailsPane does:
            // this is reachable straight from a selection change, and without the guard it would
            // grow a collapsed strip back open over a terminal.
            if (pane.ResultsList.Visibility != Visibility.Visible) return;

            var list = pane.ResultsList;
            int count = list?.SelectedItems.Count ?? 0;
            int gen = ++pane.DetailsGen;   // invalidates any async stat/decode already in flight

            pane.DetailsPreviewImage.Source = null;
            pane.DetailsPreviewImage.Visibility = Visibility.Collapsed;
            pane.DetailsPreviewIcon.Source = null;
            pane.DetailsPreviewIcon.Visibility = Visibility.Collapsed;

            if (count == 1 && list!.SelectedItem is SearchResult one && !one.IsDirectory)
                ShowDetailsSingle(pane, one, gen);
            else if (count > 1)
                ShowDetailsMulti(pane, [.. list!.SelectedItems.Cast<SearchResult>()]);
            else
                ShowDetailsEmpty(pane);   // nothing selected, or a single folder - nothing meaningful to show

            SyncDetailsPaneCollapse(pane, animate);
        }

        private void ShowDetailsEmpty(FilePane pane)
        {
            pane.DetailsFieldsGrid.Visibility = Visibility.Collapsed;
            pane.DetailsEmptyText.Visibility  = Visibility.Visible;
        }

        private void ShowDetailsSingle(FilePane pane, SearchResult r, int gen)
        {
            pane.DetailsEmptyText.Visibility  = Visibility.Collapsed;
            pane.DetailsFieldsGrid.Visibility = Visibility.Visible;

            pane.DetailsNameText.Text = r.FileName;
            // A folder's own size is not shown without a recursive walk we are not doing here
            // (Explorer either omits it too or computes it lazily) - the size ROW still shows,
            // just with a dash, so the field layout does not jump between a file and a folder.
            pane.DetailsSizeText.Text = r.IsDirectory ? "-" : (r.SizeLabel.Length > 0 ? r.SizeLabel : "0 B");
            pane.DetailsModifiedText.Text = r.ModifiedLabel.Length > 0 ? r.ModifiedLabel : "-";
            pane.DetailsPathText.Text = r.FilePath;
            pane.DetailsTypeText.Text = r.IsDirectory ? Loc("Str_Details_Folder") : DescribeExtension(r.FileName);

            // Created + attributes are not on SearchResult (it is shared with search hits, and
            // eagerly stat-ing two more fields on every row of every search would cost real time
            // for something only ever looked at for ONE selected row at a time). One cheap extra
            // stat here instead, off the UI thread so a slow path - a network share, a removable
            // drive spinning up - cannot hitch the click that selected it.
            pane.DetailsCreatedText.Text = "...";
            pane.DetailsAttrText.Text = "...";
            string path = r.FilePath;
            bool isDir = r.IsDirectory;

            // An archive ENTRY has no file behind it, so there is nothing to stat and nothing
            // to decode: FileInfo and FileStream would both throw on the virtual path and be
            // swallowed by the catches below, which is exception-driven control flow for a
            // case that is known up front. Size and modified are already on the row, straight
            // out of the archive's own directory (Services/ArchiveProvider.cs).
            bool archiveEntry = Services.ArchiveProvider.TrySplit(path, out _, out string arcEntry)
                                && arcEntry.Length > 0;
            if (archiveEntry)
            {
                pane.DetailsCreatedText.Text = "-";
                pane.DetailsAttrText.Text = "-";
                pane.DetailsPreviewIcon.Source = Services.IconCache.For(path, 128, isDir);
                pane.DetailsPreviewIcon.Visibility = Visibility.Visible;
                pane.DetailsPreviewImage.Visibility = Visibility.Collapsed;
                CorrectDetailsPaneHeight(pane);
                return;
            }

            _ = Task.Run(() =>
            {
                DateTime created = default;
                string attrs = string.Empty;
                try
                {
                    FileAttributes fa;
                    if (isDir)
                    {
                        var di = new DirectoryInfo(path);
                        created = di.CreationTime;
                        fa = di.Attributes;
                    }
                    else
                    {
                        var fi = new FileInfo(path);
                        created = fi.CreationTime;
                        fa = fi.Attributes;
                    }
                    attrs = DescribeAttributes(fa);
                }
                catch { /* removed or inaccessible between selection and stat - leave the dash */ }

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != pane.DetailsGen) return;   // a newer selection already landed
                    pane.DetailsCreatedText.Text = created == default ? "-" : created.ToString("yyyy-MM-dd HH:mm");
                    pane.DetailsAttrText.Text = attrs.Length > 0 ? attrs : "-";
                    CorrectDetailsPaneHeight(pane);
                }));
            });

            // Preview: a real downscaled decode for an image file, the shell's own big icon for
            // everything else (including folders). The icon shows immediately as a placeholder
            // under an image too, so the slot is never blank while the decode is in flight.
            pane.DetailsPreviewIcon.Source = Services.IconCache.For(path, 128, isDir);
            pane.DetailsPreviewIcon.Visibility = Visibility.Visible;

            // --demo has no file behind the selected row, so there is nothing at this path to
            // decode: the preview is DRAWN from the path instead (Services/DemoImages.cs), which
            // is the only way this strip can show an actual picture in a capture without reading a
            // real one off the machine. Null for a path that is not one of the fabricated
            // pictures, and the strip then keeps the generic icon it already painted above.
            if (!isDir && IsDetailsImageFile(path))
            {
                _ = Task.Run(() =>
                {
                    BitmapSource? bmp = DemoMode
                        ? Services.DemoImages.Render(path, DetailsPreviewPx)
                        : DecodeDetailsPreview(path, DetailsPreviewPx);
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (gen != pane.DetailsGen || bmp == null) return;
                        pane.DetailsPreviewImage.Source = bmp;
                        pane.DetailsPreviewImage.Visibility = Visibility.Visible;
                        pane.DetailsPreviewIcon.Visibility = Visibility.Collapsed;
                        CorrectDetailsPaneHeight(pane);
                    }));
                });
            }
        }

        private void ShowDetailsMulti(FilePane pane, List<SearchResult> items)
        {
            pane.DetailsEmptyText.Visibility  = Visibility.Collapsed;
            pane.DetailsFieldsGrid.Visibility = Visibility.Visible;

            pane.DetailsNameText.Text = string.Format(Loc("Str_Status_Selected"), items.Count.ToString("N0"));

            // In-memory sum only, never a fresh disk touch: SizeBytes was already stat'd when
            // each row was listed (Browse.cs), so this is cheap even for a big selection.
            long total = 0;
            bool anyFolder = false;
            foreach (var it in items)
            {
                if (it.IsDirectory) { anyFolder = true; continue; }
                total += it.SizeBytes;
            }
            // A trailing "+" when folders are part of the selection: their own contents are not
            // walked, so the true combined total is at least this much, not exactly this much.
            pane.DetailsSizeText.Text = anyFolder ? DetailsFormatBytes(total) + " +" : DetailsFormatBytes(total);

            pane.DetailsModifiedText.Text = "-";
            pane.DetailsCreatedText.Text = "-";
            pane.DetailsAttrText.Text = "-";
            pane.DetailsTypeText.Text = "-";
            pane.DetailsPathText.Text = items[0].Directory;

            // No single icon reads as "these N different files" - left blank rather than picking
            // one file from the selection at random.
        }

        private string DescribeExtension(string fileName)
        {
            string ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext)) return Loc("Str_Details_NoExt");
            return string.Format(Loc("Str_Details_ExtFile"), ext.TrimStart('.').ToUpperInvariant());
        }

        private string DescribeAttributes(FileAttributes fa)
        {
            var bits = new List<string>();
            if ((fa & FileAttributes.ReadOnly) != 0) bits.Add(Loc("Str_Details_AttrReadOnly"));
            if ((fa & FileAttributes.Hidden)   != 0) bits.Add(Loc("Str_Details_AttrHidden"));
            if ((fa & FileAttributes.System)   != 0) bits.Add(Loc("Str_Details_AttrSystem"));
            return string.Join(", ", bits);
        }

        private static bool IsDetailsImageFile(string path) => DetailsImageExtensions.Contains(Path.GetExtension(path));

        /// <summary>
        /// Decodes a downscaled preview off the UI thread. Runs entirely inside the calling
        /// (background) thread; the returned BitmapImage is frozen, so handing it back to the UI
        /// thread afterward is safe. Returns null on any failure (corrupt file, unreadable codec,
        /// file removed mid-decode) rather than throwing - the caller falls back to the icon.
        /// </summary>
        private static BitmapImage? DecodeDetailsPreview(string path, int px)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;      // fully read + decoded before EndInit returns
                bmp.DecodePixelWidth = px;                       // downscale AT decode time, not after
                bmp.StreamSource = fs;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        private static string DetailsFormatBytes(long b)
        {
            if (b <= 0) return "0 B";
            if (b < 1024) return b + " B";
            double kb = b / 1024.0;
            if (kb < 1024) return kb.ToString("0") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.0") + " MB";
            return (mb / 1024.0).ToString("0.00") + " GB";
        }
    }
}
