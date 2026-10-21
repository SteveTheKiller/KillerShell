using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

[Collection("Process restart")]
public sealed class ProcessRestartBrokerTests
{
    [Fact]
    public void ApprovedBrokerPreservesArgumentsAndReplacesOnlyAfterExit()
    {
        var original = new Original();
        const string arguments = "--name \"two words\" --literal & %PATH% é 日本";
        var request = new ProcessStartInfo(original.ImagePath, arguments) { WorkingDirectory = @"C:\fixture\work" };
        var broker = ProcessRestart.CreateBrokerStartInfo(42, request, 123);
        Assert.Equal("runas", broker.Verb);
        Assert.NotEqual(original.ImagePath, broker.FileName);
        bool launched = false;

        int result = ProcessRestart.RunElevatedHelper(broker.Arguments.Split(' '), true, _ => original, replacement =>
        {
            Assert.True(original.Killed);
            Assert.True(original.HasExited);
            Assert.Equal(original.ImagePath, replacement.FileName);
            Assert.Equal(arguments, replacement.Arguments);
            Assert.Equal(@"C:\fixture\work", replacement.WorkingDirectory);
            Assert.False(replacement.UseShellExecute);
            Assert.Equal(string.Empty, replacement.Verb);
            launched = true;
        }, _ => { Assert.False(original.Killed); return new MemoryStream(); });

        Assert.Equal(0, result);
        Assert.True(launched);
    }

    [Theory]
    [InlineData("unelevated")]
    [InlineData("argument-count")]
    [InlineData("pid")]
    [InlineData("creation")]
    [InlineData("timeout")]
    [InlineData("malformed-data")]
    [InlineData("nul-argument")]
    [InlineData("relative-image")]
    [InlineData("root-relative-image")]
    [InlineData("wrong-image")]
    [InlineData("stale-creation")]
    public void InvalidBrokerRequestsCannotTerminateTheOriginal(string fault)
    {
        var original = new Original();
        var args = ProcessRestart.CreateBrokerStartInfo(42, new ProcessStartInfo(original.ImagePath), 123).Arguments.Split(' ');
        if (fault == "argument-count") Array.Resize(ref args, args.Length - 1);
        if (fault == "pid") args[1] = "-1";
        if (fault == "creation") args[2] = "0";
        if (fault == "timeout") args[3] = "60001";
        if (fault == "malformed-data") args[4] = "b!";
        if (fault == "nul-argument") args[5] = Encode("a\0b");
        if (fault == "relative-image") args[4] = Encode("original.exe");
        if (fault == "root-relative-image") args[4] = Encode(@"\fixture\original.exe");
        if (fault == "wrong-image") args[4] = Encode(@"C:\fixture\different.exe");
        if (fault == "stale-creation") args[2] = "456";
        bool launched = false;

        int result = ProcessRestart.RunElevatedHelper(args, fault != "unelevated", _ => original,
            _ => launched = true, _ => new MemoryStream());

        Assert.NotEqual(0, result);
        Assert.False(original.Killed);
        Assert.False(launched);
    }

    [Theory]
    [InlineData("image-open")]
    [InlineData("exit-timeout")]
    [InlineData("replacement-launch")]
    public void BrokerReportsFailuresWithoutLaunchingAnUnconfirmedReplacement(string fault)
    {
        var original = new Original { ExitAllowed = fault != "exit-timeout" };
        var args = ProcessRestart.CreateBrokerStartInfo(42, new ProcessStartInfo(original.ImagePath), 123).Arguments.Split(' ');
        bool launchAttempted = false;

        int result = ProcessRestart.RunElevatedHelper(args, true, _ => original, _ =>
        {
            launchAttempted = true;
            throw new Win32Exception(2);
        }, _ => fault == "image-open" ? throw new UnauthorizedAccessException() : new MemoryStream());

        Assert.NotEqual(0, result);
        Assert.Equal(fault != "image-open", original.Killed);
        Assert.Equal(fault == "replacement-launch", launchAttempted);
    }

    [Fact]
    public void BrokerNonzeroExitIsReportedToTheCaller()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            "/d /c exit 5") { UseShellExecute = false, CreateNoWindow = true };
        var failure = Assert.Throws<Win32Exception>(() => ProcessRestart.LaunchBroker(start));
        Assert.Equal(5, failure.NativeErrorCode);
    }

    private static string Encode(string value) => "b" + Convert.ToBase64String(Encoding.Unicode.GetBytes(value));

    private sealed class Original : IRestartProcess
    {
        public long StartTimeUtcTicks => 123;
        public string ImagePath => @"C:\fixture\original.exe";
        public bool HasExited { get; private set; }
        internal bool Killed;
        internal bool ExitAllowed = true;
        public void Kill() => Killed = true;
        public bool WaitForExit(int milliseconds) => HasExited = ExitAllowed;
        public void Dispose() { }
    }
}
