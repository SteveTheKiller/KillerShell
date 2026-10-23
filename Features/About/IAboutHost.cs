namespace KillerShell.Features.About
{
    /// <summary>
    /// What AboutController needs from the window hosting it, beyond the shared shell services.
    /// Every member is a value, not a control, so the controller holds no reference to a TextBlock
    /// or a Button and can be driven by a stub in a test.
    /// </summary>
    internal interface IAboutHost : IShellServices
    {
        /// <summary>Version line, clickable through to that release.</summary>
        string Version { set; }

        /// <summary>Release date, shown muted opposite the version.</summary>
        string ReleaseDate { set; }

        /// <summary>Code-signing subject, or the unsigned message.</summary>
        string Publisher { set; }

        /// <summary>The quoted alias line, and whether the signature earned the right to show it.</summary>
        string Alias { set; }
        bool AliasVisible { set; }

        /// <summary>Certificate thumbprint.</summary>
        string Thumbprint { set; }

        /// <summary>SHA-256 of the running exe, filled in once it has been computed.</summary>
        string Sha256 { set; }

        /// <summary>The line above the update button.</summary>
        string UpdateText { set; }

        /// <summary>The update button appears only when a newer release exists, and goes insensitive
        /// while a download is running.</summary>
        bool UpdateVisible { set; }
        bool UpdateEnabled { set; }

        /// <summary>The "Check for updates on startup" checkbox, first of the card's checkboxes.</summary>
        bool StartupUpdateCheck { set; }

        /// <summary>The "Start with Windows" checkbox, under the update checkbox. Off until the user
        /// turns it on.</summary>
        bool StartWithWindows { set; }

        /// <summary>The "Close to system tray" checkbox, under Start with Windows. Off until the user
        /// turns it on.</summary>
        bool CloseToTray { set; }

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

        /// <summary>Fades the About card in.</summary>
        void ShowCard();

        /// <summary>Checks unsaved documents before starting a relaunch that cannot be canceled.</summary>
        bool ConfirmShutdown();

        /// <summary>Restores close prompts when a relaunch fails or is canceled.</summary>
        void CancelShutdown();
    }
}
