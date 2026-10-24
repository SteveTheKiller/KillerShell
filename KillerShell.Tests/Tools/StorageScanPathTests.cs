using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Tools;

public sealed class StorageScanPathTests
{
    [Theory]
    [InlineData(new[] { @"C:\" }, @"C:\")]
    [InlineData(new[] { @"C:\", "Windows" }, @"C:\Windows")]
    [InlineData(new[] { @"C:\", "Users", "steve" }, @"C:\Users\steve")]
    [InlineData(new[] { @"D:\Projects" }, @"D:\Projects")]
    [InlineData(new[] { @"D:\Projects", "app" }, @"D:\Projects\app")]
    public void ADriveRootKeepsItsSeparator(string[] parts, string expected)
        => Assert.Equal(expected, StorageAnalyzerControl.JoinScanPath(parts));
}
