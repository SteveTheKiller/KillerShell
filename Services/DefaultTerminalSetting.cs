using System;
using Microsoft.Win32;
using KillerShell.Terminal;

// "Use KillerShell as the default terminal", the About card checkbox.
//
// Windows' console reads HKCU\Console\%%Startup: DelegationConsole names the console host that
// serves a console program's calls, DelegationTerminal the terminal it hands the screen to
// (Terminal/TerminalHandoff.cs). Settings lists only packaged apps that ship both halves, so
// KillerShell is never one of its choices; turning this on writes the pair directly, as Settings
// and Windows Terminal's own setting do, with Windows Terminal's console host and KillerShell's
// terminal class.
//
// It is the user's own choice made in KillerShell, so it never happens on its own and is always
// undone the same way: the pair it replaced is saved first and put back on the way out, but only
// while the values are still KillerShell's. A choice made later in Settings is never overwritten.
namespace KillerShell.Services
{
    internal enum DefaultTerminalState
    {
        /// <summary>Windows uses some other terminal.</summary>
        Off,

        /// <summary>Console programs open in KillerShell.</summary>
        On,

        /// <summary>KillerShell turned it on, then something else (Settings, a policy) chose another.</summary>
        ChangedElsewhere,
    }

    internal enum DefaultTerminalBlocker
    {
        None,

        /// <summary>A portable copy: the registration would point at an exe that may not be there tomorrow.</summary>
        Portable,

        /// <summary>No Windows Terminal console host to hand programs to KillerShell.</summary>
        NoConsoleHost,
    }

    internal sealed class DefaultTerminalSetting
    {
        internal const string StartupKey = @"Console\%%Startup";
        internal const string ConsoleValue = "DelegationConsole";
        internal const string TerminalValue = "DelegationTerminal";

        /// <summary>Where the replaced pair waits to be put back: "console|terminal", empty for missing.</summary>
        internal const string PreviousSetting = "DefaultTerminalPrevious";
        private const string SettingsKey = @"Software\KillerShell";

        /// <summary>
        /// Windows Terminal's console hosts in the order they are preferred: Stable, Preview,
        /// Canary (microsoft/terminal src/host/exe/CConsoleHandoff.h).
        /// </summary>
        internal static readonly string[] ConsoleHosts =
        [
            "{2EACA947-7F5F-4CFA-BA87-8F7FBEEFBE69}",
            "{06EC847C-C0A5-46B8-92CB-7C92F6E35CD5}",
            "{A854D02A-F2FE-44A5-BB24-D03F4CF830D4}",
        ];

        internal static string ClassId => TerminalHandoff.Clsid.ToString("B").ToUpperInvariant();

        private readonly IRegistryStore _registry;
        private readonly bool _installed;
        private readonly string _exePath;

        internal DefaultTerminalSetting(IRegistryStore registry, bool installed, string exePath)
        {
            _registry = registry;
            _installed = installed;
            _exePath = exePath;
        }

