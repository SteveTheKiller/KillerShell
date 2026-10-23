using System;
using Microsoft.Win32;

// "Open folders in KillerShell", the About card checkbox.
//
// What a double-click on a file-system folder or a drive runs is the default verb of
// Directory\shell or Drive\shell, which Windows checks before the Folder class that every
// special folder (This PC, Recycle Bin, Control Panel) shares. Making KillerShell's own "Open in
// KillerShell" verb the default of those two, for this user only, opens folders and drives in
// KillerShell and leaves everything else, the desktop and the open/save dialogs with Explorer.
// Programs that run explorer.exe themselves still get Explorer.
//
// As with the default terminal (DefaultTerminalSetting.cs), it happens only when the user turns
// it on, and what it replaced is saved first and put back on the way out, but only where the
// value is still KillerShell's. HKCU values sit over HKLM's one by one, so deleting ours brings
// Windows' own default straight back.
namespace KillerShell.Services
{
    internal enum FolderOpenerState
    {
        /// <summary>Folders open in Explorer or whatever was chosen before.</summary>
        Off,

        /// <summary>Folders and drives open in KillerShell.</summary>
        On,

        /// <summary>KillerShell turned it on, then something else took folders over.</summary>
        ChangedElsewhere,
    }

    internal sealed class FolderOpenerSetting
    {
        /// <summary>The verb Associations.cs registers on folders and drives, "Open in KillerShell".</summary>
        internal const string Verb = "KillerShell";

        /// <summary>Where the replaced defaults wait to be put back: "directory|drive", empty for missing.</summary>
        internal const string PreviousSetting = "FolderOpenerPrevious";
        private const string SettingsKey = @"Software\KillerShell";

        private static readonly string[] Owners = [@"Software\Classes\Directory\shell", @"Software\Classes\Drive\shell"];

        private readonly IRegistryStore _registry;
        private readonly bool _installed;
        private readonly string _exePath;

        internal FolderOpenerSetting(IRegistryStore registry, bool installed, string exePath)
        {
            _registry = registry;
            _installed = installed;
            _exePath = exePath;
        }

        /// <summary>
        /// The verb's command, shared with Associations.cs. --new-window: a folder opened from
        /// Explorer gets a window of its own, not the saved session restored around it. The
        /// trailing \. keeps a drive root's quote from being escaped.
        /// </summary>
        internal static string VerbCommand(string exePath) => "\"" + exePath + "\" --new-window \"%1\\.\"";

        internal FolderOpenerState State
        {
            get
            {
                if (Array.TrueForAll(Owners, IsOurs)) return FolderOpenerState.On;
                return Previous() != null ? FolderOpenerState.ChangedElsewhere : FolderOpenerState.Off;
            }
        }

        /// <summary>Whether the About checkbox takes a click: only installed copies can turn it on.</summary>
        internal bool CanChange => _installed || State == FolderOpenerState.On;

        /// <summary>The About checkbox's tooltip.</summary>
        internal string NoteKey =>
            !_installed ? "Str_About_OpenFoldersPortable"
          : State == FolderOpenerState.ChangedElsewhere ? "Str_About_OpenFoldersChanged"
          : "Str_About_OpenFoldersHint";

        /// <summary>Whether the note also shows as text: a disabled checkbox is out of keyboard reach.</summary>
        internal bool ShowsNote => NoteKey != "Str_About_OpenFoldersHint";

        /// <summary>Opens folders and drives in KillerShell. False for a portable copy, with nothing changed.</summary>
        internal bool TurnOn()
        {
            if (State == FolderOpenerState.On)
            {
                Repair();
                return true;
            }
            if (!_installed) return false;

            EnsureVerbs();
            // Per owner: one still set to KillerShell (another program took only the other one)
            // keeps the value saved for it, so "KillerShell" is never what gets put back.
            string[] saved = (Previous() ?? "|").Split('|');
            string[] previous = new string[Owners.Length];
            for (int i = 0; i < Owners.Length; i++)
                previous[i] = IsOurs(Owners[i]) ? (i < saved.Length ? saved[i] : string.Empty) : Read(Owners[i]);
            _registry.SetString(RegistryHive.CurrentUser, SettingsKey, PreviousSetting, string.Join("|", previous));
            foreach (string owner in Owners)
                _registry.SetString(RegistryHive.CurrentUser, owner, null, Verb);
            return true;
        }

        /// <summary>
        /// Puts back what opened folders before, wherever the default is still KillerShell's.
        /// Uninstall and the Associations card's Remove run it too, before the verb goes.
        /// </summary>
        internal void TurnOff()
        {
            string[] previous = (Previous() ?? "|").Split('|');
            for (int i = 0; i < Owners.Length; i++)
            {
                if (!IsOurs(Owners[i])) continue;
                string value = i < previous.Length ? previous[i] : string.Empty;
                if (string.Equals(value, Verb, StringComparison.OrdinalIgnoreCase)) value = string.Empty;
                if (value.Length == 0) _registry.DeleteValue(RegistryHive.CurrentUser, Owners[i], string.Empty);
                else _registry.SetString(RegistryHive.CurrentUser, Owners[i], null, value);
            }
            _registry.DeleteValue(RegistryHive.CurrentUser, SettingsKey, PreviousSetting);
        }

        /// <summary>While it is on, makes sure the verb the defaults name still exists.</summary>
        internal void Repair()
        {
            if (State == FolderOpenerState.On && _installed) EnsureVerbs();
        }

        private bool IsOurs(string owner) => string.Equals(Read(owner), Verb, StringComparison.OrdinalIgnoreCase);

        private string Read(string owner) => _registry.GetString(RegistryHive.CurrentUser, owner, null) ?? string.Empty;

        private string? Previous() => _registry.GetString(RegistryHive.CurrentUser, SettingsKey, PreviousSetting);

        /// <summary>
        /// The verb the defaults name, as this copy runs it. A per-user verb wins over the
        /// all-users one, so one left by another copy (a portable copy registered from the
        /// Associations card and deleted since) is rewritten rather than made the double-click
        /// default. An all-users verb is kept: only an all-users install writes or removes it.
        /// </summary>
        private void EnsureVerbs()
        {
            string command = VerbCommand(_exePath);
            foreach (string owner in Owners)
            {
                string key = owner + @"\" + Verb;
                if (_registry.KeyExists(RegistryHive.CurrentUser, key))
                {
                    if (string.Equals(_registry.GetString(RegistryHive.CurrentUser, key + @"\command", null), command,
                                      StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                else if (_registry.KeyExists(RegistryHive.LocalMachine, key))
                    continue;
                _registry.SetString(RegistryHive.CurrentUser, key, null, "Open in KillerShell");
                _registry.SetString(RegistryHive.CurrentUser, key + @"\command", null, VerbCommand(_exePath));
            }
        }
    }
}
