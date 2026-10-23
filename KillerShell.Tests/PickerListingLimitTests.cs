using System;
using System.IO;
using System.Threading;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests;

public sealed class PickerListingLimitTests
{
    [Fact]
    public void ExactLimitCompletesAndAdditionalEntryIsExplicitlyIncomplete()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShellPickerLimit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (int index = 0; index < 10000; index++) Directory.CreateDirectory(Path.Combine(root, index.ToString()));
            Assert.Equal(10000, PickerDirectoryReader.Read(root, CancellationToken.None)!.Length);
            Directory.CreateDirectory(Path.Combine(root, "overflow"));
            Assert.Throws<IOException>(() => PickerDirectoryReader.Read(root, CancellationToken.None));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Assert.Throws<OperationCanceledException>(() => PickerDirectoryReader.Read(root, canceled.Token));
        }
        finally { Directory.Delete(root, true); }
    }
}