        /// <summary>The installed Windows Terminal console host to use, or null when there is none.</summary>
        internal string? ConsoleHost
        {
            get
            {
                foreach (string host in ConsoleHosts)
                    if (_registry.KeyExists(RegistryHive.ClassesRoot, @"PackagedCom\ClassIndex\" + host))
                        return host;
                return null;
            }
        }

        internal DefaultTerminalBlocker Blocker =>
            !_installed ? DefaultTerminalBlocker.Portable
          : ConsoleHost == null ? DefaultTerminalBlocker.NoConsoleHost
          : DefaultTerminalBlocker.None;

        internal DefaultTerminalState State
        {
            get
            {
                if (IsKillerShellPair()) return DefaultTerminalState.On;
                return Previous() != null ? DefaultTerminalState.ChangedElsewhere : DefaultTerminalState.Off;
            }
        }

        /// <summary>
        /// Whether the About checkbox takes a click: to turn it on, or to turn it off again after
        /// Windows Terminal has gone and the handoff no longer works.
        /// </summary>
        internal bool CanChange => State == DefaultTerminalState.On || Blocker == DefaultTerminalBlocker.None;

        /// <summary>The About checkbox's tooltip: why it cannot be turned on, what happened, or what it does.</summary>
        internal string NoteKey =>
            Blocker == DefaultTerminalBlocker.Portable ? "Str_About_DefaultTerminalPortable"
          : Blocker == DefaultTerminalBlocker.NoConsoleHost ? "Str_About_DefaultTerminalNoHost"
          : State == DefaultTerminalState.ChangedElsewhere ? "Str_About_DefaultTerminalChanged"
          : "Str_About_DefaultTerminalHint";

        /// <summary>
        /// Whether the card shows NoteKey as text under the checkbox, not only as its tooltip: a
        /// disabled checkbox cannot take keyboard focus, so its tooltip is out of keyboard reach.
        /// </summary>
        internal bool ShowsNote => NoteKey != "Str_About_DefaultTerminalHint";

        /// <summary>Makes KillerShell the default terminal. False when it cannot be, with nothing changed.</summary>
        internal bool TurnOn()
        {
            if (State == DefaultTerminalState.On)
            {
                Repair();
                return true;
            }
            string? host = ConsoleHost;
            if (Blocker != DefaultTerminalBlocker.None || host == null) return false;

            EnsureClassRegistered();
            _registry.SetString(RegistryHive.CurrentUser, SettingsKey, PreviousSetting,
                                Read(ConsoleValue) + "|" + Read(TerminalValue));
            _registry.SetString(RegistryHive.CurrentUser, StartupKey, ConsoleValue, host);
            _registry.SetString(RegistryHive.CurrentUser, StartupKey, TerminalValue, ClassId);
            return true;
        }

        /// <summary>
        /// Puts back the pair TurnOn replaced, while the values are still KillerShell's; a choice
        /// made since in Settings stays. Also what uninstall runs.
        /// </summary>
        internal void TurnOff()
        {
            if (IsKillerShellPair())
            {
                string[] previous = (Previous() ?? "|").Split('|');
                Restore(ConsoleValue, previous[0]);
                Restore(TerminalValue, previous.Length > 1 ? previous[1] : string.Empty);
            }
            _registry.DeleteValue(RegistryHive.CurrentUser, SettingsKey, PreviousSetting);
        }

        private void Restore(string name, string value)
        {
            if (value.Length == 0) _registry.DeleteValue(RegistryHive.CurrentUser, StartupKey, name);
            else _registry.SetString(RegistryHive.CurrentUser, StartupKey, name, value);
        }

        /// <summary>
        /// While it is on, makes sure the class the pair names is registered. Removing a duplicate
        /// install, or an all-users install from before the class existed, can leave the pair
        /// pointing at nothing. KillerShell runs this at every start of an installed copy.
        /// </summary>
        internal void Repair()
        {
            if (State == DefaultTerminalState.On && _installed) EnsureClassRegistered();
        }

        private bool IsKillerShellPair()
            => string.Equals(Read(TerminalValue), ClassId, StringComparison.OrdinalIgnoreCase) &&
               Array.Exists(ConsoleHosts, h => string.Equals(h, Read(ConsoleValue), StringComparison.OrdinalIgnoreCase));

        private string Read(string name) => _registry.GetString(RegistryHive.CurrentUser, StartupKey, name) ?? string.Empty;

        private string? Previous() => _registry.GetString(RegistryHive.CurrentUser, SettingsKey, PreviousSetting);

        /// <summary>
        /// Installs register the class (RegisterClass); this covers a copy installed before they
        /// did. Per user, which is all a console program started by this user needs.
        /// </summary>
        private void EnsureClassRegistered()
        {
            string key = @"Software\Classes\CLSID\" + ClassId;
            if (_registry.KeyExists(RegistryHive.CurrentUser, key) || _registry.KeyExists(RegistryHive.LocalMachine, key)) return;
            RegisterClass(_registry, machine: false, _exePath);
        }

        /// <summary>
        /// KillerShell's terminal class: what COM starts when DelegationTerminal names it. Written
        /// by every install, and harmless until DelegationTerminal points at it.
        /// </summary>
        internal static void RegisterClass(IRegistryStore registry, bool machine, string exePath)
        {
            var hive = machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
            string key = @"Software\Classes\CLSID\" + ClassId;
            registry.SetString(hive, key, null, "KillerShell terminal");
            registry.SetString(hive, key + @"\LocalServer32", null, "\"" + exePath + "\"");
        }

        /// <summary>Removes the class. Uninstall runs it after TurnOff.</summary>
        internal static void UnregisterClass(IRegistryStore registry, bool machine)
            => registry.DeleteKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                                  @"Software\Classes\CLSID\" + ClassId);
    }
}
