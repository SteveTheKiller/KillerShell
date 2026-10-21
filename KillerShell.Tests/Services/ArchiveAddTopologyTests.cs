using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class ArchiveAddTopologyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ks-archive-topology-" + Guid.NewGuid().ToString("N"));

    public ArchiveAddTopologyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ReportRefusesAFileThatWouldReplaceAnImplicitDirectory()
    {
        string archive = CreateArchive("docs/a.txt");
        byte[] original = File.ReadAllBytes(archive);
        string incoming = Path.Combine(_root, "docs");
        File.WriteAllText(incoming, "incoming file");

        var result = ArchiveWriter.Add(archive, "", new[] { incoming }, ArchiveCollision.Report,
            null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("docs", Assert.Single(result.Collisions));
        Assert.Equal(original, File.ReadAllBytes(archive));
        AssertExtracted(archive, "docs/a.txt", "original docs/a.txt");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ResolvingAFileAgainstAnImplicitDirectoryKeepsTheArchiveExtractable(int policy)
    {
        string archive = CreateArchive("docs/a.txt", "unrelated.txt");
        string incoming = Path.Combine(_root, "docs");
        File.WriteAllText(incoming, "incoming file");

        var result = ArchiveWriter.Add(archive, "", new[] { incoming }, (ArchiveCollision)policy,
            null, CancellationToken.None);

        Assert.True(result.Ok, result.ErrorDetail ?? result.ErrorKey);
        string expected = policy == (int)ArchiveCollision.Replace ? "docs" : "docs (2)";
        AssertExtracted(archive, expected, "incoming file");
        AssertExtracted(archive, "unrelated.txt", "original unrelated.txt");
        using var zip = ZipFile.OpenRead(archive);
        Assert.Equal(policy == (int)ArchiveCollision.KeepBoth, zip.GetEntry("docs/a.txt") != null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void IncomingDirectoryResolvesAnExistingAncestorFileAsOneDirectory(int policy)
    {
        string archive = CreateArchive("docs", "unrelated.txt");
        byte[] original = File.ReadAllBytes(archive);
        string incoming = Directory.CreateDirectory(Path.Combine(_root, "docs")).FullName;
        File.WriteAllText(Path.Combine(incoming, "a.txt"), "incoming a");
        File.WriteAllText(Path.Combine(incoming, "b.txt"), "incoming b");

        var result = ArchiveWriter.Add(archive, "", new[] { incoming }, (ArchiveCollision)policy,
            null, CancellationToken.None);

        if (policy == (int)ArchiveCollision.Report)
        {
            Assert.False(result.Ok);
            Assert.NotEmpty(result.Collisions);
            Assert.Equal(original, File.ReadAllBytes(archive));
            return;
        }
        Assert.True(result.Ok, result.ErrorDetail ?? result.ErrorKey);
        string directory = policy == (int)ArchiveCollision.Replace ? "docs" : "docs (2)";
        AssertExtracted(archive, directory + "/a.txt", "incoming a");
        AssertExtracted(archive, directory + "/b.txt", "incoming b");
        AssertExtracted(archive, "unrelated.txt", "original unrelated.txt");
        if (policy == (int)ArchiveCollision.KeepBoth) AssertExtracted(archive, "docs", "original docs");
    }

    [Theory]
    [InlineData("docs.", 0)]
    [InlineData("docs ", 0)]
    [InlineData("docs.", 1)]
    [InlineData("docs ", 1)]
    [InlineData("docs.", 2)]
    [InlineData("docs ", 2)]
    public void WindowsOutputAliasesCannotBeAddedAsSeparatePaths(string existing, int policy)
    {
        string archive = CreateArchive(existing + "/old.txt");
        byte[] original = File.ReadAllBytes(archive);
        string incoming = Path.Combine(_root, "docs");
        File.WriteAllText(incoming, "incoming file");

        var result = ArchiveWriter.Add(archive, "", new[] { incoming }, (ArchiveCollision)policy,
            null, CancellationToken.None);

        if (policy != (int)ArchiveCollision.KeepBoth)
        {
            Assert.False(result.Ok);
            Assert.NotEmpty(result.Collisions);
            Assert.Equal(original, File.ReadAllBytes(archive));
            return;
        }
        Assert.True(result.Ok, result.ErrorDetail ?? result.ErrorKey);
        AssertExtracted(archive, "docs (2)", "incoming file");
        AssertExtracted(archive, "docs/old.txt", "original " + existing + "/old.txt");
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void IncomingSourcesUseTheSameFileAndDirectoryConflictPolicy(int policy, bool directoryFirst)
    {
        string archive = CreateArchive("unrelated.txt");
        byte[] original = File.ReadAllBytes(archive);
        string file = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "files")).FullName, "docs");
        File.WriteAllText(file, "incoming file");
        string directory = Directory.CreateDirectory(Path.Combine(_root, "folders", "docs")).FullName;
        File.WriteAllText(Path.Combine(directory, "child.txt"), "incoming child");
        string[] sources = directoryFirst ? new[] { directory, file } : new[] { file, directory };

        var result = ArchiveWriter.Add(archive, "", sources, (ArchiveCollision)policy, null, CancellationToken.None);

        if (policy == (int)ArchiveCollision.Report)
        {
            Assert.False(result.Ok);
            Assert.NotEmpty(result.Collisions);
            Assert.Equal(original, File.ReadAllBytes(archive));
            return;
        }
        Assert.True(result.Ok, result.ErrorDetail ?? result.ErrorKey);
        AssertExtracted(archive, "unrelated.txt", "original unrelated.txt");
        if (policy == (int)ArchiveCollision.Replace)
            AssertExtracted(archive, directoryFirst ? "docs" : "docs/child.txt",
                directoryFirst ? "incoming file" : "incoming child");
        else
        {
            AssertExtracted(archive, directoryFirst ? "docs (2)" : "docs", "incoming file");
            AssertExtracted(archive, directoryFirst ? "docs/child.txt" : "docs (2)/child.txt", "incoming child");
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void ExplicitEmptyDirectoriesConflictWithFilesInEitherDirection(int policy, bool incomingDirectory)
    {
        string archive = CreateArchive(incomingDirectory ? "docs" : "docs/");
        byte[] original = File.ReadAllBytes(archive);
        string incoming = Path.Combine(_root, "docs");
        if (incomingDirectory) Directory.CreateDirectory(incoming);
        else File.WriteAllText(incoming, "incoming file");

        var result = ArchiveWriter.Add(archive, "", new[] { incoming }, (ArchiveCollision)policy, null, CancellationToken.None);

        if (policy == (int)ArchiveCollision.Report)
        {
            Assert.False(result.Ok);
            Assert.NotEmpty(result.Collisions);
            Assert.Equal(original, File.ReadAllBytes(archive));
            return;
        }
        Assert.True(result.Ok, result.ErrorDetail ?? result.ErrorKey);
        string output = Path.Combine(_root, "extracted");
        Assert.True(ArchiveProvider.ExtractAll(archive, output, CancellationToken.None, out string? error), error);
        string added = Path.Combine(output, policy == (int)ArchiveCollision.KeepBoth ? "docs (2)" : "docs");
        if (incomingDirectory) Assert.True(Directory.Exists(added));
        else Assert.Equal("incoming file", File.ReadAllText(added));
    }

    [Theory]
    [InlineData("../payload.txt")]
    [InlineData("/payload.txt")]
    [InlineData("docs//payload.txt")]
    [InlineData("docs/./payload.txt")]
    [InlineData("C:/outside.txt")]
    [InlineData("CON.txt")]
    [InlineData("docs/NUL")]
    [InlineData("docs/COM1.log")]
    [InlineData("docs/LPT9")]
    public void UnsupportedExistingPathsAreRefusedWithoutRewritingTheArchive(string storedName)
    {
        string archive = CreateArchive(storedName);
        byte[] original = File.ReadAllBytes(archive);
        string incoming = Path.Combine(_root, "new.txt");
        File.WriteAllText(incoming, "incoming");

        var result = ArchiveWriter.Add(archive, "", new[] { incoming }, ArchiveCollision.KeepBoth,
            null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Collisions);
        Assert.Equal(original, File.ReadAllBytes(archive));
    }

    private string CreateArchive(params string[] names)
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (string name in names)
        {
            var entry = zip.CreateEntry(name);
            if (!name.EndsWith("/", StringComparison.Ordinal))
            {
                using var output = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                output.Write("original " + name);
            }
        }
        return path;
    }

    private void AssertExtracted(string archive, string name, string text)
    {
        string destination = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Assert.True(ArchiveProvider.ExtractAll(archive, destination, CancellationToken.None, out string? error), error);
        Assert.Equal(text, File.ReadAllText(Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar))));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
