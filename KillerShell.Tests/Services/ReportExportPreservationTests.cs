using System;
using System.IO;
using System.Linq;
using System.Text;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class ReportExportPreservationTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public ReportExportPreservationTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    public void HtmlExportPreservesThePreviousReportAfterAPartialWriteFailure()
        => AssertPreserved(path => HtmlExporter.Export(path, Results(), Array.Empty<SearchTerm>(), "C:\\data"));

    [Fact]
    public void StorageHtmlExportPreservesThePreviousReportAfterAPartialWriteFailure()
        => AssertPreserved(path => StorageHtmlExporter.Export(path, Report()));

    [Fact]
    public void CsvExportPreservesThePreviousReportAfterAPartialWriteFailure()
        => AssertPreserved(path => CsvExport.Export(path, Enumerable.Range(0, 4).SelectMany(_ => Results())));

    [Theory]
    [InlineData("csv", false)]
    [InlineData("html", false)]
    [InlineData("storage", false)]
    [InlineData("csv", true)]
    [InlineData("html", true)]
    [InlineData("storage", true)]
    public void ExportPreservesReadOnlyAndAccessDeniedReports(string format, bool denyWrite)
    {
        string root = Path.Combine(Path.GetTempPath(), "ks-report-protected-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "report");
        byte[] original = Encoding.UTF8.GetBytes("Previous complete report\r\n");
        File.WriteAllBytes(path, original);
        var originalAcl = File.GetAccessControl(path);
        try
        {
            if (denyWrite)
            {
                var denied = File.GetAccessControl(path);
                denied.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                    System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.AppendData,
                    System.Security.AccessControl.AccessControlType.Deny));
                File.SetAccessControl(path, denied);
            }
            else File.SetAttributes(path, FileAttributes.ReadOnly);
            Assert.Throws<UnauthorizedAccessException>(() => Export(format, path));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            File.SetAccessControl(path, originalAcl);
            File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("html")]
    [InlineData("storage")]
    public void SuccessfulExportKeepsUtf8BomAndReportFormat(string format)
    {
        string root = Path.Combine(Path.GetTempPath(), "ks-report-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "report");
        try
        {
            File.WriteAllText(path, "old report");
            Export(format, path);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
            if (format == "csv") Assert.StartsWith("Name,Folder,SizeBytes,Modified,Found\r\n\"file0.txt\",\"C:\\data\",0,\"\",\"\"\r\n", File.ReadAllText(path));
            else
            {
                Assert.StartsWith("<!doctype html", File.ReadAllText(path).ToLowerInvariant());
                Assert.Contains("file0.txt", File.ReadAllText(path));
                Assert.Contains("</html>", File.ReadAllText(path));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Export(string format, string path)
    {
        if (format == "csv") CsvExport.Export(path, Results());
        else if (format == "html") HtmlExporter.Export(path, Results(), Array.Empty<SearchTerm>(), "C:\\data");
        else StorageHtmlExporter.Export(path, Report());
    }

    private static SearchResult[] Results() => Enumerable.Range(0, 120).Select(i => new SearchResult
        { FileName = "file" + i + ".txt", FilePath = "C:\\data\\file" + i + ".txt", Directory = "C:\\data" }).ToArray();

    private static StorageReport Report()
    {
        var root = new StorageReportNode("data", "C:\\data", 120, true);
        foreach (var result in Results()) root.Children.Add(new StorageReportNode(result.FileName, result.FilePath, 1, false));
        return new StorageReport("C:\\data", "C:\\data", 120, 0, 0, false, root);
    }

    private void AssertPreserved(Action<string> export)
    {
        string root = Path.Combine(Path.GetTempPath(), "ks-report-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "report.html");
        byte[] original = Encoding.UTF8.GetBytes("Previous complete report\r\n");
        File.WriteAllBytes(path, original);
        try
        {
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                // Permit the beginning of a direct overwrite, then fail a later native write.
                // Excluding delete sharing also exercises the safe writer's in-place fallback.
                locked.Lock(4096, 4096);
                try
                {
                    Assert.ThrowsAny<IOException>(() => export(path));
                }
                finally { locked.Unlock(4096, 4096); }
            }
            byte[] final = File.ReadAllBytes(path);
            _output.WriteLine("original_sha256=" + VerifiedFile.Sha256(original));
            _output.WriteLine("final_sha256=" + VerifiedFile.Sha256(final));
            Assert.Equal(original, final);
        }
        finally { Directory.Delete(root, true); }
    }
}
