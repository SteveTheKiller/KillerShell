using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class UninstallRemovalTests
{
    [Fact]
    public async Task TrustedCleanupTreatsPathAsDataAndDeletesOnlyItsDetachedImage()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "cleanup", Guid.NewGuid().ToString("N"));
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(root);
        try
        {
            string image = Path.Combine(root, "$(literal)&image.exe");
            string sibling = Path.Combine(root, "unrelated.txt");
            File.WriteAllText(image, "detached fixture image");
            File.WriteAllText(sibling, "preserve these bytes");
            Process? child = null;
            try
            {
                using (var held = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var start = UninstallRemoval.CleanupStartInfo(image);
                    Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                        @"WindowsPowerShell\v1.0\powershell.exe"), start.FileName);
                    Assert.False(start.UseShellExecute);
                    start.RedirectStandardOutput = true;
                    start.RedirectStandardError = true;
                    child = Process.Start(start)!;
                    Assert.True(File.Exists(image));
                }
                var output = child.StandardOutput.ReadToEndAsync();
                var errors = child.StandardError.ReadToEndAsync();
                Assert.True(child.WaitForExit(40000), "Cleanup helper exceeded its bounded retry deadline.");
                Assert.True(child.ExitCode == 0, await output + await errors);
                Assert.False(File.Exists(image));
                Assert.Equal("preserve these bytes", File.ReadAllText(sibling));
            }
            finally
            {
                if (child != null)
                {
                    if (!child.HasExited) { child.Kill(); child.WaitForExit(); }
                    child.Dispose();
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
