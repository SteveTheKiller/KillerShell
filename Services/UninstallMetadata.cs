using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace KillerShell.Services
{
    // Keep the installer's recovery entry until image removal has committed successfully.
    internal sealed class UninstallMetadata
    {
        private readonly RegistryKey _hive;
        private readonly Dictionary<string, Node?> _trees = new Dictionary<string, Node?>();

        internal UninstallMetadata(RegistryKey hive, params string[] paths)
        {
            _hive = hive;
            foreach (string path in paths)
            {
                using var key = hive.OpenSubKey(path);
                _trees.Add(path, key == null ? null : Capture(key));
            }
        }

        internal void Restore()
        {
            var failures = new List<Exception>();
            foreach (var tree in _trees)
            {
                if (tree.Value == null) continue;
                try
                {
                    using var key = _hive.CreateSubKey(tree.Key);
                    if (key == null) throw new UnauthorizedAccessException(tree.Key);
                    RestoreNode(key, tree.Value);
                }
                catch (Exception ex) { failures.Add(ex); }
            }
            if (failures.Count > 0)
                throw new AggregateException("Uninstall recovery metadata could not be restored.", failures);
        }

        private static Node Capture(RegistryKey key)
        {
            var node = new Node();
            foreach (string name in key.GetValueNames())
                node.Values.Add(name, Tuple.Create(
                    key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(name)));
            foreach (string name in key.GetSubKeyNames())
            {
                using var child = key.OpenSubKey(name);
                if (child == null) throw new InvalidOperationException("Uninstall metadata changed while being captured.");
                node.Children.Add(name, Capture(child));
            }
            return node;
        }

        private static void RestoreNode(RegistryKey key, Node node)
        {
            foreach (var value in node.Values)
                key.SetValue(value.Key, value.Value.Item1, value.Value.Item2);
            foreach (var child in node.Children)
            {
                using var target = key.CreateSubKey(child.Key);
                if (target == null) throw new UnauthorizedAccessException(child.Key);
                RestoreNode(target, child.Value);
            }
            key.Flush();
        }

        private sealed class Node
        {
            internal readonly Dictionary<string, Tuple<object, RegistryValueKind>> Values = new Dictionary<string, Tuple<object, RegistryValueKind>>();
            internal readonly Dictionary<string, Node> Children = new Dictionary<string, Node>();
        }
    }
}
