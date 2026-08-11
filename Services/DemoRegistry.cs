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

        private static readonly List<string> NoChildren = [];
        private static readonly List<ValueEntry> NoValues = [];

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
            => Values[fullPath] = [.. entries];

        private static ValueEntry S(string name, string value)  => new(name, RegistryValueKind.String, value);
        private static ValueEntry D(string name, int value)     => new(name, RegistryValueKind.DWord, value);
        private static ValueEntry B(string name, params byte[] value) => new(name, RegistryValueKind.Binary, value);

        // The other three kinds the grid can label and format (RegistryValueFormat in
        // Tools\RegistryEditorControl.cs handles all six). Present so the Type column has
        // something other than REG_SZ and REG_DWORD in it: a capture of a registry editor that
        // only ever shows two of the six types does not demonstrate the editor reads the rest.
        // The CLR types matter - DataLabel casts them - so a MultiString must be a string array
        // and a QWord must be a long, not an int.
        private static ValueEntry E(string name, string value)  => new(name, RegistryValueKind.ExpandString, value);
        private static ValueEntry M(string name, params string[] value) => new(name, RegistryValueKind.MultiString, value);
        private static ValueEntry Q(string name, long value)    => new(name, RegistryValueKind.QWord, value);

        static DemoRegistry()
        {
            // ── HKEY_CLASSES_ROOT ────────────────────────────────
            // The extensions KillerShell itself can associate (Associations.cs) plus the
            // ProgIDs behind them, so expanding this hive lands on the keys the associations card
            // is actually talking about rather than on an arbitrary slice of a merged view.
            Key("HKEY_CLASSES_ROOT",
                ".386", ".log", ".md", ".ps1", ".reg", ".txt", ".yml", ".zip",
                "KillerShell.Document", "Microsoft.PowerShellScript.1", "regfile", "txtfile");

            // The exact key that used to crash the tab (1.1.1's fix) - kept here on purpose so a
            // screenshot of this fix can be taken against the SAME entry the bug report was about.
            Val(@"HKEY_CLASSES_ROOT\.386",
                S("", "vxdfile"),
                S("PerceivedType", "text"));

            Val(@"HKEY_CLASSES_ROOT\.log", S("", "KillerShell.Document"), S("PerceivedType", "text"));
            Val(@"HKEY_CLASSES_ROOT\.md",  S("", "KillerShell.Document"), S("PerceivedType", "text"));
            Val(@"HKEY_CLASSES_ROOT\.ps1", S("", "Microsoft.PowerShellScript.1"));
            Val(@"HKEY_CLASSES_ROOT\.reg", S("", "regfile"));
            Val(@"HKEY_CLASSES_ROOT\.txt", S("", "txtfile"), S("Content Type", "text/plain"), S("PerceivedType", "text"));
            Val(@"HKEY_CLASSES_ROOT\.yml", S("", "KillerShell.Document"), S("PerceivedType", "text"));
            Val(@"HKEY_CLASSES_ROOT\.zip",
                S("", "CompressedFolder"),
                S("Content Type", "application/x-zip-compressed"),
                S("PerceivedType", "compressed"));

            Key(@"HKEY_CLASSES_ROOT\Microsoft.PowerShellScript.1", "shell");
            Val(@"HKEY_CLASSES_ROOT\Microsoft.PowerShellScript.1", S("", "Windows PowerShell Script File"));
            Key(@"HKEY_CLASSES_ROOT\Microsoft.PowerShellScript.1\shell", "open");
            Key(@"HKEY_CLASSES_ROOT\Microsoft.PowerShellScript.1\shell\open", "command");
            Val(@"HKEY_CLASSES_ROOT\Microsoft.PowerShellScript.1\shell\open\command",
                S("", @"""C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"" ""%1"""));

            // The app's own ProgID, written exactly the way Associations.cs writes a real
            // one - DefaultIcon pointing at the extracted .ico beside the exe included.
            Key(@"HKEY_CLASSES_ROOT\KillerShell.Document", "DefaultIcon", "shell");
            Val(@"HKEY_CLASSES_ROOT\KillerShell.Document",
                S("", "Text Document"),
                S("FriendlyTypeName", "KillerShell Document"),
                S("AlwaysShowExt", "1"));
            Val(@"HKEY_CLASSES_ROOT\KillerShell.Document\DefaultIcon",
                S("", @"C:\Program Files\KillerShell\text-file.ico,0"));
            Key(@"HKEY_CLASSES_ROOT\KillerShell.Document\shell", "edit", "open");
            Key(@"HKEY_CLASSES_ROOT\KillerShell.Document\shell\open", "command");
            Val(@"HKEY_CLASSES_ROOT\KillerShell.Document\shell\open\command",
                S("", @"""C:\Program Files\KillerShell\KillerShell.exe"" ""%1"""));
            Key(@"HKEY_CLASSES_ROOT\KillerShell.Document\shell\edit", "command");
            Val(@"HKEY_CLASSES_ROOT\KillerShell.Document\shell\edit\command",
                S("", @"""C:\Program Files\KillerShell\KillerShell.exe"" /edit ""%1"""));

            Key(@"HKEY_CLASSES_ROOT\txtfile", "shell");
            Val(@"HKEY_CLASSES_ROOT\txtfile", S("", "Text Document"), S("EditFlags", "0x00000000"));
            Key(@"HKEY_CLASSES_ROOT\txtfile\shell", "open");
            Key(@"HKEY_CLASSES_ROOT\txtfile\shell\open", "command");
            Val(@"HKEY_CLASSES_ROOT\txtfile\shell\open\command",
                E("", @"%SystemRoot%\system32\NOTEPAD.EXE %1"));

            Key(@"HKEY_CLASSES_ROOT\regfile", "shell");
            Val(@"HKEY_CLASSES_ROOT\regfile", S("", "Registration Entries"), S("AlwaysShowExt", "1"));
            Key(@"HKEY_CLASSES_ROOT\regfile\shell", "open");
            Key(@"HKEY_CLASSES_ROOT\regfile\shell\open", "command");
            Val(@"HKEY_CLASSES_ROOT\regfile\shell\open\command",
                E("", @"regedit.exe ""%1"""));

            // ── HKEY_CURRENT_USER ────────────────────────────────
            Key("HKEY_CURRENT_USER", "Control Panel", "Environment", "Software");

            Key(@"HKEY_CURRENT_USER\Control Panel", "Desktop", "Mouse");
            Val(@"HKEY_CURRENT_USER\Control Panel\Desktop",
                S("Wallpaper", @"C:\Users\Demo\Pictures\Wallpapers\bone-grain-3840.png"),
                S("WallpaperStyle", "10"),
                D("MenuShowDelay", 200),
                D("DragFullWindows", 1),
                // The real shape of this one: a bit field of desktop effects, stored as raw bytes
                // rather than a DWORD, which is exactly why REG_BINARY is worth showing in a
                // capture at all.
                B("UserPreferencesMask", 0x9E, 0x1E, 0x07, 0x80, 0x12, 0x00, 0x00, 0x00));
            Val(@"HKEY_CURRENT_USER\Control Panel\Mouse",
                S("MouseSpeed", "1"),
                S("MouseSensitivity", "10"),
                D("MouseHoverTime", 400));

            // The per-user environment block, which is the textbook REG_EXPAND_SZ: the whole
            // reason that type exists is a Path with %USERPROFILE% still unexpanded in it.
            Val(@"HKEY_CURRENT_USER\Environment",
                E("Path", @"%USERPROFILE%\.local\bin;C:\Tools\bin"),
                E("TEMP", @"%USERPROFILE%\AppData\Local\Temp"),
                E("TMP", @"%USERPROFILE%\AppData\Local\Temp"),
                S("KILLERSHELL_PROFILE", "field"));

            Key(@"HKEY_CURRENT_USER\Software", "KillerTools", "Microsoft", "Notepad++");
            Key(@"HKEY_CURRENT_USER\Software\KillerTools", "KillerNotes", "KillerShell");

            Key(@"HKEY_CURRENT_USER\Software\KillerTools\KillerShell", "Recent", "Window");
            Val(@"HKEY_CURRENT_USER\Software\KillerTools\KillerShell",
                S("LastRun", "2026-07-03"),
                S("Theme", "Dark"),
                S("Accent", "Red"),
                D("AppScale", 100),
                D("DetailsPaneOpen", 1),
                Q("BytesScanned", 41938275610L));

            // A REG_MULTI_SZ that is genuinely a list, not a string with commas in it - which is
            // the only way to show the grid rendering one.
            Val(@"HKEY_CURRENT_USER\Software\KillerTools\KillerShell\Recent",
                M("Folders",
                  @"C:\Users\Demo\code\killer-scripts",
                  @"C:\Users\Demo\Documents\Invoices",
                  @"C:\Users\Demo\Logs",
                  @"D:\Backups"),
                M("Searches", "invoice", "TODO", "*.vbk"));

            Val(@"HKEY_CURRENT_USER\Software\KillerTools\KillerShell\Window",
                D("Left", 220), D("Top", 96), D("Width", 1480), D("Height", 940),
                D("Maximized", 0),
                // A window placement blob, which is what a REG_BINARY in a settings key really is.
                B("Placement", 0x2C, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
                               0x03, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
                               0xDC, 0x00, 0x00, 0x00, 0x60, 0x00, 0x00, 0x00));

            Key(@"HKEY_CURRENT_USER\Software\Microsoft", "Windows");
            Key(@"HKEY_CURRENT_USER\Software\Microsoft\Windows", "CurrentVersion");
            Key(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion", "Explorer", "Run");
            Key(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer",
                "Advanced", "User Shell Folders");

            // Same three values, same data, as the fabricated .reg file DemoMode.cs's DemoReg()
            // writes for this exact key - one invented machine, not two that disagree.
            Val(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                D("HideFileExt", 0),
                D("Hidden", 1),
                D("ShowSuperHidden", 0),
                D("LaunchTo", 1),
                D("NavPaneExpandToCurrentFolder", 1));

            // The shell folders the fabricated machine's own tree is built from
            // (Services\DemoFileSystem.cs), so the two describe the same profile.
            Val(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                E("Desktop",   @"%USERPROFILE%\Desktop"),
                E("Favorites", @"%USERPROFILE%\Favorites"),
                E("My Music",  @"%USERPROFILE%\Music"),
                E("My Pictures", @"%USERPROFILE%\Pictures"),
                E("My Video",  @"%USERPROFILE%\Videos"),
                E("Personal",  @"%USERPROFILE%\Documents"),
                E("Recent",    @"%USERPROFILE%\Recent"));

            Val(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run",
                S("ScreenConnect", @"""C:\Program Files (x86)\ScreenConnect Client\ScreenConnect.WindowsClient.exe"" -e Access"),
                E("OneDrive", @"%LOCALAPPDATA%\Microsoft\OneDrive\OneDrive.exe /background"));

            // ── HKEY_LOCAL_MACHINE ───────────────────────────────
            Key("HKEY_LOCAL_MACHINE", "HARDWARE", "SOFTWARE", "SYSTEM");

            Key(@"HKEY_LOCAL_MACHINE\HARDWARE", "DESCRIPTION");
            Key(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION", "System");
            Key(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System", "BIOS");
            Val(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS",
                S("BaseBoardManufacturer", "Dell Inc."),
                S("BaseBoardProduct", "0K4H7T"),
                S("BIOSVendor", "Dell Inc."),
                S("BIOSVersion", "1.14.0"),
                S("BIOSReleaseDate", "03/11/2026"),
                S("SystemFamily", "Latitude"),
                S("SystemProductName", "Latitude 7450"));

            Key(@"HKEY_LOCAL_MACHINE\SOFTWARE", "KillerTools", "Microsoft", "Policies");

            Key(@"HKEY_LOCAL_MACHINE\SOFTWARE\KillerTools", "Deploy", "KillerShell");
            Val(@"HKEY_LOCAL_MACHINE\SOFTWARE\KillerTools\Deploy",
                S("LastRun", "2026-06-29"),
                S("Channel", "stable"),
                D("Retries", 3),
                M("Sites", "Cedar Ridge", "Fairview", "Lakeside"));

            // The install record, matching the fabricated Program Files folder and the fabricated
            // process list (Services\DemoFileSystem.cs, Tools\ProcessListControl.cs).
            Val(@"HKEY_LOCAL_MACHINE\SOFTWARE\KillerTools\KillerShell",
                S("InstallPath", @"C:\Program Files\KillerShell"),
                S("Version", "1.1.0"),
                S("ReleaseDate", "2026-07-01"),
                D("InstalledForAllUsers", 1),
                Q("InstallSize", 27262976L));

            Key(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft", "Windows", "Windows Defender", "Windows NT");
            Key(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows", "CurrentVersion");
            Key(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion", "Run", "Uninstall");

            Val(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                E("SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe"),
                S("SentinelAgent", @"""C:\Program Files\SentinelOne\Sentinel Agent\SentinelUI.exe"" /minimized"));

            Key(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "KillerShell_is1");
            Val(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\KillerShell_is1",
                S("DisplayName", "KillerShell"),
                S("DisplayVersion", "1.1.0"),
                S("Publisher", "Killer Tools"),
                S("InstallLocation", @"C:\Program Files\KillerShell\"),
                S("UninstallString", @"""C:\Program Files\KillerShell\unins000.exe"""),
                D("EstimatedSize", 26624),
                D("NoModify", 1));

            Val(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender",
                D("DisableAntiSpyware", 0),
                D("PUAProtection", 1),
                E("InstallLocation", @"%ProgramFiles%\Windows Defender\"));

            Key(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT", "CurrentVersion");
            Val(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                S("ProductName", "Windows 11 Pro"),
                S("DisplayVersion", "24H2"),
                S("CurrentBuild", "26100"),
                S("BuildLabEx", "26100.1.amd64fre.ge_release.240331-1435"),
                S("EditionID", "Professional"),
                S("RegisteredOwner", "Demo"),
                S("RegisteredOrganization", "Killer Tools"),
                E("SystemRoot", @"%SystemRoot%"),
                D("InstallDate", 1751328000),
                Q("InstallTime", 133648934400000000L),
                B("DigitalProductId", 0xA4, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00,
                                      0x30, 0x30, 0x33, 0x33, 0x30, 0x2D, 0x38, 0x30,
                                      0x30, 0x30, 0x30, 0x2D, 0x30, 0x30, 0x30, 0x30));

            // A policy branch, because a locked-down endpoint is what a field tech spends the day
            // in and "why is this grayed out" is the question this tab gets opened for.
            Key(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies", "Microsoft");
            Key(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft", "Windows");
            Key(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows", "WindowsUpdate");
            Key(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "AU");
            Val(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",
                S("WUServer", "http://wsus.fairview.local:8530"),
                S("WUStatusServer", "http://wsus.fairview.local:8530"),
                D("TargetGroupEnabled", 1),
                S("TargetGroup", "Workstations"));
            Val(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU",
                D("UseWUServer", 1),
                D("NoAutoUpdate", 0),
                D("AUOptions", 4),
                D("ScheduledInstallDay", 0),
                D("ScheduledInstallTime", 3));

            // The service side of the same machine the Services view lists
            // (Tools\ProcessListControl.cs PopulateDemoServices) - the Start and Type numbers here
            // are the ones behind "Automatic" and "Disabled" over there.
            Key(@"HKEY_LOCAL_MACHINE\SYSTEM", "CurrentControlSet", "Select");
            Val(@"HKEY_LOCAL_MACHINE\SYSTEM\Select",
                D("Current", 1), D("Default", 1), D("Failed", 0), D("LastKnownGood", 1));

            Key(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet", "Control", "Services");

            Key(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", "ComputerName", "TimeZoneInformation");
            Key(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\ComputerName", "ComputerName");
            Val(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName",
                S("ComputerName", "WKS-DEMO01"));
            Val(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\TimeZoneInformation",
                S("TimeZoneKeyName", "Mountain Standard Time"),
                S("StandardName", "@tzres.dll,-1122"),
                D("Bias", 420),
                D("DaylightBias", -60),
                D("DynamicDaylightTimeDisabled", 0),
                // Packed SYSTEMTIME structures, the second real-world REG_BINARY worth showing:
                // eight little-endian shorts each, month/day-of-week/week/hour rather than a date.
                B("StandardStart", 0x00, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x01, 0x00,
                                   0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00),
                B("DaylightStart", 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x02, 0x00,
                                   0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00));

            Key(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services",
                "Dnscache", "RemoteRegistry", "SentinelAgent", "Spooler", "wuauserv");

            Val(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SentinelAgent",
                E("ImagePath", @"""C:\Program Files\SentinelOne\Sentinel Agent\SentinelServiceHost.exe"""),
                S("DisplayName", "SentinelOne Agent"),
                S("ObjectName", "LocalSystem"),
                D("Start", 2),          // SERVICE_AUTO_START
                D("Type", 16),          // SERVICE_WIN32_OWN_PROCESS
                D("ErrorControl", 1),
                M("DependOnService", "RpcSs", "BFE"));

            Val(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Spooler",
                E("ImagePath", @"%SystemRoot%\System32\spoolsv.exe"),
                S("DisplayName", "@%systemroot%\\system32\\spoolsv.exe,-1"),
                S("ObjectName", "LocalSystem"),
                D("Start", 2),
                D("Type", 272),
                M("DependOnService", "RPCSS", "http"));

            Val(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\RemoteRegistry",
                E("ImagePath", @"%SystemRoot%\system32\svchost.exe -k localService -p"),
                S("DisplayName", "@regsvc.dll,-1"),
                S("ObjectName", "NT AUTHORITY\\LocalService"),
                D("Start", 4),          // SERVICE_DISABLED, matching the Services view
                D("Type", 32),
                D("ErrorControl", 1));

            Val(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\wuauserv",
                E("ImagePath", @"%systemroot%\system32\svchost.exe -k netsvcs -p"),
                S("DisplayName", "@%systemroot%\\system32\\wuaueng.dll,-105"),
                S("ObjectName", "LocalSystem"),
                D("Start", 3),          // SERVICE_DEMAND_START, i.e. Manual
                D("Type", 32),
                D("ErrorControl", 1));

            Val(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Dnscache",
                E("ImagePath", @"%SystemRoot%\System32\svchost.exe -k NetworkService -p"),
                S("DisplayName", "@%SystemRoot%\\System32\\dnsrslvr.dll,-101"),
                S("ObjectName", "NT AUTHORITY\\NetworkService"),
                D("Start", 2),
                D("Type", 32),
                M("DependOnService", "NSI", "Tdx", "Afd"));

            // ── HKEY_USERS ───────────────────────────────────────
            // The same SID the fake Security log's logon events use (EventViewerControl.cs
            // PopulateDemoEvents) - so a capture with both tabs open names the same account.
            Key("HKEY_USERS", ".DEFAULT", "S-1-5-18", "S-1-5-21-111111111-222222222-333333333-1001");

            Key(@"HKEY_USERS\.DEFAULT", "Control Panel", "Software");
            Key(@"HKEY_USERS\.DEFAULT\Control Panel", "Desktop");
            Val(@"HKEY_USERS\.DEFAULT\Control Panel\Desktop",
                S("Wallpaper", string.Empty),
                S("ScreenSaveActive", "1"),
                S("ScreenSaveTimeOut", "600"));

            // The signed-in profile's own hive, carrying the same settings HKCU shows - which is
            // exactly the relationship the two really have, since HKCU is a link onto this key.
            Key(@"HKEY_USERS\S-1-5-21-111111111-222222222-333333333-1001", "Environment", "Software");
            Val(@"HKEY_USERS\S-1-5-21-111111111-222222222-333333333-1001\Environment",
                E("Path", @"%USERPROFILE%\.local\bin;C:\Tools\bin"),
                E("TEMP", @"%USERPROFILE%\AppData\Local\Temp"));
            Key(@"HKEY_USERS\S-1-5-21-111111111-222222222-333333333-1001\Software", "KillerTools");
            Key(@"HKEY_USERS\S-1-5-21-111111111-222222222-333333333-1001\Software\KillerTools", "KillerShell");
            Val(@"HKEY_USERS\S-1-5-21-111111111-222222222-333333333-1001\Software\KillerTools\KillerShell",
                S("Theme", "Dark"),
                S("Accent", "Red"),
                D("AppScale", 100));

            // ── HKEY_CURRENT_CONFIG ──────────────────────────────
            Key("HKEY_CURRENT_CONFIG", "Software", "System");
            Key(@"HKEY_CURRENT_CONFIG\Software", "Fonts");
            Val(@"HKEY_CURRENT_CONFIG\Software\Fonts",
                D("LogPixels", 96),
                S("FIXEDFON.FON", "vgafix.fon"),
                S("FONTS.FON", "vgasys.fon"));

            Key(@"HKEY_CURRENT_CONFIG\System", "CurrentControlSet");
            Key(@"HKEY_CURRENT_CONFIG\System\CurrentControlSet", "Control", "Enum");
            Key(@"HKEY_CURRENT_CONFIG\System\CurrentControlSet\Control", "PRINT", "VIDEO");
            Val(@"HKEY_CURRENT_CONFIG\System\CurrentControlSet\Control\PRINT",
                D("DisableServerThread", 0));
            Key(@"HKEY_CURRENT_CONFIG\System\CurrentControlSet\Control\VIDEO",
                "{9d5d1b2e-4c17-4e6a-9f0b-3a71c5d84e02}");
            Key(@"HKEY_CURRENT_CONFIG\System\CurrentControlSet\Control\VIDEO\{9d5d1b2e-4c17-4e6a-9f0b-3a71c5d84e02}",
                "0000");
            Val(@"HKEY_CURRENT_CONFIG\System\CurrentControlSet\Control\VIDEO\{9d5d1b2e-4c17-4e6a-9f0b-3a71c5d84e02}\0000",
                D("DefaultSettings.XResolution", 2560),
                D("DefaultSettings.YResolution", 1440),
                D("DefaultSettings.BitsPerPel", 32),
                D("DefaultSettings.VRefresh", 120));
        }
    }
}
