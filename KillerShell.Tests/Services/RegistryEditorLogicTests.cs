using System;
using System.Security.AccessControl;
using System.Security.Principal;
using KillerShell.Services;
using Microsoft.Win32;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class RegistryEditorLogicTests
    {
        [Fact]
        public void RenameKey_PreservesProtectedPermissionsAndOpenHandles()
        {
            string subkey = "Software\\KillerShell.Tests." + Guid.NewGuid().ToString("N");
            try
            {
                using var parent = Registry.CurrentUser.CreateSubKey(subkey);
                using var source = parent.CreateSubKey("Original");
                using var child = source.CreateSubKey("Child");
                var security = new RegistrySecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new RegistryAccessRule(WindowsIdentity.GetCurrent().User!,
                    RegistryRights.FullControl, InheritanceFlags.ContainerInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                source.SetAccessControl(security);
                child.SetAccessControl(security);
                source.SetValue("Value", "original");
                child.SetValue("Value", "child");
                const AccessControlSections sections = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;
                string sourceSecurity = source.GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections);
                string childSecurity = child.GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections);

                RegistryEditResult result = RegistryEditorLogic.RenameKey("HKEY_CURRENT_USER\\" + subkey, "Original", "Renamed");

                Assert.True(result.Succeeded, result.Error);
                Assert.Null(parent.OpenSubKey("Original"));
                using var renamed = parent.OpenSubKey("Renamed");
                using var renamedChild = renamed!.OpenSubKey("Child");
                Assert.Equal(sourceSecurity, renamed.GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections));
                Assert.Equal(childSecurity, renamedChild!.GetAccessControl(sections).GetSecurityDescriptorSddlForm(sections));
                Assert.Equal("child", renamedChild.GetValue("Value"));
                source.SetValue("Value", "through existing handle");
                Assert.Equal("through existing handle", renamed.GetValue("Value"));
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(subkey, false); }
        }

        [Theory]
        [InlineData("", 1)]
        [InlineData("   ", 1)]
        [InlineData("Software\\KillerShell", 2)]
        [InlineData("KillerShell", 0)]
        public void ValidateName_ReturnsExpectedResult(string value, int expected)
            => Assert.Equal(expected, (int)RegistryEditorLogic.ValidateName(value));

        [Fact]
        public void TrySplitPath_SeparatesHiveAndSubkey()
        {
            Assert.True(RegistryEditorLogic.TrySplitPath(
                "HKEY_CURRENT_USER\\Software\\KillerShell", out string hive, out string subkey));
            Assert.Equal("HKEY_CURRENT_USER", hive);
            Assert.Equal("Software\\KillerShell", subkey);
            Assert.Equal("HKEY_CURRENT_USER\\Software",
                RegistryEditorLogic.ParentPath("HKEY_CURRENT_USER\\Software\\KillerShell"));
        }

        [Theory]
        [InlineData(RegistryValueKind.String, "text", "text")]
        [InlineData(RegistryValueKind.DWord, 42, "0x0000002a (42)")]
        [InlineData(RegistryValueKind.QWord, 42L, "0x000000000000002a (42)")]
        public void DataLabel_FormatsCommonValueKinds(RegistryValueKind kind, object value, string expected)
            => Assert.Equal(expected, RegistryEditorLogic.DataLabel(value, kind));

        [Fact]
        public void DataLabel_FormatsBinaryAndMultiStringValues()
        {
            Assert.Equal("00 0f ff", RegistryEditorLogic.DataLabel(
                new byte[] { 0x00, 0x0f, 0xff }, RegistryValueKind.Binary));
            Assert.Equal("one  |  two", RegistryEditorLogic.DataLabel(
                new[] { "one", "two" }, RegistryValueKind.MultiString));
        }

        [Fact]
        public void RegistryEdits_RoundTripInDisposableCurrentUserKey()
        {
            string rootName = "KillerShell.Tests." + Guid.NewGuid().ToString("N");
            string parentPath = "HKEY_CURRENT_USER\\Software";
            string rootPath = parentPath + "\\" + rootName;
            try
            {
                Assert.True(RegistryEditorLogic.CreateKey(parentPath, rootName).Succeeded);
                Assert.True(RegistryEditorLogic.CreateValue(
                    rootPath, "Name", "first", RegistryValueKind.String).Succeeded);
                Assert.True(RegistryEditorLogic.SetValue(
                    rootPath, "Name", "second", RegistryValueKind.String).Succeeded);
                Assert.True(RegistryEditorLogic.RenameValue(rootPath, "Name", "Renamed").Succeeded);

                using var key = RegistryEditorLogic.OpenKey(rootPath, writable: false);
                Assert.NotNull(key);
                Assert.Equal("second", key!.GetValue("Renamed"));

                Assert.True(RegistryEditorLogic.DeleteValue(rootPath, "Renamed").Succeeded);
            }
            finally
            {
                RegistryEditorLogic.DeleteKey(parentPath, rootName);
            }
        }

        [Fact]
        public void Renames_KeepUncommonValueTypes()
        {
            const uint RegResourceList = 8;
            string rootName = "KillerShell.Tests." + Guid.NewGuid().ToString("N");
            string parentPath = "HKEY_CURRENT_USER\\Software";
            string rootPath = parentPath + "\\" + rootName;
            byte[] data = { 1, 2, 3, 4 };
            try
            {
                Assert.True(RegistryEditorLogic.CreateKey(parentPath, rootName).Succeeded);
                Assert.True(RegistryEditorLogic.CreateKey(rootPath, "Child").Succeeded);
                using (var key = RegistryEditorLogic.OpenKey(rootPath, writable: true))
                    Assert.Equal(0, RegSetValueEx(key!.Handle, "Res", 0, RegResourceList, data, data.Length));
                using (var key = RegistryEditorLogic.OpenKey(rootPath + "\\Child", writable: true))
                    Assert.Equal(0, RegSetValueEx(key!.Handle, "Res", 0, RegResourceList, data, data.Length));

                Assert.True(RegistryEditorLogic.RenameValue(rootPath, "Res", "Renamed").Succeeded);
                Assert.True(RegistryEditorLogic.RenameKey(rootPath, "Child", "Moved").Succeeded);

                Assert.Equal(RegResourceList, RawType(rootPath, "Renamed"));
                Assert.Equal(RegResourceList, RawType(rootPath + "\\Moved", "Res"));
            }
            finally
            {
                RegistryEditorLogic.DeleteKey(parentPath, rootName);
            }
        }

        private static uint RawType(string path, string name)
        {
            using var key = RegistryEditorLogic.OpenKey(path, writable: false);
            int size = 0;
            Assert.Equal(0, RegQueryValueEx(key!.Handle, name, IntPtr.Zero, out uint type, null, ref size));
            return type;
        }

        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegSetValueEx(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string name,
                                                int reserved, uint type, byte[] data, int size);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegQueryValueEx(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, string name,
                                                  IntPtr reserved, out uint type, byte[]? data, ref int size);
    }
}
