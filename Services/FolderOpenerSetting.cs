using System;
using Microsoft.Win32;

// "Open folders in KillerShell", the About card checkbox.
//
// What a double-click on a file-system folder or a drive runs is the default verb of
// Directory\shell or Drive\shell, which Windows checks before the Folder class that every
// special folder (This PC, Recycle Bin, Control Panel) shares. Making KillerShell's own "Open in
// KillerShell" verb the default of those two, for this user only, opens folders and drives in
// KillerShell and leaves everything else, the desktop and the open/save dialogs with Explorer.
//
// A program that asks Windows to open a folder (Start-Process, ShellExecute) asks for the "open"
// verb instead, and the default never reaches it (KillerLab-W11, the first KS-C run). Directory and
// Drive have no "open" of their own, so Explorer's on Folder answers; one of ours in HKCU sits in
// front of it. Programs that run explorer.exe themselves still get Explorer.
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

        /// <summary>
        /// Where the replaced defaults wait to be put back: "directory|drive", empty for missing.
        /// The replaced open verbs wait whole under the subkey of the same name plus "Open"
        /// (SavedOpen). Clear all Data keeps every value and subkey whose name starts with this.
        /// </summary>
        internal const string PreviousSetting = "FolderOpenerPrevious";
        private const string SettingsKey = @"Software\KillerShell";
        private const string SavedOpenKey = SettingsKey + @"\" + PreviousSetting + "Open";

        private const string Arguments = " --new-window \"%1\\.\"";

        private static readonly string[] Owners = [@"Software\Classes\Directory\shell", @"Software\Classes\Drive\shell"];
        private static readonly string[] OwnerNames = ["Directory", "Drive"];

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
        internal static string VerbCommand(string exePath) => "\"" + exePath + "\"" + Arguments;

        internal FolderOpenerState State
        {
            get
            {
                if (Array.TrueForAll(Owners, o => IsOurs(o) && IsOurOpen(o))) return FolderOpenerState.On;
                return Previous() != null ? FolderOpenerState.ChangedElsewhere : FolderOpenerState.Off;
            }
        }

        /// <summary>Whether the About checkbox takes a click: only installed copies can turn it on.</summary>
        internal bool CanChange => _installed || State == FolderOpenerState.On;

        /// <summary>
        /// The About checkbox's tooltip. A portable copy that finds it on (an installed copy turned
        /// it on) may turn it off, so it says nothing about installing until it is off.
        /// </summary>
        internal string NoteKey =>
            !_installed && State != FolderOpenerState.On ? "Str_About_OpenFoldersPortable"
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

            // Per owner: one still set to KillerShell (another program took only the other one)
            // keeps the value saved for it, so "KillerShell" is never what gets put back. The same
            // goes for each open verb.
            string[] saved = (Previous() ?? "|").Split('|');
            string[] previous = new string[Owners.Length];
            for (int i = 0; i < Owners.Length; i++)
            {
                previous[i] = IsOurs(Owners[i]) ? (i < saved.Length ? saved[i] : string.Empty) : Read(Owners[i]);
                if (!IsOurOpen(Owners[i])) SaveOpen(i);
            }
            _registry.SetString(RegistryHive.CurrentUser, SettingsKey, PreviousSetting, string.Join("|", previous));
            EnsureVerbs();
            foreach (string owner in Owners)
                _registry.SetString(RegistryHive.CurrentUser, owner, null, Verb);
            // Last: an open verb is only ever live behind a default that is KillerShell's (Repair).
            EnsureOpen();
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
            // The open verbs: another program's comes back where ours is still in its place.
            for (int i = 0; i < Owners.Length; i++)
                if (IsOurOpen(Owners[i])) RestoreOpen(i);
            _registry.DeleteKey(RegistryHive.CurrentUser, SavedOpenKey);
            _registry.DeleteValue(RegistryHive.CurrentUser, SettingsKey, PreviousSetting);
        }

        /// <summary>
        /// For a copy being removed (UserHives.cs): turns it off where this user's open verb runs
        /// that copy, and leaves a user whose folders open in a copy of their own alone.
        /// </summary>
        internal void TurnOffFor(string exePath)
        {
            string command = VerbCommand(exePath);
            if (Array.Exists(Owners, o => string.Equals(OpenCommand(o), command, StringComparison.OrdinalIgnoreCase)))
                TurnOff();
            // A verb of this user's naming the removed copy (EnsureVerbs writes one over an
            // all-users verb from an older version) would stay in the right-click menu, dead.
            foreach (string owner in Owners)
            {
                string key = owner + @"\" + Verb;
                if (string.Equals(_registry.GetString(RegistryHive.CurrentUser, key + @"\command", null), command,
                                  StringComparison.OrdinalIgnoreCase))
                    _registry.DeleteKey(RegistryHive.CurrentUser, key);
            }
        }

        /// <summary>
        /// While it is on, makes sure the verbs it relies on still exist and run this copy. An open
        /// verb whose default something else has taken since (another file manager, a reset, a
        /// TurnOn that stopped halfway) goes back with it: the box shows off then, so programs
        /// must not keep reaching KillerShell either.
        /// </summary>
        internal void Repair()
        {
            for (int i = 0; i < Owners.Length; i++)
                if (!IsOurs(Owners[i]) && IsOurOpen(Owners[i])) RestoreOpen(i);
            if (State != FolderOpenerState.On || !_installed) return;
            EnsureVerbs();
            EnsureOpen();
        }

        private bool IsOurs(string owner) => string.Equals(Read(owner), Verb, StringComparison.OrdinalIgnoreCase);

        private string? OpenCommand(string owner) => _registry.GetString(RegistryHive.CurrentUser, owner + @"\open\command", null);

        /// <summary>
        /// An open verb any copy of KillerShell wrote. Not only this copy's: the one left behind by
        /// a removed duplicate install is still ours to mend and to take away.
        /// </summary>
        private bool IsOurOpen(string owner)
        {
            string? command = OpenCommand(owner);
            return command != null && command.StartsWith("\"", StringComparison.Ordinal)
                && command.EndsWith(@"\KillerShell.exe""" + Arguments, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Where another program's open verb waits, whole; missing when there was none.</summary>
        private static string SavedOpen(int i) => SavedOpenKey + @"\" + OwnerNames[i];

        /// <summary>
        /// Sets another program's open verb aside as it is: its command with the value kind it had,
        /// and whatever handler, DelegateExecute or DDE conversation runs in place of it or beside it.
        /// </summary>
        private void SaveOpen(int i)
        {
            _registry.DeleteKey(RegistryHive.CurrentUser, SavedOpen(i));
            _registry.CopyKey(RegistryHive.CurrentUser, Owners[i] + @"\open", SavedOpen(i));
        }

        /// <summary>
        /// Puts back the open verb set aside, or none when there was none, which hands "open"
        /// back to Explorer's on Folder.
        /// </summary>
        private void RestoreOpen(int i)
        {
            _registry.DeleteKey(RegistryHive.CurrentUser, Owners[i] + @"\open");
            _registry.CopyKey(RegistryHive.CurrentUser, SavedOpen(i), Owners[i] + @"\open");
            _registry.DeleteKey(RegistryHive.CurrentUser, SavedOpen(i));
        }

        private string Read(string owner) => _registry.GetString(RegistryHive.CurrentUser, owner, null) ?? string.Empty;

        private string? Previous() => _registry.GetString(RegistryHive.CurrentUser, SettingsKey, PreviousSetting);

        /// <summary>
        /// The verb the defaults name, as this copy runs it. A per-user verb wins over the
        /// all-users one, so one left by another copy (a portable copy registered from the
        /// Associations card and deleted since) is rewritten rather than made the double-click
        /// default. An all-users verb that already runs this copy as this version does is used as
        /// it is; one from an older version (an all-users copy updated in place keeps 1.4's
        /// command, which restores the whole session) gets this user's own verb over it.
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
                else if (string.Equals(_registry.GetString(RegistryHive.LocalMachine, key + @"\command", null), command,
                                       StringComparison.OrdinalIgnoreCase))
                    continue;
                _registry.SetString(RegistryHive.CurrentUser, key, null, "Open in KillerShell");
                _registry.SetString(RegistryHive.CurrentUser, key + @"\command", null, VerbCommand(_exePath));
            }
        }

        /// <summary>
        /// The open verb programs get: only ever this user's, always running this copy, and
        /// nothing else under it, so no handler or DDE conversation left by another program runs
        /// in place of the command or beside it.
        /// </summary>
        private void EnsureOpen()
        {
            string command = VerbCommand(_exePath);
            foreach (string owner in Owners)
            {
                if (string.Equals(OpenCommand(owner), command, StringComparison.OrdinalIgnoreCase)) continue;
                _registry.DeleteKey(RegistryHive.CurrentUser, owner + @"\open");
                _registry.SetString(RegistryHive.CurrentUser, owner + @"\open\command", null, command);
            }
        }
    }
}
