using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
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
            foreach (var (name, _) in Hives)
                if (string.Equals(name, hiveName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static RegistryKey? OpenKey(string fullPath, bool writable)
        {
            if (!TrySplitPath(fullPath, out string hiveName, out string subKey)) return null;
            foreach (var (name, root) in Hives)
                if (string.Equals(name, hiveName, StringComparison.OrdinalIgnoreCase))
                    return subKey.Length == 0 ? root : root.OpenSubKey(subKey, writable);
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

        private const AccessControlSections KeySecurity =
            AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;

        /// <summary>
        /// Renames a key by copying its tree under the new name, then deleting the original.
        /// </summary>
        /// <remarks>
        /// Windows' own rename (RegRenameKey, NtRenameKey in the kernel) is never called. On build
        /// 26100 it crashed the whole system with an access violation in
        /// CmpKeySecurityIncrementReferenceCount while renaming a key with protected permissions.
        ///
        /// A copy cannot carry everything a real rename does: handles other programs hold keep
        /// pointing at the deleted original, and class names, last-write times and volatility are
        /// not copied. Symbolic link keys are refused rather than copied through to their target.
        /// Each key's owner must be one this process may assign, checked across the whole tree
        /// before anything is created, so permissions come across exactly or nothing changes.
        /// The original is deleted only after the copy and its permissions are complete.
        /// </remarks>
        internal static RegistryEditResult RenameKey(string parentPath, string oldName, string newName)
            => Write(parentPath, parent =>
            {
                if (parent.GetSubKeyNames().Any(name => string.Equals(name, newName, StringComparison.OrdinalIgnoreCase)))
                    return new RegistryEditResult(RegistryEditStatus.AlreadyExists);

                using var me = WindowsIdentity.GetCurrent();
                var tree = new List<KeyCopy>();
                string? refusal = Survey(parent, oldName, string.Empty, me, tree);
                if (refusal != null) return new RegistryEditResult(RegistryEditStatus.Failed, refusal);

                var created = new List<RegistryKey>();
                try
                {
                    // Top down, so a child is created inside a parent that still has the
                    // permissions it was created with. Security is applied once everything exists.
                    foreach (var item in tree)
                    {
                        string target = item.RelativePath.Length == 0 ? newName : newName + "\\" + item.RelativePath;
                        parent.CreateSubKey(target)?.Dispose();
                        var copy = parent.OpenSubKey(target, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.FullControl)
                                   ?? throw new UnauthorizedAccessException("Could not open the new key '" + target + "'.");
                        created.Add(copy);
                        using var original = parent.OpenSubKey(item.SourcePath, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadKey)
                                             ?? throw new System.IO.IOException("'" + item.SourcePath + "' disappeared during the rename.");
                        foreach (string value in original.GetValueNames())
                            if (!CopyValueRaw(original, value, copy, value))
                                throw new System.IO.IOException("Could not copy the value '" + value + "' of '" + item.SourcePath + "'.");
                    }
                    for (int i = 0; i < tree.Count; i++)
                    {
                        var security = new RegistrySecurity();
                        security.SetSecurityDescriptorSddlForm(tree[i].Sddl, KeySecurity);
                        created[i].SetAccessControl(security);
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
                                                 or System.IO.IOException or ArgumentException or InvalidOperationException)
                {
                    foreach (var key in created) key.Dispose();
                    string cleanup = string.Empty;
                    try { parent.DeleteSubKeyTree(newName, throwOnMissingSubKey: false); }
                    catch (Exception cleanupError) { cleanup = " The partial copy '" + newName + "' could not be removed: " + cleanupError.Message; }
                    return new RegistryEditResult(ex is UnauthorizedAccessException or System.Security.SecurityException
                        ? RegistryEditStatus.AccessDenied : RegistryEditStatus.Failed,
                        "Nothing was renamed. " + ex.Message + cleanup);
                }
                foreach (var key in created) key.Dispose();

                try { parent.DeleteSubKeyTree(oldName); }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException
                                                 or System.IO.IOException or ArgumentException)
                {
                    return new RegistryEditResult(RegistryEditStatus.Failed,
                        "Copied to '" + newName + "' but could not remove '" + oldName + "': " + ex.Message);
                }
                return new RegistryEditResult(RegistryEditStatus.Success);
            });

        private sealed class KeyCopy
        {
            internal string SourcePath = string.Empty;
            internal string RelativePath = string.Empty;
            internal string Sddl = string.Empty;
        }

        /// <summary>
        /// Reads the tree under <paramref name="name"/> parents first, changing nothing. Returns why
        /// it cannot be copied faithfully, or null when it can.
        /// </summary>
        private static string? Survey(RegistryKey parent, string name, string relative, WindowsIdentity me, List<KeyCopy> tree)
        {
            string sourcePath = relative.Length == 0 ? name : name + "\\" + relative;
            string displayPath = relative.Length == 0 ? name : relative;
            if (IsLinkKey(parent, sourcePath))
                return "'" + displayPath + "' is a link to another key and cannot be renamed by copying.";

            using var key = parent.OpenSubKey(sourcePath, RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadKey);
            if (key == null) return "'" + displayPath + "' could not be read.";
            RegistrySecurity security = key.GetAccessControl(KeySecurity);
            if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner
                && !owner.Equals(me.User) && !(me.Groups?.Contains(owner) ?? false))
                return "'" + displayPath + "' is owned by " + owner.Value + ", which this session cannot assign to a copy.";

            tree.Add(new KeyCopy { SourcePath = sourcePath, RelativePath = relative, Sddl = security.GetSecurityDescriptorSddlForm(KeySecurity) });
            foreach (string child in key.GetSubKeyNames())
            {
                string? refusal = Survey(parent, name, relative.Length == 0 ? child : relative + "\\" + child, me, tree);
                if (refusal != null) return refusal;
            }
            return null;
        }

        /// <summary>A symbolic link key, opened as the link itself, carries its target in a REG_LINK value.</summary>
        private static bool IsLinkKey(RegistryKey parent, string subKey)
        {
            const int OpenLink = 0x8, QueryValue = 0x1;
            const uint RegLink = 6;
            if (RegOpenKeyEx(parent.Handle, subKey, OpenLink, QueryValue, out var link) != 0) return false;
            using (link)
            {
                int size = 0;
                return RegQueryValueEx(link, "SymbolicLinkValue", IntPtr.Zero, out uint type, null, ref size) == 0
                       && type == RegLink;
            }
        }

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
                if (!CopyValueRaw(key, oldName, key, newName)) return new RegistryEditResult(RegistryEditStatus.Failed);
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

        /// <summary>
        /// Copies one value as its exact type and bytes. RegistryKey only understands the common
        /// types; anything else (REG_NONE, the hardware resource lists, REG_LINK) reads back as
        /// Unknown and cannot be written again, so a rename would fail or change the type.
        /// </summary>
        private static bool CopyValueRaw(RegistryKey source, string sourceName, RegistryKey destination, string destinationName)
        {
            int size = 0;
            int rc = RegQueryValueEx(source.Handle, sourceName, IntPtr.Zero, out uint type, null, ref size);
            if (rc != 0) return false;
            var data = new byte[size];
            rc = RegQueryValueEx(source.Handle, sourceName, IntPtr.Zero, out type, data, ref size);
            if (rc != 0) return false;
            return RegSetValueEx(destination.Handle, destinationName, 0, type, data, size) == 0;
        }

        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegQueryValueEx(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string name,
                                                  IntPtr reserved, out uint type, byte[]? data, ref int size);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegSetValueEx(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string name,
                                                int reserved, uint type, byte[] data, int size);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegOpenKeyEx(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string subKey,
                                               int options, int desired, out Microsoft.Win32.SafeHandles.SafeRegistryHandle result);
    }
}
