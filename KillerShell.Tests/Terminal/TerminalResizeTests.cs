using System.Linq;
using System.Text;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal
{
    public sealed class TerminalResizeTests
    {
        private static TerminalBuffer Fed(int cols, int rows, string output)
        {
            var buffer = new TerminalBuffer(cols, rows);
            var parser = new VtParser(buffer);
            byte[] bytes = Encoding.UTF8.GetBytes(output);
            parser.Feed(bytes, bytes.Length);
            return buffer;
        }

        private static string[] Transcript(TerminalBuffer buffer)
            => Enumerable.Range(0, buffer.TotalLines)
                .Select(i => string.Concat(buffer.LineAt(i).Select(c => c.Text)).TrimEnd())
                .ToArray();

        [Fact]
        public void ShrinkingMovesTopRowsIntoScrollbackAndKeepsThePrompt()
        {
            var buffer = Fed(10, 4, "one\r\ntwo\r\nthree\r\nPS> ");

            buffer.Resize(10, 2);

            Assert.Equal(new[] { "one", "two", "three", "PS>" }, Transcript(buffer));
            Assert.Equal(2, buffer.ScrollbackCount);
            Assert.Equal(1, buffer.CursorRow);
            Assert.Equal(4, buffer.CursorCol);
        }

        [Fact]
        public void ShrinkThenGrowKeepsTranscriptOrder()
        {
            var buffer = Fed(10, 4, "one\r\ntwo\r\nthree\r\nPS> ");

            buffer.Resize(10, 2);
            buffer.Resize(10, 5);

            Assert.Equal(new[] { "one", "two", "three", "PS>", "", "", "" }, Transcript(buffer));
            Assert.Equal(1, buffer.CursorRow);
        }

        [Fact]
        public void RowsKeepTheirIdentityAndIndexAcrossAShrink()
        {
            var buffer = Fed(10, 4, "one\r\ntwo\r\nthree\r\nPS> ");
            object[] before = Enumerable.Range(0, buffer.TotalLines).Select(i => buffer.LineIdentity(buffer.LineAt(i))).ToArray();

            buffer.Resize(10, 2);

            for (int i = 0; i < 4; i++)
                Assert.Same(before[i], buffer.LineIdentity(buffer.LineAt(i)));
        }

        [Fact]
        public void ExistingScrollbackStaysAheadOfTheMovedRows()
        {
            var buffer = Fed(10, 3, "a\r\nb\r\nc\r\nd\r\ne");   // a and b already scrolled off
            Assert.Equal(2, buffer.ScrollbackCount);

            buffer.Resize(10, 1);

            Assert.Equal(new[] { "a", "b", "c", "d", "e" }, Transcript(buffer));
            Assert.Equal(0, buffer.CursorRow);
        }

        [Fact]
        public void EmptyRowsBelowTheCursorGoFirst()
        {
            var buffer = Fed(10, 5, "one\r\nPS> ");   // three blank rows below the prompt

            buffer.Resize(10, 2);

            Assert.Equal(0, buffer.ScrollbackCount);
            Assert.Equal(new[] { "one", "PS>" }, Transcript(buffer));
        }

        [Fact]
        public void OutputBelowTheCursorIsKeptWhenItFits()
        {
            // The cursor moved back up to row 1 after writing row 3.
            var buffer = Fed(10, 4, "one\r\ntwo\r\nthree\r\nfour\u001b[2;1H");

            buffer.Resize(10, 3);

            Assert.Equal(new[] { "one", "two", "three", "four" }, Transcript(buffer));
            Assert.Equal(0, buffer.CursorRow);
        }

        [Fact]
        public void AlternateScreenIsNotTurnedIntoScrollback()
        {
            var buffer = Fed(10, 4, "main\r\n\u001b[?1049hA1\r\nA2\r\nA3\r\nA4");

            buffer.Resize(10, 2);

            Assert.True(buffer.AltScreen);
            Assert.Equal(0, buffer.ScrollbackCount);
        }
    }
}
