using System;
using System.Threading;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal
{
    public sealed class TerminalClosedEventTests
    {
        // The control is built but never shown or started, so no window or shell opens.
        [Fact]
        public void ClosingATerminalRaisesClosedOnce()
        {
            int raised = 0;
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var term = new TerminalControl(TerminalSkin.Default);
                    term.Closed += () => raised++;
                    term.Close();
                    term.Close();
                }
                catch (Exception error) { failure = error; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
            Assert.Null(failure);
            Assert.Equal(1, raised);
        }
    }
}
