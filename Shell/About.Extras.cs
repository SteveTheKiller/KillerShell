using System.Windows;
using KillerShell.Controls;
using KillerShell.Features;
using KillerShell.Features.About;

// APP-OWNED: KillerShell's half of the About overlay that the kit's Shell/About.cs does not
// cover. The card's own settings (Start with Windows, Close to tray, default terminal, open
// folders, ask before recycling a folder, Clear all Data), the --demo preview and the unsaved
// documents check before an update closes the app.
namespace KillerShell.Shell
{
    public partial class MainWindow : IAboutStartup, IAboutTray, IAboutShell
    {
        /// <summary>After the first paint and any startup dialog (MainWindow.xaml.cs). An elevated
        /// window never offers an update: the swap would run, and relaunch, as administrator.</summary>
        private void CheckForUpdatesOnStartup()
        {
            if (!IsElevated) _about.CheckOnStartup();   // Elevation.cs
        }

        // Read every time the card opens: the delete prompt's don't-ask-again box can turn this
        // off while the window is up. Assigned before the card shows, so the Changed handler
        // writing the same value back is harmless.
        partial void OnOverlayShowing(UIElement overlay)
        {
            if (overlay == AboutOverlay) AboutConfirmFolderDelete.IsChecked = ConfirmFolderDelete;   // FileCommands.cs
        }

        // ---- IShellServices ----

        Window IShellServices.Window => this;
        string IShellServices.Loc(string key) => Loc(key);
        void IShellServices.SetStatus(string text) => SetTabStatus(_active, text);

        // ---- IAboutStartup, IAboutTray, IAboutShell ----

        bool IAboutStartup.StartWithWindows { set => AboutStartWithWindows.IsChecked = value; }
        bool IAboutTray.CloseToTray         { set => AboutCloseToTray.IsChecked = value; }

        bool IAboutShell.DefaultTerminal        { set => AboutDefaultTerminal.IsChecked = value; }
        bool IAboutShell.DefaultTerminalEnabled { set => AboutDefaultTerminal.IsEnabled = value; }
        string IAboutShell.DefaultTerminalNote
        {
            set
            {
                AboutDefaultTerminal.ToolTip = value;
                AboutDefaultTerminalNote.Text = value;
                System.Windows.Automation.AutomationProperties.SetHelpText(AboutDefaultTerminal, value);
            }
        }
        bool IAboutShell.DefaultTerminalNoteVisible
        {
            set => AboutDefaultTerminalNote.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
        bool IAboutShell.OpenFolders        { set => AboutOpenFolders.IsChecked = value; }
        bool IAboutShell.OpenFoldersEnabled { set => AboutOpenFolders.IsEnabled = value; }
        string IAboutShell.OpenFoldersNote
        {
            set
            {
                AboutOpenFolders.ToolTip = value;
                AboutOpenFoldersNote.Text = value;
                System.Windows.Automation.AutomationProperties.SetHelpText(AboutOpenFolders, value);
            }
        }
        bool IAboutShell.OpenFoldersNoteVisible
        {
            set => AboutOpenFoldersNote.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        bool IAboutShell.DemoPreview => DemoMode;
        bool IAboutShell.ConfirmShutdown() => ConfirmShutdown();
        void IAboutShell.CancelShutdown() => CancelShutdown();

        // ---- The card's settings ----

        /// <summary>Ask before recycling a folder. This is the only way back after the delete
        /// prompt's don't-ask-again box has been ticked.</summary>
        private void AboutConfirmFolderDelete_Changed(object sender, RoutedEventArgs e)
            => ConfirmFolderDelete = AboutConfirmFolderDelete.IsChecked == true;   // FileCommands.cs

        private void AboutStartWithWindows_Click(object sender, RoutedEventArgs e)
            => _about.SetStartWithWindows(AboutStartWithWindows.IsChecked == true);

        private void AboutCloseToTray_Click(object sender, RoutedEventArgs e)
            => _about.SetCloseToTray(AboutCloseToTray.IsChecked == true);

        private void AboutDefaultTerminal_Click(object sender, RoutedEventArgs e)
            => _about.SetDefaultTerminal(AboutDefaultTerminal.IsChecked == true);   // DefaultTerminal.cs

        private void AboutOpenFolders_Click(object sender, RoutedEventArgs e)
            => _about.SetOpenFolders(AboutOpenFolders.IsChecked == true);   // Services/FolderOpenerSetting.cs

        /// <summary>Clear all Data, the footer's destructive link. Wipes every saved preference
        /// and the temp files extracted from archives; the user's own files are not touched.
        /// Always asks.</summary>
        private void AboutClearData_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ConfirmDialog(Loc("Str_Dlg_ClearDataMsg"), null,
                                        Loc("Str_About_ClearData")) { Owner = this };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            App.ClearAllData();

            // The card shows the old value of a setting that was just deleted; put it back in step.
            AboutConfirmFolderDelete.IsChecked = ConfirmFolderDelete;
            SetTabStatusKey(_active, "Str_Status_DataCleared");
        }
    }
}
