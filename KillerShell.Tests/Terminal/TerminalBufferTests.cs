using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal
{
    public sealed class TerminalBufferTests
    {
        [Fact]
        public void Print_WrapsOnlyWhenNextCharacterArrives()
        {
            var buffer = new TerminalBuffer(3, 2);
            buffer.Print('a');
            buffer.Print('b');
            buffer.Print('c');

            Assert.Equal(0, buffer.CursorRow);
            Assert.Equal(2, buffer.CursorCol);

            buffer.Print('d');

            Assert.Equal(1, buffer.CursorRow);
            Assert.Equal(1, buffer.CursorCol);
            Assert.Equal('d', buffer.LineAt(1)[0].Ch);
        }

        [Fact]
        public void AlternateScreen_RestoresOriginalContents()
        {
            var buffer = new TerminalBuffer(4, 2);
            buffer.Print('a');
            buffer.CsiDispatch('h', [1049], '?', '\0');
            buffer.Print('x');
            Assert.True(buffer.AltScreen);

            buffer.CsiDispatch('l', [1049], '?', '\0');

            Assert.False(buffer.AltScreen);
            Assert.Equal('a', buffer.LineAt(0)[0].Ch);
        }

        [Fact]
        public void FullScreenScroll_PreservesScrollback()
        {
            var buffer = new TerminalBuffer(2, 2);
            buffer.Print('a');
            buffer.Execute(0x0A);
            buffer.Print('b');
            buffer.Execute(0x0A);

            Assert.Equal(1, buffer.ScrollbackCount);
            Assert.Equal('a', buffer.LineAt(0)[0].Ch);
        }
    }
}
