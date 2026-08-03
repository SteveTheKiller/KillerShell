using System;
using System.Collections.Generic;
using Microsoft.Win32;

// The fabricated registry --demo shows in the Registry Editor tab, same idea and same rules as
// Services\DemoFileSystem.cs for the file browser: everything here is FIXED (no live machine
// behind any of it), and it leans on the same invented MSP workstation the rest of demo mode
// already describes, so a capture of this tab beside a capture of the fake terminal or file
// listing does not contradict either of them.
//
// A couple of entries are the SAME data DemoMode.cs's DemoReg() already writes into a fabricated
// .reg file (KillerTools\Deploy, Explorer\Advanced) - one machine, read two different ways,
// should not disagree with itself. HKEY_CLASSES_ROOT\.386's PerceivedType value is a deliberate
// nod to the real bug that shipped in 1.1.1: the exact key/value that used to crash the tab.
//
// RegistryKey is sealed - there is no faking "a RegistryKey that isn't real" the way DemoFs fakes
// a file listing without touching System.IO. Instead RegistryNode.LoadChildren and
// RegistryEditorControl.LoadValues branch on MainWindow.DemoMode BEFORE they ever call
// RegistryPathHelper.OpenKey, and read this table instead - the live code path is untouched, a
// demo tree just never reaches it.
namespace KillerShell.Services
{
    internal static class DemoRegistry
    {
        internal readonly struct ValueEntry
        {
            internal readonly string Name;
            internal readonly RegistryValueKind Kind;
            internal readonly object Value;
            internal ValueEntry(string name, RegistryValueKind kind, object value)
            { Name = name; Kind = kind; Value = value; }
        }

        private static readonly Dictionary<string, List<string>> Children =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, List<ValueEntry>> Values =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly List<string> NoChildren = new();
        private static readonly List<ValueEntry> NoValues = new();

        /// <summary>Subkey NAMES directly under <paramref name="fullPath"/> ("HIVE\Sub\Key"),
        /// already sorted the way RegistryNode.LoadChildren sorts real ones. Empty for a key that
        /// was never invented, same "nothing wrong with an empty folder" reasoning DemoFs.Children
        /// documents.</summary>
        internal static IReadOnlyList<string> ChildrenOf(string fullPath)
            => Children.TryGetValue(fullPath, out var kids) ? kids : NoChildren;

        /// <summary>Values directly on <paramref name="fullPath"/>, unsorted - LoadValues applies
        /// the same default-first/alphabetical order it already applies to real data.</summary>
        internal static IReadOnlyList<ValueEntry> ValuesOf(string fullPath)
            => Values.TryGetValue(fullPath, out var vals) ? vals : NoValues;

        private static void Key(string fullPath, params string[] subKeyNames)
        {
            var sorted = new List<string>(subKeyNames);
            sorted.Sort(StringComparer.OrdinalIgnoreCase);
            Children[fullPath] = sorted;
        }

        private static void Val(string fullPath, params ValueEntry[] entries)
            => Values[fullPath] = new List<ValueEntry>(entries);

        private static ValueEntry S(string name, string value)  => new(name, RegistryValueKind.String, value);
        private static ValueEntry D(string name, int value)     => new(name, RegistryValueKind.DWord, value);
        private static ValueEntry B(string name, params byte[] value) => new(name, RegistryValueKind.Binary, value);

        static DemoRegistry()
        {
            // ── HKEY_CLASSES_ROOT ────────────────────────────────
            Key("HKEY_CLASSES_ROOT", ".386", ".ps1", ".reg", "Microsoft.PowerShellScript.1");

            // The exact key that used to crash the tab (1.1.1's fix) - kept here on purpose so a
            // screenshot of this fix can be taken against the SAME entry the bug report was about.
            Val(@"HKEY_CLASSES_ROOT\.386",
                S("", "vxdfile"),
                S("PerceivedType", "text"));

            Val(@"HKEY_CLASSES_ROOT\.ps1", S("", "Microsoft.PowerShellScript.1"));
            Val(@"HKEY_CLASSES_ROOT\.reg", S("", "regfile"));

            Key(@"HKEY_CLASSES_ROOT\Microsoft.PowerShellScript.1", "shell");
            Val(@"HKEY_CLASSES_ROOT\Microsoft.PowerShellScript.1", S("", "Windows PowerShell Script File"));
            Key(@"HKEY_CLASSES_ROOT\Microsoft.PowerShellScript.1\shell", "open");
            Key(@"HKEY_CLASSES_ROOT\Microsoft.PowerShellScript.1\shell\open", "command");
            Val(@"HKEY_CLASSES_ROOT\Microsoft.PowerShellScript.1\shell\open\command",
                S("", @"""C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"" ""%1"""));

            // ── HKEY_CURRENT_USER ────────────────────────────────
            Key("HKEY_CURRENT_USER", "Software");
            Key(@"HKEY_CURRENT_USER\Software", "KillerTools", "Microsoft");
            Key(@"HKEY_CURRENT_USER\Software\KillerTools", "KillerShell");
            Val(@"HKEY_CURRENT_USER\Software\KillerTools\KillerShell",
                S("LastRun", "2026-07-03"),
                S("Theme", "Dark"),
                S("Accent", "Red"));

            Key(@"HKEY_CURRENT_USER\Software\Microsoft", "Windows");
            Key(@"HKEY_CURRENT_USER\Software\Microsoft\Windows", "CurrentVersion");
            Key(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion", "Explorer");
            Key(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer", "Advanced");
            // Same three values, same data, as the fabricated .reg file DemoMode.cs's DemoReg()
            // writes for this exact key - one invented machine, not two that disagree.
            Val(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                D("HideFileExt", 0),
                D("Hidden", 1),
                D("ShowSuperHidden", 0));

            // ── HKEY_LOCAL_MACHINE ───────────────────────────────
            Key("HKEY_LOCAL_MACHINE", "SOFTWARE");
            Key(@"HKEY_LOCAL_MACHINE\SOFTWARE", "KillerTools", "Microsoft");

            Key(@"HKEY_LOCAL_MACHINE\SOFTWARE\KillerTools", "Deploy");
            Val(@"HKEY_LOCAL_MACHINE\SOFTWARE\KillerTools\Deploy",
                S("LastRun", "2026-06-29"),
                S("Channel", "stable"),
                D("Retries", 3));

            Key(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft", "Windows NT");
            Key(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT", "CurrentVersion");
            Val(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                S("ProductName", "Windows 11 Pro"),
                S("DisplayVersion", "24H2"),
                S("CurrentBuild", "26100"),
                S("RegisteredOwner", "steve"),
                B("DigitalProductId", 0xA4, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00));

            // ── HKEY_USERS ───────────────────────────────────────
            // The same SID the fake Security log's logon events use (EventViewerControl.cs
            // PopulateDemoEvents) - so a capture with both tabs open names the same account.
            Key("HKEY_USERS", ".DEFAULT", "S-1-5-21-111111111-222222222-333333333-1001");

            // ── HKEY_CURRENT_CONFIG ──────────────────────────────
            Key("HKEY_CURRENT_CONFIG", "System");
            Key(@"HKEY_CURRENT_CONFIG\System", "CurrentControlSet");
        }
    }
}
