using System;
using KillerShell.Services;
using Microsoft.Win32;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class UninstallMetadataTests
{
    [Fact]
    public void LateFailureCanRestoreBothRecoveryTreesWithoutExpandingValues()
    {
        string root = @"Software\KillerShell-Tests\Uninstall-" + Guid.NewGuid().ToString("N");
        try
        {
            using var fixture = Registry.CurrentUser.CreateSubKey(root);
            using (var install = fixture.CreateSubKey("Install"))
            {
                install.SetValue("Installed", 1, RegistryValueKind.DWord);
                install.SetValue("Path", @"%TEMP%\KillerShell.exe", RegistryValueKind.ExpandString);
                using var child = install.CreateSubKey("Settings");
                child.SetValue("Bytes", new byte[] { 1, 4, 9 }, RegistryValueKind.Binary);
            }
            using (var uninstall = fixture.CreateSubKey("Uninstall"))
                uninstall.SetValue("QuietUninstallString", "retained-image /uninstall-silent");
            var checkpoint = new UninstallMetadata(fixture, "Install", "Uninstall", "Missing");
            fixture.DeleteSubKeyTree("Install");
            fixture.DeleteSubKeyTree("Uninstall");
            checkpoint.Restore();
            using var restored = fixture.OpenSubKey("Install");
            Assert.Equal(1, restored!.GetValue("Installed"));
            Assert.Equal(RegistryValueKind.ExpandString, restored.GetValueKind("Path"));
            Assert.Equal(@"%TEMP%\KillerShell.exe", restored.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            using var settings = restored.OpenSubKey("Settings");
            Assert.Equal(new byte[] { 1, 4, 9 }, (byte[])settings!.GetValue("Bytes")!);
            using var entry = fixture.OpenSubKey("Uninstall");
            Assert.Equal("retained-image /uninstall-silent", entry!.GetValue("QuietUninstallString"));
            using var missing = fixture.OpenSubKey("Missing");
            Assert.Null(missing);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, false); }
    }
}
