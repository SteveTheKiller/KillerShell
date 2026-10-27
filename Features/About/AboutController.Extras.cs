using KillerShell.Services;

namespace KillerShell.Features.About
{
    /// <summary>KillerShell's own About rows: the default terminal and open folders checkboxes,
    /// each with whether it takes a click and its note, plus the --demo preview and the unsaved
    /// documents check before an update closes the app.</summary>
    internal interface IAboutShell
    {
        /// <summary>The "Use KillerShell as the default terminal" checkbox, under Close to system
        /// tray: whether it is on, whether it takes a click, and its tooltip.</summary>
        bool DefaultTerminal { set; }
        bool DefaultTerminalEnabled { set; }
        string DefaultTerminalNote { set; }

        /// <summary>Whether the note also shows as text under the checkbox.</summary>
        bool DefaultTerminalNoteVisible { set; }

        /// <summary>The "Open folders in KillerShell" checkbox, under the default terminal one,
        /// with the same four parts.</summary>
        bool OpenFolders { set; }
        bool OpenFoldersEnabled { set; }
        string OpenFoldersNote { set; }
        bool OpenFoldersNoteVisible { set; }

        /// <summary>True under --demo, where the card is rendered in its signed state so marketing
        /// captures from an unsigned local build match the release (DemoMode.cs).</summary>
        bool DemoPreview { get; }

        /// <summary>Checks unsaved documents before starting a relaunch that cannot be canceled.</summary>
        bool ConfirmShutdown();

        /// <summary>Restores close prompts when a relaunch fails.</summary>
        void CancelShutdown();
    }

    /// <summary>
    /// APP-OWNED: KillerShell's additions to the shared AboutController (kit file
    /// Features/About/AboutController.cs). Kept out of the kit drift check.
    /// </summary>
    internal sealed partial class AboutController
    {
        private IAboutShell? Shell => _host as IAboutShell;

        partial void OnShow()
        {
            ShowDefaultTerminal(App.DefaultTerminalSetting());   // DefaultTerminal.cs
            ShowOpenFolders(App.FolderOpenerSetting());
        }

        // --demo previews the signed card (DemoMode.cs), using the real cert values.
        partial void AdjustSignature(ref bool valid, ref string subject, ref string thumbprint)
        {
            if (Shell?.DemoPreview != true) return;
            valid = true;
            subject = AppInfo.DemoSubject;
            thumbprint = AppInfo.DemoThumbprint;
        }

        // CodeSignature reports its placeholders in English; the card shows them translated.
        partial void ShownValue(ref string value)
        {
            if (value == "(none)") value = _host.Loc("Str_About_None");
            else if (value == "(unavailable)") value = _host.Loc("Str_About_Unavailable");
        }

        // Captures stay clean under --demo.
        partial void SkipStartupCheck(ref bool skip) => skip |= Shell?.DemoPreview == true;

        // Checked after the download: documents can change while it is in progress.
        partial void ConfirmUpdateShutdown(ref bool proceed)
        {
            if (Shell is { } shell) proceed = shell.ConfirmShutdown();
        }

        partial void UpdateShutdownCanceled() => Shell?.CancelShutdown();

        /// <summary>Turns KillerShell on or off as the default terminal from the About checkbox,
        /// then shows what the registry actually holds, so a refused write unchecks the box again.</summary>
        internal void SetDefaultTerminal(bool enabled)
        {
            var setting = App.DefaultTerminalSetting();   // DefaultTerminal.cs
            try
            {
                if (enabled) setting.TurnOn();
                else setting.TurnOff();
            }
            catch { /* shown below as whatever the registry kept */ }
            ShowDefaultTerminal(setting);
        }

        private void ShowDefaultTerminal(DefaultTerminalSetting setting)
        {
            if (Shell is not { } shell) return;
            shell.DefaultTerminal        = setting.State == DefaultTerminalState.On;
            shell.DefaultTerminalEnabled = setting.CanChange;
            shell.DefaultTerminalNote    = _host.Loc(setting.NoteKey);
            shell.DefaultTerminalNoteVisible = setting.ShowsNote;
        }

        /// <summary>Turns "Open folders in KillerShell" on or off from the About checkbox, then
        /// shows what the registry actually holds.</summary>
        internal void SetOpenFolders(bool enabled)
        {
            var setting = App.FolderOpenerSetting();   // DefaultTerminal.cs
            try
            {
                if (enabled) setting.TurnOn();
                else setting.TurnOff();
            }
            catch { /* shown below as whatever the registry kept */ }
            App.NotifyAssociationsChanged();
            ShowOpenFolders(setting);
        }

        private void ShowOpenFolders(FolderOpenerSetting setting)
        {
            if (Shell is not { } shell) return;
            shell.OpenFolders            = setting.State == FolderOpenerState.On;
            shell.OpenFoldersEnabled     = setting.CanChange;
            shell.OpenFoldersNote        = _host.Loc(setting.NoteKey);
            shell.OpenFoldersNoteVisible = setting.ShowsNote;
        }
    }
}
