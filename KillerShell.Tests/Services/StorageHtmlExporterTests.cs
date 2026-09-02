using System;
using System.IO;
using KillerShell.Services;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class StorageHtmlExporterTests
    {
        [Fact]
        public void Export_EncodesPathsAndNames()
        {
            string output = Path.Combine(Path.GetTempPath(), "KillerShell.Tests-" + Guid.NewGuid().ToString("N") + ".html");
            try
            {
                var root = new StorageReportNode("root", "C:\\root&folder", 10, true);
                root.Children.Add(new StorageReportNode("<script>alert(1)</script>",
                    "C:\\root&folder\\<script>alert(1)</script>", 10, false));
                var report = new StorageReport("C:\\root&folder", "C:\\root&folder", 10, 0, 0, false, root);

                StorageHtmlExporter.Export(output, report);
                string html = File.ReadAllText(output);

                Assert.Contains("C:\\root&amp;folder", html);
                Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
                Assert.DoesNotContain("<script>alert(1)</script>", html);
            }
            finally
            {
                if (File.Exists(output)) File.Delete(output);
            }
        }
    }
}
