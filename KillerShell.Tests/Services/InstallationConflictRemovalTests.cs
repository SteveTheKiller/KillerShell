using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class InstallationConflictRemovalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnrelatedInstallationFilesKeepTheImageAndRegistration(bool machine)
    {
        using var fixture = new Fixture(machine);
        string sentinel = Path.Combine(fixture.Directory, "keep.txt");
        File.WriteAllText(sentinel, "unrelated document");
        bool registered = true;

        Assert.Throws<IOException>(() => InstallationConflictRemoval.Remove(machine,
            () => registered = false, _ => registered = true, fixture.Environment));

        Assert.Equal("unrelated document", File.ReadAllText(sentinel));
        Assert.Equal("installed image", File.ReadAllText(fixture.Image));
        Assert.True(registered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulRemovalKeepsUnrelatedStartMenuFiles(bool machine)
    {
        using var fixture = new Fixture(machine);
        string sentinel = Path.Combine(fixture.Menu, "keep.txt");
        File.WriteAllText(sentinel, "unrelated shortcut notes");
        bool registered = true;

        InstallationConflictRemoval.Remove(machine,
            () => registered = false, _ => registered = true, fixture.Environment);

        Assert.True(File.Exists(sentinel), "Unrelated Start Menu files must survive conflict removal.");
        Assert.Equal("unrelated shortcut notes", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(fixture.Menu, "KillerShell.lnk")));
        Assert.False(Directory.Exists(fixture.Directory));
        Assert.False(registered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRegistrationRemovalRestoresTheImageAndRegistration(bool machine)
    {
        using var fixture = new Fixture(machine);
        bool registered = true;

        var failure = Assert.Throws<IOException>(() => InstallationConflictRemoval.Remove(machine,
            () => { registered = false; throw new IOException("registration refused"); },
            _ => registered = true, fixture.Environment));

        Assert.Equal("registration refused", failure.Message);
        Assert.Equal("installed image", File.ReadAllText(fixture.Image));
        Assert.True(registered, "A failed removal must keep truthful registration for retry.");
    }

    [Fact]
    public void LinkedStartMenuCannotRemoveAnUnrelatedShortcut()
    {
        using var fixture = new Fixture(false);
        string unrelated = Path.Combine(fixture.Root, "unrelated");
        Directory.CreateDirectory(unrelated);
        string sentinel = Path.Combine(unrelated, "KillerShell.lnk");
        File.WriteAllText(sentinel, "unrelated shortcut");
        File.Delete(Path.Combine(fixture.Menu, "KillerShell.lnk"));
        Directory.Delete(fixture.Menu, recursive: false);
        Assert.True(CreateSymbolicLink(fixture.Menu, unrelated, 3), "The fixture requires Developer Mode directory links.");
        bool registered = true;
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => InstallationConflictRemoval.Remove(false,
                () => registered = false, _ => registered = true, fixture.Environment));

            Assert.Equal("unrelated shortcut", File.ReadAllText(sentinel));
            Assert.Equal("installed image", File.ReadAllText(fixture.Image));
            Assert.True(registered);
        }
        finally { if (Directory.Exists(fixture.Menu)) Directory.Delete(fixture.Menu, recursive: false); }
    }

    [Theory]
    [InlineData("locked-image")]
    [InlineData("locked-shortcut")]
    [InlineData("cleanup-failure")]
    public void FailedRemovalKeepsAnExecutableAndTruthfulRegistration(string fault)
    {
        using var fixture = new Fixture(false);
        bool registered = true;
        using var held = fault == "cleanup-failure" ? null : new FileStream(
            fault == "locked-image" ? fixture.Image : Path.Combine(fixture.Menu, "KillerShell.lnk"),
            FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fault == "cleanup-failure") fixture.Environment.Cleanup = _ => throw new IOException("cleanup failed");

        Assert.ThrowsAny<IOException>(() => InstallationConflictRemoval.Remove(false,
            () => registered = false, _ => registered = true, fixture.Environment));

        Assert.Equal("installed image", File.ReadAllText(fixture.Image));
        Assert.True(registered);
    }

    [Fact]
    public void FailedRollbackReportsTheCompleteRecoveryImage()
    {
        using var fixture = new Fixture(false);
        bool registered = true;
        string? recovery = null;

        var failure = Assert.Throws<AggregateException>(() => InstallationConflictRemoval.Remove(false, () =>
        {
            registered = false;
            Directory.CreateDirectory(fixture.Directory);
            File.WriteAllText(fixture.Image, "concurrent replacement");
            throw new IOException("registration refused");
        }, path => { registered = true; recovery = path; }, fixture.Environment));

        Assert.Equal("concurrent replacement", File.ReadAllText(fixture.Image));
        Assert.NotNull(recovery);
        Assert.Equal("installed image", File.ReadAllText(recovery!));
        Assert.Contains(recovery!, failure.ToString());
        Assert.True(registered);
    }

    [Fact]
    public void MissingShortcutParentsDoNotPreventRemoval()
    {
        using var fixture = new Fixture(false);
        File.Delete(Path.Combine(fixture.Menu, "KillerShell.lnk"));
        Directory.Delete(fixture.Menu, recursive: false);
        Directory.Delete(Path.GetDirectoryName(fixture.Menu)!, recursive: false);
        bool registered = true;

        InstallationConflictRemoval.Remove(false,
            () => registered = false, _ => registered = true, fixture.Environment);

        Assert.False(Directory.Exists(fixture.Directory));
        Assert.False(registered);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CreateSymbolicLink(string link, string target, int flags);

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "KillerShell-conflict-" + Guid.NewGuid().ToString("N"));
        internal readonly string Directory, Image, Menu;
        internal readonly UninstallRemoval.EnvironmentBoundary Environment;

        internal Fixture(bool machine)
        {
            Environment = new UninstallRemoval.EnvironmentBoundary
            {
                Folder = folder => Path.Combine(Root, folder.ToString()),
                Processes = () => Array.Empty<Process>(),
                Cleanup = File.Delete,
                ReadDirectorySecurity = _ => Trusted<DirectorySecurity>(),
                ReadFileSecurity = _ => Trusted<FileSecurity>(),
                ReadCreatorOwner = () => new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            };
            string parent = machine ? Environment.Folder(System.Environment.SpecialFolder.ProgramFiles)
                : Path.Combine(Environment.Folder(System.Environment.SpecialFolder.LocalApplicationData), "Programs");
            Directory = Path.Combine(parent, "KillerShell");
            Image = Path.Combine(Directory, "KillerShell.exe");
            Menu = Path.Combine(Environment.Folder(machine ? System.Environment.SpecialFolder.CommonPrograms
                : System.Environment.SpecialFolder.Programs), "KillerShell");
            System.IO.Directory.CreateDirectory(Directory);
            System.IO.Directory.CreateDirectory(Menu);
            File.WriteAllText(Image, "installed image");
            File.WriteAllText(Path.Combine(Menu, "KillerShell.lnk"), "owned shortcut");
        }

        private static T Trusted<T>() where T : FileSystemSecurity, new()
        {
            var descriptor = new T();
            descriptor.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
            return descriptor;
        }

        public void Dispose()
        {
            string full = Path.GetFullPath(Root);
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("KillerShell-conflict-", StringComparison.Ordinal)
                || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The conflict fixture escaped its owned temporary root.");
            System.IO.Directory.Delete(full, recursive: true);
        }
    }
}
