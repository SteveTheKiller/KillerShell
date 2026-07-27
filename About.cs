using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Navigation;

// KillerUI / Grunge - About overlay. Partial of MainWindow.
// MainWindow.xaml must provide an "AboutOverlay" Grid (dim bg, Collapsed, MouseLeftButtonDown=
// AboutOverlay_Click) containing a card (MouseLeftButtonDown=AboutCard_Click) with named blocks:
// AboutVersionBlock and AboutReleaseDateBlock (one row, version left / date right),
// AboutPublisherBlock, AboutAkaBlock (Collapsed by default) wrapping AboutAkaRun inside a
// thekiller.net Hyperlink, AboutThumbprintBlock, AboutSha256Block, AboutUpdateButton,
// AboutUpdateText, and a close button Click=AboutClose_Click. The info panel Grid must be
// named AboutInfoGrid - the header binds its width to it so the SHA-256 line stays the only
// thing setting the card width.
namespace KillerShell
{
    public partial class MainWindow
    {
        private const string GitHubRepo     = "SteveTheKiller/KillerShell";
        private const string ExeName        = "KillerShell.exe";
        private const string AppDisplayName = "KillerShell";

        private string? _updateTag;

        // The certificate subject is the legal name ("Open Source Developer Stephen Riley"),
        // so the About card ties it back to the name people know. Gated on the subject actually
        // being Steve's: a fork signed by somebody else must not claim the alias, and an
        // unsigned build has no subject at all.
        private const string SignerName = "Stephen Riley";
        private const string AkaName    = "Steve the Killer";

        // --demo shows the About card as it looks on a signed release, so marketing captures
        // taken from a local build match the real thing. These are the REAL certificate values
        // (read off an already-signed sibling app), not invented ones - a published screenshot
        // must never show a fingerprint that does not exist. Refresh both if the cert is
        // replaced; the current one expires 2027-05-04.
        private const string DemoSubject    = "Open Source Developer Stephen Riley";
        private const string DemoThumbprint = "E478E4940DFD3547DAF2199494B399214FB3E0FD";

        private static string CurrentVersion =>
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(2) ?? "0.99";

        /// <summary>Release date baked in from the csproj's ReleaseDate property, so a user can
        /// see how old their build is. Empty when the attribute is missing (an old build), in
        /// which case the version line shows the version alone.</summary>
        private static string ReleaseDate
        {
            get
            {
                foreach (var a in Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>())
                    if (a.Key == "ReleaseDate") return a.Value ?? string.Empty;
                return string.Empty;
            }
        }

        private void ShowAboutOverlay()
        {
            AboutVersionBlock.Text = $"v{CurrentVersion}";
            // Its own block so it can sit muted, italic and right-aligned opposite the version.
            AboutReleaseDateBlock.Text = ReleaseDate;

            var (subject, thumb) = GetSignerInfo();
            // --demo previews the signed card (DemoMode.cs), using the real cert values.
            if (DemoMode) { subject = DemoSubject; thumb = DemoThumbprint; }
            AboutPublisherBlock.Text  = subject;
            // Shown only when the exe is signed by Steve - not merely signed by somebody.
            bool signedByMe = subject.IndexOf(SignerName, StringComparison.OrdinalIgnoreCase) >= 0;
            // Only the quoted alias goes in the run - the "AKA " prefix and the thekiller.net
            // hyperlink around this run both live in the XAML. 0x201C / 0x201D are the curly
            // quotes, built from codepoints (same trick as the glyph swap in Results.cs) so
            // this file stays pure ASCII on disk - it is BOM-less UTF-8, the encoding trap
            // that made release.ps1 PS7-only.
            AboutAkaRun.Text         = (char)0x201C + AkaName + (char)0x201D;
            AboutAkaBlock.Visibility = signedByMe ? Visibility.Visible : Visibility.Collapsed;
            AboutThumbprintBlock.Text = thumb;
            AboutSha256Block.Text     = "computing...";
            AboutUpdateButton.Visibility = Visibility.Collapsed;

            FadeOverlayIn(AboutOverlay);

            Task.Run(() =>
            {
                var h = GetExeSha256();
                Dispatcher.BeginInvoke((Action)(() => AboutSha256Block.Text = h));
            });
            CheckForUpdateAsync(Assembly.GetExecutingAssembly().GetName().Version);
        }

