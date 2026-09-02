using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Win32;

namespace KillerShell.Services
{
    internal enum RegistryEditStatus
    {
        Success,
        AccessDenied,
        AlreadyExists,
        Failed,
    }

    internal readonly struct RegistryEditResult
    {
        internal RegistryEditStatus Status { get; }
        internal string Error { get; }
        internal bool Succeeded => Status == RegistryEditStatus.Success;

        internal RegistryEditResult(RegistryEditStatus status, string error = "")
        {
            Status = status;
            Error = error;
        }
    }

    internal enum RegistryNameError
    {
        None,
        Empty,
        ContainsBackslash,
    }

    internal static class RegistryEditorLogic
    {
        private const int MaxDisplayChars = 4000;

        internal static readonly (string Name, RegistryKey Root)[] Hives =
        [
            ("HKEY_CLASSES_ROOT", Registry.ClassesRoot),
            ("HKEY_CURRENT_USER", Registry.CurrentUser),
            ("HKEY_LOCAL_MACHINE", Registry.LocalMachine),
            ("HKEY_USERS", Registry.Users),
            ("HKEY_CURRENT_CONFIG", Registry.CurrentConfig),
        ];

        internal static RegistryNameError ValidateName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return RegistryNameError.Empty;
            return value.IndexOf('\\') >= 0 ? RegistryNameError.ContainsBackslash : RegistryNameError.None;
        }

