using Microsoft.Win32;

// The registry as the features that change Windows' own settings see it (DefaultTerminalSetting.cs).
// An interface so their tests run against an in-memory copy and never write the real registry.
namespace KillerShell.Services
{
    internal interface IRegistryStore
    {
        /// <summary>A string value, or null when the key or value is missing. A null name is the default value.</summary>
        string? GetString(RegistryHive hive, string key, string? name);

        /// <summary>Writes a string value, creating the key if needed. Throws when refused.</summary>
        void SetString(RegistryHive hive, string key, string? name, string value);

        /// <summary>Removes a value; a missing key or value is not an error.</summary>
        void DeleteValue(RegistryHive hive, string key, string name);

        bool KeyExists(RegistryHive hive, string key);

        /// <summary>Removes a key and everything under it; a missing key is not an error.</summary>
        void DeleteKey(RegistryHive hive, string key);
    }

    internal sealed class WindowsRegistryStore : IRegistryStore
    {
        internal static readonly WindowsRegistryStore Instance = new();

        private static RegistryKey Root(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Default);

        public string? GetString(RegistryHive hive, string key, string? name)
        {
            try
            {
                using var root = Root(hive);
                using var k = root.OpenSubKey(key);
                return k?.GetValue(name ?? string.Empty) as string;
            }
            catch { return null; }
        }

        public void SetString(RegistryHive hive, string key, string? name, string value)
        {
            using var root = Root(hive);
            using var k = root.CreateSubKey(key);
            k.SetValue(name ?? string.Empty, value, RegistryValueKind.String);
        }

        public void DeleteValue(RegistryHive hive, string key, string name)
        {
            using var root = Root(hive);
            using var k = root.OpenSubKey(key, writable: true);
            k?.DeleteValue(name, throwOnMissingValue: false);
        }

        public bool KeyExists(RegistryHive hive, string key)
        {
            try
            {
                using var root = Root(hive);
                using var k = root.OpenSubKey(key);
                return k != null;
            }
            catch { return false; }
        }

        public void DeleteKey(RegistryHive hive, string key)
        {
            using var root = Root(hive);
            root.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
    }
}
