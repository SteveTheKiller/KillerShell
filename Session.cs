using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using KillerShell.Models;

// Session concerns: the portable Install flow, the smart-Esc quit prompt, and tab
// persistence to the registry. Partial of MainWindow.
namespace KillerShell
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

            // An all-users install needs UAC. If that is declined the app carries on running as
            // it was, so put the badge back rather than leaving the UI mid-install.
            if (!App.InstallAndRelaunch(wantDesktop: dlg.Check1Checked, allUsers: dlg.Check2Checked))
            {
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
                string terms = string.Join(";", t.Groups.SelectMany(g => g.Terms)
                    .Where(x => !string.IsNullOrWhiteSpace(x.Pattern))
                    .Select(x => $"{(x.Mode == SearchTerm.SearchMode.Content ? 1 : 0)}~{Uri.EscapeDataString(x.Pattern)}"));
                string filters = string.Join(";", t.Filters.Select(f =>
                    $"{f.FieldIndex}~{f.ConditionIndex}~{Uri.EscapeDataString(f.Text)}~{(f.Date.HasValue ? f.Date.Value.Ticks.ToString() : "")}~{Uri.EscapeDataString(f.SizeText)}~{f.UnitIndex}"));
                lines.Add(string.Join("|",
                    Uri.EscapeDataString(t.Title), Uri.EscapeDataString(t.RootPath),
                    Uri.EscapeDataString(t.IncludePatterns), Uri.EscapeDataString(t.ExcludePatterns),
                    t.CaseSensitive ? "1" : "0", t.SortIndex.ToString(), t.SortAsc ? "1" : "0",
                    terms, filters));
            }
            Services.ThemeManager.SetSetting("Tabs", string.Join("\n", lines));
        }

        private bool TryRestoreTabs()
        {
            if (Services.ThemeManager.GetSetting("RememberTabs") == "0") return false;
            var raw = Services.ThemeManager.GetSetting("Tabs");
            if (string.IsNullOrEmpty(raw)) return false;

            try
            {
                foreach (var line in raw!.Split('\n'))
                {
                    var p = line.Split('|');
                    if (p.Length < 9) continue;
                    var t = CreateTab();
                    t.Title           = Uri.UnescapeDataString(p[0]);
                    t.RootPath        = Uri.UnescapeDataString(p[1]);
                    t.IncludePatterns = Uri.UnescapeDataString(p[2]);
                    t.ExcludePatterns = Uri.UnescapeDataString(p[3]);
                    t.CaseSensitive   = p[4] == "1";
                    t.SortIndex       = int.TryParse(p[5], out int si) ? si : 0;
                    t.SortAsc         = p[6] == "1";

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
            }
            catch { /* corrupt blob - fall back to a fresh tab */ }

            if (_tabs.Count == 0) return false;
            ActivateTab(_tabs[0]);
            return true;
        }
    }
}
