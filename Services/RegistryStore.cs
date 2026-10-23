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

        /// <summary>
        /// Copies a key and everything under it, every value with its own kind, over whatever the
        /// destination holds; a missing source copies nothing.
        /// </summary>
        void CopyKey(RegistryHive hive, string source, string destination);
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

        public void CopyKey(RegistryHive hive, string source, string destination)
        {
            using var root = Root(hive);
            using var from = root.OpenSubKey(source);
            if (from == null) return;
            using var to = root.CreateSubKey(destination);
            Copy(from, to);
        }

        private static void Copy(RegistryKey from, RegistryKey to)
        {
            // Unexpanded, so a REG_EXPAND_SZ command goes back with its %SystemRoot% intact.
            foreach (string name in from.GetValueNames())
                to.SetValue(name, from.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!,
                            from.GetValueKind(name));
            foreach (string name in from.GetSubKeyNames())
            {
                using var child = from.OpenSubKey(name);
                using var copy = to.CreateSubKey(name);
                if (child != null) Copy(child, copy);
            }
        }
    }
}
