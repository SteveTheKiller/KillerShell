using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class DemoArchiveMutationTests
{
    [Fact]
    public Task DemoArchiveCreationAndExtractionStayInTheSession()
        => RuntimeComponentProcess.Run(nameof(DemoArchiveMutationScenario));
}

public static class DemoArchiveMutationScenario
{
    public static void Run()
    {
        const string folder = @"C:\Users\Demo\Documents\Runbooks";
        const string requested = @"C:\Users\Demo\Documents\runbooks-demo.zip";
        string leaf = DemoFs.Children(folder).First(entry => !entry.IsDir).Name;
        string source = Path.Combine(folder, leaf);
        string Text(string path) => "Session content for " + path;

        string? created = DemoArchives.Create(requested, new[] { folder }, Text);
        Assert.Equal(requested, created);
        Assert.NotNull(DemoFs.Find(created!));
        Assert.True(DemoArchives.Contains(created!));
        Assert.Contains(DemoArchives.List(created!, ""), entry => entry.Name == "Runbooks" && entry.IsDirectory);
        string entryPath = "Runbooks/" + leaf;
        Assert.Equal(Text(source), DemoArchives.TextFor(ArchiveProvider.Combine(created!, entryPath)));
        string? second = DemoArchives.Create(requested, new[] { folder }, Text);
        Assert.NotNull(second);
        Assert.NotEqual(created, second);
        Assert.Equal(Text(source), DemoArchives.TextFor(ArchiveProvider.Combine(created!, entryPath)));

        string? extracted = DemoArchives.Extract(created!, true);
        Assert.NotNull(extracted);
        string extractedFile = Path.Combine(extracted!, "Runbooks", leaf);
        Assert.NotNull(DemoFs.Find(extractedFile));
        Assert.Equal(Text(source), DemoTextStore.Open(extractedFile, "missing"));
        Assert.NotEqual(extracted, DemoArchives.Extract(created!, true));

        bool readUnknown = false;
        Assert.Null(DemoArchives.Create(requested, new[] { @"C:\private\real.txt" }, path =>
        {
            readUnknown = true;
            return path;
        }));
        Assert.False(readUnknown);
        Assert.Null(DemoArchives.Create(@"C:\private\real.zip", new[] { folder }, Text));
    }
}
