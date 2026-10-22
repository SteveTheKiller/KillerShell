using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class PublisherPolicyTests
{
    [Fact]
    public void ACurrentImageWithAnInvalidSignatureCannotAcceptAnUnsignedReplacement()
    {
        using var fixture = new Fixture();
        string signed = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        Assert.True(File.Exists(signed), "The guest prerequisite is the signed dotnet host.");
        byte[] bytes = File.ReadAllBytes(signed);
        bytes[bytes.Length / 2] ^= 0xFF;
        string tampered = Path.Combine(fixture.Root, "tampered.exe");
        File.WriteAllBytes(tampered, bytes);
        using var certificate = X509Certificate.CreateFromSignedFile(tampered);
        string unsigned = typeof(CodeSignature).Assembly.Location;

        Assert.False(CodeSignature.MayReplace(tampered, unsigned));
        Assert.True(CodeSignature.MayReplace(unsigned, unsigned));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUninspectableCurrentImageCannotDisablePublisherValidation(bool missing)
    {
        using var fixture = new Fixture();
        string current = Path.Combine(fixture.Root, "current.exe");
        if (!missing) File.Copy(typeof(CodeSignature).Assembly.Location, current);
        using var held = missing ? null : new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.False(CodeSignature.MayReplace(current, typeof(CodeSignature).Assembly.Location));
    }

    [Fact]
    public void AMalformedImageCannotClaimTheUnsignedDevelopmentException()
    {
        using var fixture = new Fixture();
        Assert.False(CodeSignature.MayReplace(fixture.Image, typeof(CodeSignature).Assembly.Location));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ks-publisher-policy-" + Guid.NewGuid().ToString("N"));
        internal string Image => Path.Combine(Root, "malformed.exe");
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Image, "malformed image");
        }
        public void Dispose()
        {
            string boundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.Delete(Root, true);
        }
    }
}
