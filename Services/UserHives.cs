using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

// Every local user's own registry, for the one job that has to reach past the account running it:
// an all-users uninstall run as SYSTEM (RMM, Intune) or by another administrator putting back each
// user's "Open folders in KillerShell" (FolderOpenerSetting.cs, DefaultTerminal.cs). Left alone,
// that user's open verb would name a deleted exe and no folder would open for them.
//
// A signed-in user's hives are already under HKEY_USERS: SID for NTUSER.DAT and SID_Classes for
// UsrClass.dat, where HKCU\Software\Classes lives. A profile that is not signed in is loaded from
// those two files for as long as the work takes and unloaded straight after. Every key is opened
// and closed inside one call of UserHiveStore, so nothing holds the hive open when it unloads; a
// hive left loaded would hand that user a temporary profile at their next sign-in.
namespace KillerShell.Services
{
    internal static class UserHives
    {
        private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
        private const string LoadPrefix = "KillerShell-Uninstall-";

        /// <summary>
        /// Runs <paramref name="work"/> against each local user's registry other than the running
        /// account's. A profile that cannot be reached is skipped; nothing here throws.
        /// </summary>
        internal static void ForEachOtherUser(Action<IRegistryStore> work)
        {
            string? self;
            using (var identity = WindowsIdentity.GetCurrent()) self = identity.User?.Value;

            string[] sids;
            using (var list = Registry.LocalMachine.OpenSubKey(ProfileList))
                sids = list?.GetSubKeyNames() ?? [];

            bool privileged = false;
            foreach (string sid in sids)
            {
                // Local and domain accounts; not SYSTEM, LOCAL SERVICE or NETWORK SERVICE.
                if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) ||
                    string.Equals(sid, self, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (IsLoaded(sid))
                    {
                        work(new UserHiveStore(WindowsRegistryStore.Instance, sid, IsLoaded(sid + "_Classes") ? sid + "_Classes" : null));
                        continue;
                    }
                    if (!privileged) privileged = EnableHivePrivileges();
                    if (privileged) WithUnloadedProfile(sid, work);
                }
                catch { /* the next profile still gets its turn */ }
            }
        }

        private static bool IsLoaded(string name)
        {
            using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
            using var key = users.OpenSubKey(name);
            return key != null;
        }

        private static void WithUnloadedProfile(string sid, Action<IRegistryStore> work)
        {
            string? profile;
            using (var entry = Registry.LocalMachine.OpenSubKey(ProfileList + @"\" + sid))
                profile = entry?.GetValue("ProfileImagePath") as string;
            if (string.IsNullOrEmpty(profile)) return;

            string user = Path.Combine(profile, "NTUSER.DAT");
            string classes = Path.Combine(profile, @"AppData\Local\Microsoft\Windows\UsrClass.dat");
            // Folder verbs live in UsrClass.dat; without it there is nothing to put back.
            if (!File.Exists(user) || !File.Exists(classes)) return;

            string userName = LoadPrefix + sid, classesName = userName + "_Classes";
            if (RegLoadKey(HkeyUsers, userName, user) != 0) return;
            try
            {
                if (RegLoadKey(HkeyUsers, classesName, classes) != 0) return;
                try { work(new UserHiveStore(WindowsRegistryStore.Instance, userName, classesName)); }
                finally { Unload(classesName); }
            }
            finally { Unload(userName); }
        }

        /// <summary>Unloads a hive this class loaded, collecting first if a handle is somehow still open.</summary>
        private static void Unload(string name)
        {
            if (RegUnLoadKey(HkeyUsers, name) == 0) return;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            RegUnLoadKey(HkeyUsers, name);
        }

        /// <summary>
        /// Loading another user's hive takes the backup and restore privileges, which an
        /// administrator's token and SYSTEM hold but have switched off.
        /// </summary>
        private static bool EnableHivePrivileges()
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out IntPtr token))
                return false;
            try
            {
                foreach (string name in new[] { "SeBackupPrivilege", "SeRestorePrivilege" })
                {
                    if (!LookupPrivilegeValue(null, name, out long luid)) return false;
                    var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = PrivilegeEnabled };
                    if (!AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero) ||
                        Marshal.GetLastWin32Error() != 0)
                        return false;
                }
                return true;
            }
            finally { CloseHandle(token); }
        }

        private static readonly IntPtr HkeyUsers = new(unchecked((int)0x80000003));
        private const uint TokenAdjustPrivileges = 0x0020;
        private const uint TokenQuery = 0x0008;
        private const int PrivilegeEnabled = 0x0002;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct TokenPrivileges
        {
            public int Count;
            public long Luid;
            public int Attributes;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegLoadKey(IntPtr key, string subKey, string file);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegUnLoadKey(IntPtr key, string subKey);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state,
                                                         int length, IntPtr previous, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
