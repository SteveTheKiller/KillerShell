using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class UninstallTrustTests
{
    private const string Trusted = "O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)";

    [Theory]
    [InlineData("S-1-5-32-544", false)]
    [InlineData("S-1-5-18", false)]
    [InlineData("S-1-5-21-100-200-300-1001", true)]
    [InlineData(null, true)]
    public void MachineUninstallRequiresTrustedRecoveryOwner(string? owner, bool refused)
        => CheckRemoval("parent", Trusted + "(A;OICIIO;GA;;;CO)", refused, owner);

    [Theory]
    [InlineData("(A;OICIIO;GA;;;CO)", false)]
    [InlineData("(A;OIIO;GA;;;BU)", true)]
    public void PreElevationImageCheckDefersCreatingOwnerButChecksPermissions(string ace, bool refused)
        => CheckRemoval("parent", Trusted + ace, refused, preflight: true);

    [Theory]
    [InlineData("image", "O:BAG:BA")]
    [InlineData("icon", "O:BAG:BAD:NO_ACCESS_CONTROL")]
    [InlineData("installation", "O:BAG:BA")]
    [InlineData("parent", "O:BAG:BAD:NO_ACCESS_CONTROL")]
    [InlineData("ancestor", "O:BAG:BA")]
    public void MachineUninstallRefusesNullDacl(string location, string sddl)
        => CheckRemoval(location, sddl, refused: true);

    [Theory]
    [InlineData("image")]
    [InlineData("installation")]
    [InlineData("parent")]
    public void MachineUninstallDistinguishesEmptyDaclFromNull(string location)
        => CheckRemoval(location, "O:BAG:BAD:P", refused: false);

    [Theory]
    [InlineData("installation", "(A;;GA;;;BU)", true)]
    [InlineData("installation", "(A;;GW;;;BU)", true)]
    [InlineData("parent", "(A;;GA;;;BU)", true)]
    [InlineData("parent", "(A;;GW;;;BU)", true)]
    [InlineData("parent", "(A;;FW;;;BU)", true)]
    [InlineData("ancestor", "(A;;GA;;;BU)", true)]
    [InlineData("ancestor", "(A;;GW;;;BU)", false)]
    [InlineData("ancestor", "(A;;FW;;;BU)", false)]
    [InlineData("ancestor", "(A;;0x00000004;;;BU)(A;OICIIO;GA;;;CO)", false)]
    [InlineData("parent", "(A;CIIO;FW;;;BU)", true)]
    [InlineData("parent", "(A;CINPIO;GW;;;BU)", true)]
    [InlineData("parent", "(A;OIIO;GA;;;BU)", true)]
    [InlineData("parent", "(A;OINPIO;GA;;;BU)", false)]
    [InlineData("parent", "(A;OICIIO;GA;;;CO)", false)]
    [InlineData("parent", "(A;CIIO;GR;;;BU)", false)]
    [InlineData("parent", "(A;CIIO;WD;;;BU)", true)]
    [InlineData("parent", "(A;CIIO;WO;;;BU)", true)]
    [InlineData("parent", "(A;CIIO;0x00000040;;;BU)", true)]
    [InlineData("parent", "(A;OIIO;0x00000040;;;BU)", false)]
    [InlineData("installation", "(A;OICIIO;GA;;;BU)", false)]
    [InlineData("ancestor", "(A;OICIIO;GA;;;BU)", false)]
    [InlineData("image", "(A;IO;GA;;;BU)", false)]
    [InlineData("icon", "(A;;GW;;;BU)", true)]
    public void MachineUninstallChecksEffectiveAndRecoveryPermissions(string location, string ace, bool refused)
        => CheckRemoval(location, Trusted + ace, refused);

    [Theory]
    [InlineData("GA")]
    [InlineData("GW")]
    public void MachineUninstallRefusesUntrustedGenericImageWrites(string rights)
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell-trust-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "KillerShell");
        string image = Path.Combine(directory, "KillerShell.exe");
        Directory.CreateDirectory(directory);
        File.WriteAllText(image, "preserve original image");
        var environment = new UninstallRemoval.EnvironmentBoundary
        {
            Folder = _ => root,
            Processes = () => Array.Empty<Process>(),
            ReadDirectorySecurity = _ => Descriptor<DirectorySecurity>(Trusted),
            ReadFileSecurity = _ => Descriptor<FileSecurity>(Trusted + "(A;;" + rights + ";;;BU)"),
            ReadCreatorOwner = () => new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
            {
                using var removal = UninstallRemoval.Begin(true, environment);
            });
            Assert.Equal("preserve original image", File.ReadAllText(image));
            Assert.Empty(Directory.GetFiles(root, ".KillerShell-uninstall-*"));
        }
        finally { DeleteOwnedRoot(root); }
    }

    private static T Descriptor<T>(string sddl) where T : FileSystemSecurity, new()
    {
        var raw = new RawSecurityDescriptor(sddl);
        var bytes = new byte[raw.BinaryLength];
        raw.GetBinaryForm(bytes, 0);
        var security = new T();
        security.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.All);
        return security;
    }

    private static void CheckRemoval(string location, string sddl, bool refused,
        string? owner = "S-1-5-32-544", bool preflight = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell-trust-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(root, "KillerShell");
        string image = Path.Combine(directory, "KillerShell.exe");
        string icon = Path.Combine(directory, "text-file.ico");
        string target = location switch
        {
            "image" => image,
            "icon" => icon,
            "installation" => directory,
            "parent" => root,
            "ancestor" => Path.GetDirectoryName(root)!,
            _ => throw new ArgumentException("Unknown ACL location.", nameof(location)),
        };
        Directory.CreateDirectory(directory);
        File.WriteAllText(image, "preserve original image");
        File.WriteAllText(icon, "preserve original icon");
        string Select(string path) => string.Equals(path, target, StringComparison.OrdinalIgnoreCase) ? sddl : Trusted;
        var environment = new UninstallRemoval.EnvironmentBoundary
        {
            Folder = _ => root,
            Processes = () => Array.Empty<Process>(),
            ReadDirectorySecurity = path => Descriptor<DirectorySecurity>(Select(path)),
            ReadFileSecurity = path => Descriptor<FileSecurity>(Select(path)),
            ReadCreatorOwner = () => preflight
                ? throw new InvalidOperationException("Pre-elevation validation cannot decide the elevated creating owner.")
                : owner == null ? null : new SecurityIdentifier(owner),
            Cleanup = _ => throw new IOException("Controlled cleanup launch failure."),
        };
        try
        {
            if (preflight)
            {
                if (refused)
                    Assert.Throws<UnauthorizedAccessException>(() => UninstallRemoval.ValidateMachineImage(image, environment));
                else
                    UninstallRemoval.ValidateMachineImage(image, environment);
            }
            else if (refused)
                Assert.Throws<UnauthorizedAccessException>(() =>
                {
                    using var removal = UninstallRemoval.Begin(true, environment);
                });
            else
            {
                using var removal = UninstallRemoval.Begin(true, environment);
                Assert.False(Directory.Exists(directory));
                Assert.Throws<IOException>(() => removal.Commit());
            }
            Assert.Equal("preserve original image", File.ReadAllText(image));
            Assert.Equal("preserve original icon", File.ReadAllText(icon));
            Assert.Empty(Directory.GetFiles(root, ".KillerShell-uninstall-*"));
        }
        finally { DeleteOwnedRoot(root); }
    }

    private static void DeleteOwnedRoot(string root)
    {
        string full = Path.GetFullPath(root);
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        const string prefix = "KillerShell-trust-";
        string name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase)
            || !name.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name.Substring(prefix.Length), "N", out _)
            || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The trust fixture escaped its owned temporary root.");
        Directory.Delete(full, true);
    }
}
