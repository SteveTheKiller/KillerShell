using System;
using Microsoft.Win32;

// Another user's registry, seen the way FolderOpenerSetting.cs sees the current one. HKCU becomes
// that user's hive under HKEY_USERS, and HKCU\Software\Classes becomes their classes hive
// (UsrClass.dat), which Windows keeps apart from NTUSER.DAT. Everything outside HKCU is the
// machine's and passes straight through. Used by UserHives.cs for an all-users uninstall.
namespace KillerShell.Services
{
    internal sealed class UserHiveStore : IRegistryStore
    {
        private const string Classes = @"Software\Classes";

        private readonly IRegistryStore _inner;
        private readonly string _root;
        private readonly string? _classesRoot;

        /// <param name="root">The user's hive under HKEY_USERS: their SID when signed in.</param>
        /// <param name="classesRoot">Their classes hive, or null when there is none to reach.</param>
        internal UserHiveStore(IRegistryStore inner, string root, string? classesRoot)
        {
            _inner = inner;
            _root = root;
            _classesRoot = classesRoot;
        }

        /// <summary>Where an HKCU key lives under HKEY_USERS, or null for a classes key with no classes hive.</summary>
        private string? Map(string key)
        {
            key = key.Trim('\\');
            if (key.Equals(Classes, StringComparison.OrdinalIgnoreCase))
                return _classesRoot;
            if (key.StartsWith(Classes + @"\", StringComparison.OrdinalIgnoreCase))
                return _classesRoot == null ? null : _classesRoot + key.Substring(Classes.Length);
            return _root + @"\" + key;
        }

        public string? GetString(RegistryHive hive, string key, string? name)
        {
            if (hive != RegistryHive.CurrentUser) return _inner.GetString(hive, key, name);
            string? mapped = Map(key);
            return mapped == null ? null : _inner.GetString(RegistryHive.Users, mapped, name);
        }

        public void SetString(RegistryHive hive, string key, string? name, string value)
        {
            if (hive != RegistryHive.CurrentUser) { _inner.SetString(hive, key, name, value); return; }
            if (Map(key) is string mapped) _inner.SetString(RegistryHive.Users, mapped, name, value);
        }

        public void DeleteValue(RegistryHive hive, string key, string name)
        {
            if (hive != RegistryHive.CurrentUser) { _inner.DeleteValue(hive, key, name); return; }
            if (Map(key) is string mapped) _inner.DeleteValue(RegistryHive.Users, mapped, name);
        }

        public bool KeyExists(RegistryHive hive, string key)
        {
            if (hive != RegistryHive.CurrentUser) return _inner.KeyExists(hive, key);
            return Map(key) is string mapped && _inner.KeyExists(RegistryHive.Users, mapped);
        }

        public void DeleteKey(RegistryHive hive, string key)
        {
            if (hive != RegistryHive.CurrentUser) { _inner.DeleteKey(hive, key); return; }
            if (Map(key) is string mapped) _inner.DeleteKey(RegistryHive.Users, mapped);
        }

        public void CopyKey(RegistryHive hive, string source, string destination)
        {
            if (hive != RegistryHive.CurrentUser) { _inner.CopyKey(hive, source, destination); return; }
            if (Map(source) is string from && Map(destination) is string to)
                _inner.CopyKey(RegistryHive.Users, from, to);
        }
    }
}
