using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using KillerShell.Models;
using KillerShell.Tools;

// Session concerns: the portable Install flow, the smart-Esc quit prompt, and tab
// persistence to the registry. Partial of MainWindow.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        // ═══════════════════════════════════════════════════════════
        //  PORTABLE INSTALL (ported from KillerScan)
        // ═══════════════════════════════════════════════════════════
        private void Install_Click(object sender, RoutedEventArgs e)
        {
            // Already installed machine-wide (by an admin, winget, choco or an RMM)? Then the
            // all-users box is pre-ticked and locked: installing per-user alongside it would
            // leave two copies and two uninstall entries.
            bool machineWide = App.MachineInstallExists();

            var dlg = new ConfirmDialog(
                Loc("Str_Dlg_InstallMsg"), Loc("Str_Dlg_InstallBullets"), Loc("Str_Btn_DoInstall"),
                Loc("Str_Chk_Desktop"), check1Initial: true,
                Loc("Str_Chk_AllUsers"), check2Initial: machineWide) { Owner = this };
            if (machineWide) dlg.LockCheck2(Loc("Str_Dlg_AlreadyAllUsers"));
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            PortableBadge.Visibility = Visibility.Collapsed;
            SaveTabsOnExit();

            // InstallAndRelaunch ends in Application.Current.Shutdown(), and OnClosing VETOES that
            // unless the close is already confirmed: stage 1 sets e.Cancel and queues the quit
            // dialog, so clicking Install popped an unexpected "quit? close my tabs?" prompt and
            // left the portable window open instead of handing over to the installed copy. The
            // session was already written by SaveTabsOnExit above, so there is nothing left to ask
            // about - confirm the close up front, exactly as the IsElevated guard does on the same
            // two stages. Stage 2's 140ms fade still runs, so the hand-off keeps its usual look.
            _closeConfirmed = true;

            // An all-users install needs UAC. If that is declined the app carries on running as
            // it was, so put the badge back rather than leaving the UI mid-install.
            if (!App.InstallAndRelaunch(wantDesktop: dlg.Check1Checked, allUsers: dlg.Check2Checked))
            {
                _closeConfirmed = false;   // still running: the quit prompt must come back
                PortableBadge.Visibility = Visibility.Visible;
                SetStatus(Loc("Str_St_InstallCancelled"));
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  SMART QUIT (Esc key AND the window close button)
        // ═══════════════════════════════════════════════════════════
        private bool _closeConfirmed;

        private void ConfirmedClose() { _closeConfirmed = true; Close(); }

        // Esc path: a remembered "stay" swallows the key, a remembered "quit" closes.
        private void RequestQuit()
        {
            // An elevated window just goes. See the note on the same guard in OnClosing.
            if (IsElevated) { ConfirmedClose(); return; }   // Elevation.cs

            string remembered = Services.ThemeManager.GetSetting("EscQuit") ?? string.Empty;
            if (remembered == "stay") return;
            if (remembered == "quit") { ConfirmedClose(); return; }
            if (ShowQuitDialog()) ConfirmedClose();
        }

        // X button / Alt+F4 / caption-menu Close land here without RequestQuit, so the
        // quit prompt (close tabs? remember?) runs for them too. A remembered choice
        // skips the dialog - and note "stay" only suppresses the ESC path; the close
        // button is already an explicit quit, so it always closes. The final close
        // fades the window out first (same 140ms language as the content fade-in).
        private bool _fadeOutDone;

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Stage 1: the quit prompt (unless confirmed, remembered, demo, or ELEVATED).
            //
            // The admin window never prompts. Both checkboxes are about the session - "close my
            // open tabs" and "remember this choice" - and an elevated window does not write the
            // session back at all (see the guard in MainWindow's Closing handler), so every
            // answer it could give is discarded. Asking a question whose answer is thrown away
            // is worse than not asking: it implies the tabs in front of you will be remembered,
            // and they will not. It is also a window you opened to run one command and close.
            if (!_closeConfirmed && !DemoMode && !IsElevated &&
                string.IsNullOrEmpty(Services.ThemeManager.GetSetting("EscQuit")))
            {
                e.Cancel = true;
                Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (ShowQuitDialog()) ConfirmedClose();
                }));
                base.OnClosing(e);
                return;
            }

            // Stage 2: fade the window out once, then really close.
            if (!_fadeOutDone)
            {
                e.Cancel        = true;
                _closeConfirmed = true;   // never re-prompt after the fade starts

                // Give any open Processes tab's background owner-lookup thread, and any open
                // Event Viewer tab's background log read, a chance to stop cleanly before the
                // window (and the process behind it) goes away - see the remark on
                // ProcessListControl.Shutdown() for the crash this prevents.
                ShutdownAllProcessLists();       // ProcessTabs.cs
                ShutdownAllEventViewers();       // EventViewerTabs.cs
                ShutdownAllPerformanceMonitors();// PerformanceTabs.cs
                ShutdownAllRegistryEditors();    // RegistryEditorTabs.cs
                ShutdownAllStorageAnalyzers();   // StorageTabs.cs
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
                };
                fade.Completed += (_, _) => { _fadeOutDone = true; Close(); };
                BeginAnimation(OpacityProperty, fade);
                base.OnClosing(e);
                return;
            }

            base.OnClosing(e);
        }

        /// <summary>Shows the quit dialog, persists both checkboxes, returns confirmed.
        /// The tabs checkbox is OPT-OUT: unchecked (default) keeps/remembers open tabs;
        /// ticking "Close my open tabs" is what forgets them.</summary>
        private bool ShowQuitDialog()
        {
            var dlg = new ConfirmDialog(
                Loc("Str_Dlg_QuitMsg"), null, Loc("Str_Btn_Quit"),
                Loc("Str_Chk_CloseTabs"), Services.ThemeManager.GetSetting("RememberTabs") == "0",
                Loc("Str_Chk_RememberChoice")) { Owner = this };
            dlg.ShowDialog();

            Services.ThemeManager.SetSetting("RememberTabs", dlg.Check1Checked ? "0" : "1");
            if (dlg.Check2Checked)
                Services.ThemeManager.SetSetting("EscQuit", dlg.Confirmed ? "quit" : "stay");
            return dlg.Confirmed;
        }

        // ═══════════════════════════════════════════════════════════
        //  TAB SESSION PERSISTENCE
        //  Tabs persist as one registry string: one line per tab, fields | separated,
        //  free-text fields URI-escaped so user patterns can't break the format.
        // ═══════════════════════════════════════════════════════════
        private void SaveTabsOnExit()
        {
            // Opt-out model: tabs are remembered UNLESS the user ticked "Close my
            // open tabs" (RememberTabs == "0"). Unset means remember.
            if (Services.ThemeManager.GetSetting("RememberTabs") == "0")
            {
                Services.ThemeManager.SetSetting("Tabs", string.Empty);
                return;
            }
            CaptureTab(_active);
            var lines = new List<string>();
            foreach (var t in _tabs)
            {
                // A shell, a document, a Processes tab, an Event Viewer tab and a Performance tab
                // all have LIVE state - a pty, an open buffer, a refresh timer, a loaded log, a
                // window of sparkline history - that a Title/RootPath/pattern blob cannot carry,
                // so saving one the same way a browse/search tab is saved below produced a
                // placeholder that LOOKED like the real thing on restore but was not: no control,
                // no glyph, IsProcessList/IsTerminal/IsEditor/IsEventViewer/IsPerformanceMonitor
                // all false. For Processes specifically that is what caused two "Processes" tabs
                // to exist at once (2026-08-02) - OpenTaskManager's singleton check scans
                // for IsProcessList == true, so it walked straight past the broken restored one
                // and opened a second, real one right next to it. A Performance tab's live
                // counters and hardware inventory have no meaningful "restore" state either way -
                // there is nothing to resurrect, only something to reopen fresh - so it follows
                // the same rule from the start rather than needing a follow-up fix.
                //
                // What DOES restore these five well is the same idea TabTearOut.cs already uses
                // for moving a tab to a brand new window: reopen it fresh rather than try to
                // resurrect its exact state. BuildRelaunchArgs is that same builder (a shell tab
                // comes back as a fresh shell in the same folder, a document as the same file
                // reopened, Processes as a fresh Processes tab, Event Viewer as a fresh Event
                // Viewer tab, Performance as a fresh Performance tab), marked with a leading \x01
                // so TryRestoreTabs can tell it apart from the classic 11-field shape below without
                // the two ever colliding - Uri.EscapeDataString never emits a raw \x01 byte, so no
                // real Title can produce one by accident.
                if (t.IsTerminal || t.IsEditor || t.IsProcessList || t.IsEventViewer || t.IsPerformanceMonitor
                    || t.IsRegistryEditor || t.IsStorageAnalyzer)
                {
                    string? flags = BuildRelaunchArgs(t, out _);   // TabTearOut.cs
                    if (flags != null) lines.Add("\x01" + flags);
                    // An untitled or unsaved document has nothing safe to reopen - BuildRelaunchArgs
                    // returns null for exactly that case, so it is simply dropped, same as a tear-out
                    // of the same tab would refuse rather than lose the edit.
                    continue;
                }

                string terms = string.Join(";", t.Groups.SelectMany(g => g.Terms)
                    .Where(x => !string.IsNullOrWhiteSpace(x.Pattern))
                    .Select(x => $"{(x.Mode == SearchTerm.SearchMode.Content ? 1 : 0)}~{Uri.EscapeDataString(x.Pattern)}"));
                string filters = string.Join(";", t.Filters.Select(f =>
                    $"{f.FieldIndex}~{f.ConditionIndex}~{Uri.EscapeDataString(f.Text)}~{(f.Date.HasValue ? f.Date.Value.Ticks.ToString() : "")}~{Uri.EscapeDataString(f.SizeText)}~{f.UnitIndex}"));
                // The browse fields are APPENDED, never inserted: the nine original fields keep
                // their indices, so a blob written by an older build still parses. They are what
                // makes a folder tab come back as a folder tab - CaptureTab stores a browsed
                // folder in RootPath as well, but a tab restored from that alone is a search
                // scoped at the folder, which is how a restored tab came back empty.
                lines.Add(string.Join("|",
                    Uri.EscapeDataString(t.Title), Uri.EscapeDataString(t.RootPath),
                    Uri.EscapeDataString(t.IncludePatterns), Uri.EscapeDataString(t.ExcludePatterns),
                    t.CaseSensitive ? "1" : "0", t.SortIndex.ToString(), t.SortAsc ? "1" : "0",
                    terms, filters,
                    Uri.EscapeDataString(t.CurrentFolder), t.IsBrowsing ? "1" : "0"));
            }
            Services.ThemeManager.SetSetting("Tabs", string.Join("\n", lines));
        }

        private bool TryRestoreTabs()
        {
            if (Services.ThemeManager.GetSetting("RememberTabs") == "0") return false;

            // One-time flush, 2026-08-02. A build from before SaveTabsOnExit stopped writing
            // shell/document/Processes tabs out at all (see the remark there) can leave a row
            // behind that LOOKS restorable field-by-field - a shell tab has set a real,
            // non-empty RootPath since earlier the same day (TerminalTabs.cs, "the tab title is
            // where the shell is"), so the first fix here - drop any row with no folder, no
            // browse flag and no search terms - did not catch it, and the row kept coming back
            // as a control-less, blank-paned tab titled whatever the shell happened to be
            // running. There is no field in the old format that reliably tells a genuine row
            // from a broken one, so guessing harder is not the fix: flush the whole blob exactly
            // once per install and start clean. SessionSchema marks that this has already
            // happened, so it never repeats and never touches tabs saved AFTER this shipped.
            if (Services.ThemeManager.GetSetting("SessionSchema") != "2")
            {
                Services.ThemeManager.SetSetting("Tabs", string.Empty);
                Services.ThemeManager.SetSetting("SessionSchema", "2");
                return false;
            }

            var raw = Services.ThemeManager.GetSetting("Tabs");
            if (string.IsNullOrEmpty(raw)) return false;

            // Each LINE gets its own try/catch (2026-08-03 - a corrupted session restored as an
            // empty folder rather than the previously open tabs). This used to be one catch
            // around the whole loop: one malformed line (a truncated %-escape from a corrupted
            // settings value, say) aborted every tab after it, AND worse, left the line's own
            // half-built SearchTab sitting in _tabs - CreateTab() had already added it before the
            // throw, so a tab with a real Title/RootPath/glyph but everything past the throw
            // still at its constructor default (not browsing, no editor, no terminal - "nothing")
            // could end up as _tabs[0]. ApplyEditorView/ApplyTerminalView/etc. only ever ACT when
            // their own type matches and no-op otherwise, on the assumption exactly one type is
            // ever true - so activating a tab with every type flag false left whatever the
            // PREVIOUS tab's EditorHost/TerminalSlot/ResultsList visibility happened to be
            // (nothing resets it for a type nothing recognizes), while ApplyPaneBars, seeing no
            // type flag set, correctly showed LocationRow - the exact "location row above a
            // blank pane, No item selected" shape that got reported.
            foreach (var line in raw!.Split('\n'))
            {
                SearchTab? t = null;
                try
                {
                    // A shell, document or Processes tab, saved as BuildRelaunchArgs flags
                    // rather than the classic 11-field shape below (see the remark on
                    // SaveTabsOnExit). ApplyHandoff (TabHandoff.cs) is the exact same "reopen
                    // fresh" parser a cross-window drag-merge uses on the receiving end - a
                    // restore is just a merge into the window that is about to be itself.
                    if (line.Length > 0 && line[0] == '\x01')
                    {
                        ApplyHandoff(line[1..]);   // TabHandoff.cs
                        continue;
                    }

                    var p = line.Split('|');
                    if (p.Length < 9) continue;

                    t = CreateTab();
                    t.Title           = Uri.UnescapeDataString(p[0]);
                    t.RootPath        = Uri.UnescapeDataString(p[1]);
                    t.IncludePatterns = Uri.UnescapeDataString(p[2]);
                    t.ExcludePatterns = Uri.UnescapeDataString(p[3]);
                    t.CaseSensitive   = p[4] == "1";
                    t.SortIndex       = int.TryParse(p[5], out int si) ? si : 0;
                    t.SortAsc         = p[6] == "1";

                    // Browse state, appended after the nine original fields. A blob from a build
                    // that predates it is nine fields long and simply restores as a search tab,
                    // which is what it was.
                    if (p.Length >= 11)
                    {
                        t.CurrentFolder = Uri.UnescapeDataString(p[9]);
                        t.IsBrowsing    = p[10] == "1";
                    }

                    var g = t.Groups[0];
                    g.Terms.Clear();
                    foreach (var ts in p[7].Split(';'))
                    {
                        var tp = ts.Split('~');
                        if (tp.Length < 2) continue;
                        g.Terms.Add(new SearchTerm
                        {
                            Mode    = tp[0] == "1" ? SearchTerm.SearchMode.Content : SearchTerm.SearchMode.FileName,
                            Pattern = Uri.UnescapeDataString(tp[1]),
                        });
                    }
                    if (g.Terms.Count == 0) g.Terms.Add(new SearchTerm());

                    foreach (var fs in p[8].Split(';'))
                    {
                        var fp = fs.Split('~');
                        if (fp.Length < 6) continue;
                        var f = new SearchFilter
                        {
                            FieldIndex     = int.TryParse(fp[0], out int fi) ? fi : 0,
                            ConditionIndex = int.TryParse(fp[1], out int ci) ? ci : 0,
                            Text           = Uri.UnescapeDataString(fp[2]),
                            SizeText       = Uri.UnescapeDataString(fp[4]),
                            UnitIndex      = int.TryParse(fp[5], out int ui) ? ui : 0,
                        };
                        if (long.TryParse(fp[3], out long ticks)) f.Date = new DateTime(ticks);
                        t.Filters.Add(f);
                    }
                }
                catch
                {
                    // This one line is corrupt - drop it, and drop the half-built tab it may
                    // already have registered, rather than lose every tab after it (or worse,
                    // leave a "nothing" tab behind for ActivateTab to land on).
                    if (t != null) _tabs.Remove(t);
                }
            }

            if (_tabs.Count == 0) return false;
            ActivateTab(_tabs[0]);
            return true;
        }
    }
}