        private static void FadeOverlayIn(UIElement o)
        {
            o.Visibility = Visibility.Visible;
            Anim.FadeIn(o);
        }

        private static void FadeOverlayOut(UIElement o)
        {
            var a = new DoubleAnimation(o.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(Anim.FadeMs)))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            a.Completed += (_, _) => o.Visibility = Visibility.Collapsed;
            o.BeginAnimation(UIElement.OpacityProperty, a);
        }

        private void AboutOverlay_Click(object sender, MouseButtonEventArgs e) => FadeOverlayOut(AboutOverlay);
        private void AboutCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
        private void AboutClose_Click(object sender, RoutedEventArgs e) => FadeOverlayOut(AboutOverlay);

        private void AboutVersion_Click(object sender, MouseButtonEventArgs e) =>
            OpenUrl($"https://github.com/{GitHubRepo}/releases/tag/v{CurrentVersion}");

        private void AboutLink_Navigate(object sender, RequestNavigateEventArgs e)
        {
            OpenUrl(e.Uri.AbsoluteUri);
            e.Handled = true;
        }

        private void AboutUpdateButton_Click(object sender, RoutedEventArgs e) => DoSelfUpdateAsync();

        private async void DoSelfUpdateAsync()
        {
            var tag = _updateTag;
            if (string.IsNullOrEmpty(tag)) return;

            var confirm = MessageBox.Show(this,
                $"Download and install {AppDisplayName} {tag}?\n\nThe app will close and reopen automatically.",
                AppDisplayName, MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            AboutUpdateButton.IsEnabled = false;
            AboutUpdateText.Text = "Downloading...";

            string? newExe = null;
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(90) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppDisplayName}-UpdateCheck");

                var exeUrl  = $"https://github.com/{GitHubRepo}/releases/download/{tag}/{ExeName}";
                var sumsUrl = $"https://github.com/{GitHubRepo}/releases/download/{tag}/SHA256SUMS.txt";

                var exeBytes = await http.GetByteArrayAsync(exeUrl);
                var sumsTxt  = await http.GetStringAsync(sumsUrl);

                string? expected = null;
                foreach (var line in sumsTxt.Replace("\r", "").Split('\n'))
                {
                    if (line.TrimStart().StartsWith(ExeName, StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2) expected = parts[^1];
                        break;
                    }
                }
                if (string.IsNullOrEmpty(expected)) throw new Exception("checksum entry not found");

                string actual;
                using (var sha = SHA256.Create())
                    actual = BitConverter.ToString(sha.ComputeHash(exeBytes)).Replace("-", "");
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("checksum mismatch");

                newExe = Path.Combine(Path.GetTempPath(), $"{AppDisplayName}_update_{Guid.NewGuid():N}.exe");
                File.WriteAllBytes(newExe, exeBytes);
            }
            catch
            {
                AboutUpdateButton.IsEnabled = true;
                AboutUpdateText.Text = $"Update available: {tag}";
                OpenUrl($"https://github.com/{GitHubRepo}/releases/latest");
                return;
            }