        internal static bool TrySplitPath(string fullPath, out string hiveName, out string subKey)
        {
            hiveName = string.Empty;
            subKey = string.Empty;
            if (string.IsNullOrEmpty(fullPath)) return false;

            int separator = fullPath.IndexOf('\\');
            hiveName = separator < 0 ? fullPath : fullPath[..separator];
            subKey = separator < 0 ? string.Empty : fullPath[(separator + 1)..];
            foreach (var hive in Hives)
                if (string.Equals(hive.Name, hiveName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static RegistryKey? OpenKey(string fullPath, bool writable)
        {
            if (!TrySplitPath(fullPath, out string hiveName, out string subKey)) return null;
            foreach (var hive in Hives)
                if (string.Equals(hive.Name, hiveName, StringComparison.OrdinalIgnoreCase))
                    return subKey.Length == 0 ? hive.Root : hive.Root.OpenSubKey(subKey, writable);
            return null;
        }

        internal static string ParentPath(string fullPath)
        {
            int index = fullPath.LastIndexOf('\\');
            return index < 0 ? string.Empty : fullPath[..index];
        }

        internal static string KindLabel(RegistryValueKind kind) => kind switch
        {
            RegistryValueKind.String => "REG_SZ",
            RegistryValueKind.ExpandString => "REG_EXPAND_SZ",
            RegistryValueKind.Binary => "REG_BINARY",
            RegistryValueKind.DWord => "REG_DWORD",
            RegistryValueKind.MultiString => "REG_MULTI_SZ",
            RegistryValueKind.QWord => "REG_QWORD",
            _ => "REG_NONE",
        };

        internal static string DataLabel(object? value, RegistryValueKind kind)
        {
            switch (kind)
            {
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString:
                    return Truncate(value as string ?? string.Empty);
                case RegistryValueKind.DWord:
                    uint dword = unchecked((uint)Convert.ToInt64(value ?? 0, CultureInfo.InvariantCulture));
                    return $"0x{dword:x8} ({dword})";
                case RegistryValueKind.QWord:
                    ulong qword = unchecked((ulong)Convert.ToInt64(value ?? 0L, CultureInfo.InvariantCulture));
                    return $"0x{qword:x16} ({qword})";
                case RegistryValueKind.Binary:
                    var bytes = value as byte[] ?? [];
                    if (bytes.Length == 0) return string.Empty;
                    int shown = Math.Min(bytes.Length, MaxDisplayChars / 3);
                    string hex = string.Join(" ", bytes.Take(shown)
                        .Select(item => item.ToString("x2", CultureInfo.InvariantCulture)));
                    return shown < bytes.Length ? hex + $" ...  ({bytes.Length} bytes total)" : hex;
                case RegistryValueKind.MultiString:
                    return Truncate(string.Join("  |  ", value as string[] ?? []));
                default:
                    return Truncate(value?.ToString() ?? string.Empty);
            }
        }

        private static string Truncate(string value)
            => value.Length <= MaxDisplayChars
                ? value
                : value[..MaxDisplayChars] + $"...  ({value.Length} chars total)";

        internal static RegistryEditResult CreateKey(string parentPath, string name)
            => Write(parentPath, parent =>
            {
                using var created = parent.CreateSubKey(name);
                return created == null
                    ? new RegistryEditResult(RegistryEditStatus.AccessDenied)
                    : new RegistryEditResult(RegistryEditStatus.Success);
            });

        internal static RegistryEditResult RenameKey(string parentPath, string oldName, string newName)
            => Write(parentPath, parent =>
            {
                if (parent.GetSubKeyNames().Any(name => string.Equals(name, newName, StringComparison.OrdinalIgnoreCase)))
                    return new RegistryEditResult(RegistryEditStatus.AlreadyExists);
                using var source = parent.OpenSubKey(oldName, writable: false);
                using var destination = parent.CreateSubKey(newName);
                if (source == null || destination == null)
                    return new RegistryEditResult(RegistryEditStatus.AccessDenied);
                CopyKeyContents(source, destination);
                parent.DeleteSubKeyTree(oldName);
                return new RegistryEditResult(RegistryEditStatus.Success);
            });

        internal static RegistryEditResult DeleteKey(string parentPath, string name)
            => Write(parentPath, parent =>
            {
                parent.DeleteSubKeyTree(name);
                return new RegistryEditResult(RegistryEditStatus.Success);
            });

        internal static RegistryEditResult SetValue(string path, string name, object value, RegistryValueKind kind)
            => Write(path, key =>
            {
                key.SetValue(name, value, kind);
                return new RegistryEditResult(RegistryEditStatus.Success);
            });

        internal static RegistryEditResult CreateValue(string path, string name, object value, RegistryValueKind kind)
            => Write(path, key =>
            {
                if (key.GetValueNames().Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
                    return new RegistryEditResult(RegistryEditStatus.AlreadyExists);
                key.SetValue(name, value, kind);
                return new RegistryEditResult(RegistryEditStatus.Success);
            });

        internal static RegistryEditResult RenameValue(string path, string oldName, string newName)
            => Write(path, key =>
            {
                if (key.GetValueNames().Any(existing => string.Equals(existing, newName, StringComparison.OrdinalIgnoreCase)))
                    return new RegistryEditResult(RegistryEditStatus.AlreadyExists);
                RegistryValueKind kind = key.GetValueKind(oldName);
                object? value = key.GetValue(oldName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null) return new RegistryEditResult(RegistryEditStatus.Failed);
                key.SetValue(newName, value, kind);
                key.DeleteValue(oldName);
                return new RegistryEditResult(RegistryEditStatus.Success);
            });

        internal static RegistryEditResult DeleteValue(string path, string name)
            => Write(path, key =>
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                return new RegistryEditResult(RegistryEditStatus.Success);
            });

        private static RegistryEditResult Write(string path, Func<RegistryKey, RegistryEditResult> operation)
        {
            try
            {
                using var key = OpenKey(path, writable: true);
                return key == null ? new RegistryEditResult(RegistryEditStatus.AccessDenied) : operation(key);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException
                                             or UnauthorizedAccessException
                                             or System.IO.IOException
                                             or ArgumentException)
            {
                return new RegistryEditResult(RegistryEditStatus.Failed, ex.Message);
            }
        }

        private static void CopyKeyContents(RegistryKey source, RegistryKey destination)
        {
            foreach (string valueName in source.GetValueNames())
            {
                RegistryValueKind kind = source.GetValueKind(valueName);
                object? value = source.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value != null) destination.SetValue(valueName, value, kind);
            }
            foreach (string subKeyName in source.GetSubKeyNames())
            {
                using var sourceChild = source.OpenSubKey(subKeyName, writable: false);
                using var destinationChild = destination.CreateSubKey(subKeyName);
                if (sourceChild != null && destinationChild != null) CopyKeyContents(sourceChild, destinationChild);
            }
        }
    }
}
