using System;
using System.Collections.Generic;
using System.Linq;
using KillerShell.Services;
using Microsoft.Win32;

namespace KillerShell.Tests.Services;

/// <summary>An in-memory registry, so tests of registry-writing features never touch the real one.</summary>
internal sealed class FakeRegistryStore : IRegistryStore
{
    private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);

    private static string Path(RegistryHive hive, string key) => hive + @"\" + key.Trim('\\');

    public string? GetString(RegistryHive hive, string key, string? name)
        => _keys.TryGetValue(Path(hive, key), out var values) && values.TryGetValue(name ?? string.Empty, out var v) ? v : null;

    public void SetString(RegistryHive hive, string key, string? name, string value)
    {
        string path = Path(hive, key);
        if (!_keys.TryGetValue(path, out var values))
            _keys[path] = values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        values[name ?? string.Empty] = value;
    }

    public void DeleteValue(RegistryHive hive, string key, string name)
    {
        if (_keys.TryGetValue(Path(hive, key), out var values)) values.Remove(name);
    }

    public bool KeyExists(RegistryHive hive, string key)
    {
        string path = Path(hive, key);
        return _keys.Keys.Any(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                                   k.StartsWith(path + @"\", StringComparison.OrdinalIgnoreCase));
    }

    public void DeleteKey(RegistryHive hive, string key)
    {
        string path = Path(hive, key);
        foreach (var k in _keys.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                                               k.StartsWith(path + @"\", StringComparison.OrdinalIgnoreCase)).ToList())
            _keys.Remove(k);
    }

    public void CopyKey(RegistryHive hive, string source, string destination)
    {
        string from = Path(hive, source), to = Path(hive, destination);
        foreach (var k in _keys.Keys.Where(k => k.Equals(from, StringComparison.OrdinalIgnoreCase) ||
                                               k.StartsWith(from + @"\", StringComparison.OrdinalIgnoreCase)).ToList())
            _keys[to + k.Substring(from.Length)] = new Dictionary<string, string>(_keys[k], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Creates an empty key, as a packaged app's COM registration appears to a reader.</summary>
    internal void CreateKey(RegistryHive hive, string key)
    {
        string path = Path(hive, key);
        if (!_keys.ContainsKey(path)) _keys[path] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
