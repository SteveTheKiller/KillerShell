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
        public void AWindowTitleAloneCountsAsTheShellListening()
        {
            var buffer = new TerminalBuffer(80, 25);
            var sent = new List<string>();
            using var staged = new StagedInput(buffer, "run", sent.Add, TimeSpan.FromSeconds(30));
            Feed(buffer, "\u001b]0;Command Prompt\u0007");
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
