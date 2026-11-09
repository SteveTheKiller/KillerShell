using System;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Xunit;

namespace KillerShell.Tests.Services;

// Uninstall used to leave empty class keys behind (OpenWithProgids under .err and the other
// types KillerShell alone hints, and the shell key under batfile, cmdfile, Directory,
// Directory\Background and Drive). The removal now deletes a key it created once it is empty,
// and never one that still holds another program's value or subkey.
public sealed class AssociationCleanupTests
{
    private const string Classes = @"Software\Classes\";

    private static RegistryKey NewRoot(out string path)
    {
        path = @"Software\KillerShell-Tests\Assoc-" + Guid.NewGuid().ToString("N");
        return Registry.CurrentUser.CreateSubKey(path);
    }

    private static bool Exists(RegistryKey root, string sub)
    {
        using var key = root.OpenSubKey(sub);
        return key != null;
    }

    // Only the copy the file associations point at keeps text-file.ico fresh. A portable copy
    // started from a USB stick, with an installed copy owning the associations, writes nothing
    // beside itself.
    [Theory]
    [InlineData("\"C:\\Program Files\\KillerShell\\KillerShell.exe\" \"%1\"", @"C:\Program Files\KillerShell\KillerShell.exe", true)]
    [InlineData("\"c:\\program files\\killershell\\KILLERSHELL.EXE\" \"%1\"", @"C:\Program Files\KillerShell\KillerShell.exe", true)]
    [InlineData("\"C:\\Program Files\\KillerShell\\KillerShell.exe\" \"%1\"", @"E:\Tools\KillerShell.exe", false)]
    [InlineData(null, @"E:\Tools\KillerShell.exe", false)]
    [InlineData("", @"E:\Tools\KillerShell.exe", false)]
    public void OnlyTheRegisteredCopyRefreshesTheFileIcon(string? registeredCommand, string exe, bool expected)
        => Assert.Equal(expected, App.ShouldRefreshFileIcon(registeredCommand, exe));

    [Fact]
    public void RemovalLeavesNoEmptyKeysWhereNothingWasThereBefore()
    {
        using var root = NewRoot(out string path);
        try
        {
            App.WriteAssociationKeys(root, @"C:\Test\KillerShell.exe");
            Assert.True(Exists(root, Classes + @".err\OpenWithProgids"));
            Assert.True(App.RemoveAssociationKeys(root));

            foreach (string ext in App.TextExtensions)
            {
                Assert.False(Exists(root, Classes + ext + @"\OpenWithProgids"), ext + " OpenWithProgids");
                Assert.False(Exists(root, Classes + ext), ext);
            }
            foreach (var (_, progId) in App.EditOnlyExtensions)
                Assert.False(Exists(root, Classes + progId), progId);
            foreach (string owner in new[] { "Directory", "Drive", @"Directory\Background" })
                Assert.False(Exists(root, Classes + owner), owner);
            Assert.False(Exists(root, @"Software\KillerShell"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }

    [Fact]
    public void RemovalKeepsEveryKeyAnotherProgramStillUses()
    {
        using var root = NewRoot(out string path);
        try
        {
            using (var other = root.CreateSubKey(Classes + @".txt\OpenWithProgids"))
                other.SetValue("Other.Document", Array.Empty<byte>(), RegistryValueKind.None);
            using (var other = root.CreateSubKey(Classes + @".yml"))
                other.SetValue("", "ymlfile");
            using (var other = root.CreateSubKey(Classes + @"batfile\shell\open\command"))
                other.SetValue("", "cmd");
            using (var other = root.CreateSubKey(Classes + @"Directory\shell\Foreign\command"))
                other.SetValue("", "foreign");
            using (var other = root.CreateSubKey(Classes + @"Directory\Background\shell\Foreign"))
                other.SetValue("", "foreign");
            using (var other = root.CreateSubKey(Classes + @"Drive\Foreign"))
                other.SetValue("", "foreign");

            App.WriteAssociationKeys(root, @"C:\Test\KillerShell.exe");
            Assert.True(App.RemoveAssociationKeys(root));

            using (var txt = root.OpenSubKey(Classes + @".txt\OpenWithProgids"))
            {
                Assert.NotNull(txt);
                Assert.Equal(new[] { "Other.Document" }, txt!.GetValueNames());
            }
            using (var yml = root.OpenSubKey(Classes + ".yml"))
                Assert.Equal("ymlfile", yml!.GetValue(""));
            Assert.False(Exists(root, Classes + @".yml\OpenWithProgids"));
            Assert.True(Exists(root, Classes + @"batfile\shell\open\command"));
            Assert.False(Exists(root, Classes + @"batfile\shell\KillerShellEdit"));
            Assert.True(Exists(root, Classes + @"Directory\shell\Foreign\command"));
            Assert.False(Exists(root, Classes + @"Directory\shell\KillerShell"));
            Assert.True(Exists(root, Classes + @"Directory\Background\shell\Foreign"));
            Assert.True(Exists(root, Classes + @"Drive\Foreign"));
            Assert.False(Exists(root, Classes + @"Drive\shell"));
            Assert.True(Exists(root, Classes + "Drive"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }

    [Fact]
    public void AnEmptyKeyThatCannotBeDeletedStillCountsAsRemoved()
    {
        using var root = NewRoot(out string path);
        string locked = Classes + @".err\OpenWithProgids";
        var me = WindowsIdentity.GetCurrent().User!;
        var deny = new RegistryAccessRule(me, RegistryRights.Delete, AccessControlType.Deny);
        try
        {
            App.WriteAssociationKeys(root, @"C:\Test\KillerShell.exe");
            using (var k = root.OpenSubKey(locked, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadKey))
            {
                var sec = k!.GetAccessControl();
                sec.AddAccessRule(deny);
                k.SetAccessControl(sec);
            }

            Assert.True(App.RemoveAssociationKeys(root));

            using (var k = root.OpenSubKey(locked))
            {
                Assert.NotNull(k);
                Assert.Null(k!.GetValue(App.AssocProgId));
            }
            Assert.False(Exists(root, Classes + @"Directory\shell\KillerShell"));
        }
        finally
        {
            using (var k = root.OpenSubKey(locked, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadKey))
            {
                if (k != null)
                {
                    var sec = k.GetAccessControl();
                    sec.RemoveAccessRuleAll(deny);
                    k.SetAccessControl(sec);
                }
            }
            Registry.CurrentUser.DeleteSubKeyTree(path, false);
        }
    }
}