            // A machine-wide install (Program Files, from the /silent path, winget, choco or an
            // RMM) is not writable by a normal user, so the swap has to run elevated. This
            // previously ran the batch unelevated and sent the copy to >nul with no errorlevel
            // check, so on those installs it silently failed and then relaunched the OLD exe -
            // the app appeared to "update" to the same version, with no error. The copy result is
            // now checked, and the batch is elevated when the target needs it.
            try
            {
                var curExe = Process.GetCurrentProcess().MainModule!.FileName;
                var pid    = Process.GetCurrentProcess().Id;
                var bat    = Path.Combine(Path.GetTempPath(), $"{AppDisplayName}_update_{Guid.NewGuid():N}.bat");

                bool needsElevation = !CanWriteTo(Path.GetDirectoryName(curExe)!);

                // When elevated, relaunch through explorer.exe so the app comes back at the
                // user's normal integrity level instead of inheriting the elevated token.
                string relaunch = needsElevation
                    ? $"start \"\" explorer.exe \"{curExe}\""
                    : $"start \"\" \"{curExe}\"";

                File.WriteAllText(bat,
                    "@echo off\r\n" +
                    ":wait\r\n" +
                    $"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul\r\n" +
                    "if not errorlevel 1 ( ping -n 2 127.0.0.1 >nul & goto wait )\r\n" +
                    $"copy /y \"{newExe}\" \"{curExe}\" >nul 2>&1\r\n" +
                    "if errorlevel 1 goto failed\r\n" +
                    relaunch + "\r\n" +
                    "goto cleanup\r\n" +
                    ":failed\r\n" +
                    // Do not relaunch a stale exe and call it an update: send the user to the
                    // releases page so the failure is visible and fixable by hand.
                    $"start \"\" \"https://github.com/{GitHubRepo}/releases/latest\"\r\n" +
                    ":cleanup\r\n" +
                    $"del \"{newExe}\" >nul 2>&1\r\n" +
                    "del \"%~f0\" >nul 2>&1\r\n");

                var psi = new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                };
                if (needsElevation) psi.Verb = "runas";   // triggers the UAC prompt

                // Declining UAC throws Win32Exception 1223, so only shut down once the helper
                // is actually running - otherwise the app would close without updating.
                Process.Start(psi);
                Application.Current.Shutdown();
            }
            catch
            {
                try { if (newExe is not null && File.Exists(newExe)) File.Delete(newExe); } catch { }
                AboutUpdateButton.IsEnabled = true;
                AboutUpdateText.Text = $"Update available: {tag}";
            }
        }

        /// <summary>True if this process can create a file in <paramref name="dir"/>. Used to decide
        /// whether the self-update swap needs elevating: Program Files installs are not writable by a
        /// normal user, per-user installs under LOCALAPPDATA always are.</summary>
        private static bool CanWriteTo(string dir)
        {
            try
            {
                var probe = Path.Combine(dir, $".kf_write_{Guid.NewGuid():N}.tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                      1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
        }

        private async void CheckForUpdateAsync(Version? current)
        {
            if (current is null) return;
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppDisplayName}-UpdateCheck");
                var json = await http.GetStringAsync(
                    $"https://api.github.com/repos/{GitHubRepo}/releases/latest").ConfigureAwait(false);

                var m = System.Text.RegularExpressions.Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                if (!m.Success) return;
                if (!Version.TryParse(m.Groups[1].Value.TrimStart('v', 'V').Trim(), out var latest)) return;

                var cur = new Version(current.Major, current.Minor, current.Build < 0 ? 0 : current.Build);
                var lat = new Version(latest.Major, latest.Minor, latest.Build < 0 ? 0 : latest.Build);
                if (lat <= cur) return;

                await Dispatcher.BeginInvoke((Action)(() =>
                {
                    _updateTag = $"v{lat.ToString(3)}";
                    AboutUpdateText.Text = $"Update available: {_updateTag}";
                    AboutUpdateButton.Visibility = Visibility.Visible;
                }));
            }
            catch { /* offline or API error - silently ignore */ }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no browser available - ignore */ }
        }

        private static (string subject, string thumb) GetSignerInfo()
        {
            try
            {
                var path = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(path)) return ("(unavailable)", "(none)");
                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                var subj = cert.GetNameInfo(X509NameType.SimpleName, false);
                return (string.IsNullOrEmpty(subj) ? cert.Subject : subj, cert.Thumbprint ?? "(none)");
            }
            catch { return ("(not signed)", "(none)"); }
        }

        private static string GetExeSha256()
        {
            try
            {
                var path = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "(unavailable)";
                using var sha = SHA256.Create();
                using var fs  = File.OpenRead(path);
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
            catch { return "(unavailable)"; }
        }
    }
}
