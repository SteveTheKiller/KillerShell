using KillerShell.Shell;

namespace KillerShell.Controls
{
    /// <summary>
    /// APP-OWNED: KillerShell's addition to the shared ConfirmDialog (kit files
    /// Controls/ConfirmDialog.xaml and .xaml.cs). Kept out of the kit drift check.
    /// </summary>
    public partial class ConfirmDialog
    {
        // The dialog takes the same theme border as every other KillerShell popup window.
        partial void OnDialogSourceInitialized() => MainWindow.ApplyThemeBorder(this);
    }
}
