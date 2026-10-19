using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using KillerShell.Features;
using KillerShell.Features.About;

// KillerUI / Grunge - About overlay. Partial of MainWindow.
//
// The About overlay's window half: the fade, the click handling, the card's own settings, and the
// IAboutHost implementation that maps the controller's values onto the named XAML elements. All the
// About behavior lives in Features/About/AboutController.cs, and the values it reports come from
// Services/AppInfo.cs, Services/CodeSignature.cs and Services/UpdateService.cs.
//
// MainWindow.xaml must provide an "AboutOverlay" Grid (dim bg, Collapsed, MouseLeftButtonDown=
// AboutOverlay_Click) containing a card (MouseLeftButtonDown=AboutCard_Click) with named blocks:
// AboutVersionBlock and AboutReleaseDateBlock (one row, version left / date right),
// AboutPublisherBlock, AboutAkaBlock (Collapsed by default) wrapping AboutAkaRun inside a
// thekiller.net Hyperlink, AboutThumbprintBlock, AboutSha256Block, AboutUpdateButton,
// AboutUpdateText, and a close button Click=AboutClose_Click. The info panel Grid must be
// named AboutInfoGrid - the header binds its width to it so the SHA-256 line stays the only
// thing setting the card width.
namespace KillerShell.Shell
{
    public partial class MainWindow : IAboutHost
    {
        private readonly AboutController _about = null!;

        private void ShowAboutOverlay()
        {
            // Read every time the card opens, not once at startup: the delete prompt's
            // don't-ask-again box can turn this off while the window is up, and a stale tick here
            // would tell the user the prompt is still on when it is not.
            //
            // Assigned before the card is shown so the Checked/Unchecked handler firing on this
            // assignment writes back the value it just read. Harmless, and cheaper than a guard
            // flag that would have to be threaded through both handlers.
            AboutConfirmFolderDelete.IsChecked = ConfirmFolderDelete;   // FileCommands.cs
            _about.Show();
        }

        /// <summary>After the first paint and any startup dialog (MainWindow.xaml.cs). An elevated
        /// window never offers an update: the swap would run, and relaunch, as administrator.</summary>
        private void CheckForUpdatesOnStartup()
        {
            if (!IsElevated) _about.CheckOnStartup();   // Elevation.cs
        }

        // ---- IShellServices / IAboutHost ----

        Window IShellServices.Window => this;
        string IShellServices.Loc(string key) => Loc(key);
        void IShellServices.SetStatus(string text) => SetTabStatus(_active, text);

        string IAboutHost.Version     { set => AboutVersionBlock.Text = value; }
        string IAboutHost.ReleaseDate { set => AboutReleaseDateBlock.Text = value; }
        string IAboutHost.Publisher   { set => AboutPublisherBlock.Text = value; }
        string IAboutHost.Alias       { set => AboutAkaRun.Text = value; }
        string IAboutHost.Thumbprint  { set => AboutThumbprintBlock.Text = value; }
        string IAboutHost.Sha256      { set => AboutSha256Block.Text = value; }
        string IAboutHost.UpdateText  { set => AboutUpdateText.Text = value; }

        bool IAboutHost.AliasVisible
        {
            set => AboutAkaBlock.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        bool IAboutHost.UpdateVisible
        {
            set => AboutUpdateButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        bool IAboutHost.UpdateEnabled { set => AboutUpdateButton.IsEnabled = value; }

        bool IAboutHost.StartupUpdateCheck { set => AboutStartupUpdateCheck.IsChecked = value; }
        bool IAboutHost.StartWithWindows   { set => AboutStartWithWindows.IsChecked = value; }
        bool IAboutHost.CloseToTray        { set => AboutCloseToTray.IsChecked = value; }

        bool IAboutHost.DemoPreview => DemoMode;

        void IAboutHost.ShowCard() => FadeOverlayIn(AboutOverlay);
        bool IAboutHost.ConfirmShutdown() => ConfirmShutdown();
        void IAboutHost.CancelShutdown() => CancelShutdown();

        // ---- The card's settings ----

        /// <summary>
        /// The About card's one setting: ask before recycling a folder. It lives here rather than
        /// in a preferences screen because this is the only way back after the delete prompt's
        /// don't-ask-again box has been ticked.
        /// </summary>
        private void AboutConfirmFolderDelete_Changed(object sender, RoutedEventArgs e)
            => ConfirmFolderDelete = AboutConfirmFolderDelete.IsChecked == true;   // FileCommands.cs

        /// <summary>The way back after the update prompt's "Always check on startup" box was
        /// cleared, and the first of the card's checkboxes.</summary>
        private void AboutStartupUpdateCheck_Click(object sender, RoutedEventArgs e)
            => _about.SetStartupCheck(AboutStartupUpdateCheck.IsChecked == true);

        private void AboutStartWithWindows_Click(object sender, RoutedEventArgs e)
            => _about.SetStartWithWindows(AboutStartWithWindows.IsChecked == true);

        private void AboutCloseToTray_Click(object sender, RoutedEventArgs e)
            => _about.SetCloseToTray(AboutCloseToTray.IsChecked == true);

        /// <summary>
        /// Clear all Data, the footer's destructive link. Wipes every saved preference and the
        /// temp files extracted from archives; the user's own files are not touched. Always asks.
        /// </summary>
        private void AboutClearData_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ConfirmDialog(Loc("Str_Dlg_ClearDataMsg"), null,
                                        Loc("Str_About_ClearData")) { Owner = this };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            App.ClearAllData();

            // The card is showing the old value of a setting that has just been deleted, so put it
            // back in step rather than leaving a tick over a preference that no longer exists.
            AboutConfirmFolderDelete.IsChecked = ConfirmFolderDelete;

            // A status line rather than a second dialog. KillerPDF puts up an OK box here, but it
            // has no status bar to say it in; this app does, and the restart note belongs with the
            // rest of what the window reports rather than in a box to dismiss.
            SetTabStatusKey(_active, "Str_Status_DataCleared");
        }

        // ---- Overlay fade ----

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

        // ---- Handlers ----

        private void AboutOverlay_Click(object sender, MouseButtonEventArgs e) => FadeOverlayOut(AboutOverlay);
        private void AboutCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
        private void AboutClose_Click(object sender, RoutedEventArgs e) => FadeOverlayOut(AboutOverlay);

        private void AboutVersion_Click(object sender, MouseButtonEventArgs e) => _about.OpenReleaseNotes();

        private void AboutUpdateButton_Click(object sender, RoutedEventArgs e) => _about.Update();

        private void AboutLink_Navigate(object sender, RequestNavigateEventArgs e)
        {
            Services.WebLink.Open(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
    }
}
