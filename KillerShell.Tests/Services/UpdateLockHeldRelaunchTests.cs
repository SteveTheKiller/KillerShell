using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

// The machine helper's two gates: a parent still running means no reopen, and a parent that has
// exited while another install holds the lock means the old copy is reopened without publishing.
[Collection("UpdateRelaunchArgument")]
public sealed class UpdateLockHeldRelaunchTests
{
    private static string Decompressed(ProcessStartInfo helper)
    {
        string arguments = helper.Arguments;
        int from = arguments.IndexOf("FromBase64String('", StringComparison.Ordinal) + "FromBase64String('".Length;
        byte[] packed = Convert.FromBase64String(arguments.Substring(from, arguments.IndexOf('\'', from) - from));
        using var gzip = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void AnExitedParentWithTheInstallLockHeldReopensTheOldCopyAndPublishesNothing()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "lockheld", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string name = @"Global\KillerTools-Install-" + AppInfo.DisplayName;
        using var held = new Mutex(false, name);
        Assert.True(held.WaitOne(0));
        try
        {
            string download = Path.Combine(root, "download.exe");
            string target = Path.Combine(root, "KillerShell.exe");
            File.WriteAllText(download, "release bytes");
            File.WriteAllText(target, "installed bytes");
            string hash;
            using (var sha = System.Security.Cryptography.SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(download))).Replace("-", "");
            UpdateImagePublication.RememberVerified(download, hash, new string('A', 40));
            // A process id of 1 never matches the recorded start time, so both waits find nothing running.
            var helper = TrustedUpdate.Publication(download, target, "1.5.1", new string('B', 64), 1, 1, 1, 1);
            string reopened = Path.Combine(root, "reopened.txt");
            string script = "function Start-KillerUpdatedApp { param($t, $r, $e) [IO.File]::WriteAllText('" + reopened + "', $t) }\n"
                + Decompressed(helper).Replace("function Start-KillerUpdatedApp", "function Start-KillerUpdatedAppReal");
            string file = Path.Combine(root, "helper.ps1");
            File.WriteAllText(file, script, new UTF8Encoding(true));
            var start = new ProcessStartInfo(helper.FileName, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + file + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(120000));
            Assert.Equal(1, process.ExitCode);
            Assert.True(File.Exists(reopened), "The old copy was not reopened: " + output);
            Assert.Equal(target, File.ReadAllText(reopened));
            Assert.Equal("installed bytes", File.ReadAllText(target));
            Assert.Contains("Another KillerTools install or update is running", File.ReadAllText(download + ".recovery.txt") + output);
        }
        finally { held.ReleaseMutex(); Directory.Delete(root, true); }
    }
}
