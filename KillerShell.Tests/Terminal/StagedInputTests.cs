using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal
{
    public sealed class StagedInputTests
    {
        private static void Feed(TerminalBuffer buffer, string output)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(output);
            new VtParser(buffer).Feed(bytes, bytes.Length);
        }

        [Fact]
        public void ThePromptsDirectoryReportSendsTheCommandOnce()
        {
            var buffer = new TerminalBuffer(80, 25);
            var sent = new List<string>();
            using var staged = new StagedInput(buffer, "run", sent.Add, TimeSpan.FromSeconds(30));
            Feed(buffer, "\u001b]7;file:///C:/one\u0007");
            Feed(buffer, "\u001b]7;file:///C:/two\u0007");
            Assert.Equal(new[] { "run" }, sent);
        }

        [Fact]
        public void AShellThatNeverReportsADirectorySendsAfterTheTimeout()
        {
            var buffer = new TerminalBuffer(80, 25);
            using var sent = new ManualResetEventSlim(false);
            using var staged = new StagedInput(buffer, "run", _ => sent.Set(), TimeSpan.FromMilliseconds(50));
            Assert.True(sent.Wait(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public void AWindowTitleAloneCountsAsTheShellListeningAfterAShortSettle()
        {
            var buffer = new TerminalBuffer(80, 25);
            var sent = new List<string>();
            using var sentEvent = new ManualResetEventSlim(false);
            using var staged = new StagedInput(buffer, "run", text => { sent.Add(text); sentEvent.Set(); }, TimeSpan.FromSeconds(30), reportsDirectory: false, settle: TimeSpan.FromMilliseconds(150));
            Feed(buffer, "]0;Command Prompt");
            Assert.Empty(sent);
            Assert.True(sentEvent.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(new[] { "run" }, sent);
        }

        [Fact]
        public void AShellThatReportsADirectoryIgnoresItsTitleAndWaitsForThePrompt()
        {
            var buffer = new TerminalBuffer(80, 25);
            var sent = new List<string>();
            using var staged = new StagedInput(buffer, "run", sent.Add, TimeSpan.FromSeconds(30), reportsDirectory: true, settle: TimeSpan.FromMilliseconds(20));
            Feed(buffer, "]0;Windows PowerShell");
            Thread.Sleep(300);
            Assert.Empty(sent);
            Feed(buffer, "]7;file:///C:/one");
            Assert.Equal(new[] { "run" }, sent);
        }

        [Fact]
        public void ADisposedStageSendsNothingAndLetsGoOfTheBuffer()
        {
            var buffer = new TerminalBuffer(80, 25);
            var sent = new List<string>();
            var staged = new StagedInput(buffer, "run", sent.Add, TimeSpan.FromMilliseconds(50));
            staged.Dispose();
            Feed(buffer, "\u001b]7;file:///C:/one\u0007");
            Thread.Sleep(300);
            Assert.Empty(sent);
        }
    }
}
