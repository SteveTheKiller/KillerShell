using System.Collections.Generic;
using System.Text;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal
{
    public sealed class VtParserTests
    {
        [Fact]
        public void Feed_PreservesUtf8AcrossChunks()
        {
            var handler = new RecordingHandler();
            var parser = new VtParser(handler);
            byte[] bytes = Encoding.UTF8.GetBytes("A€B");
            var rest = new byte[bytes.Length - 2];
            System.Array.Copy(bytes, 2, rest, 0, rest.Length);

            parser.Feed(bytes, 2);
            parser.Feed(rest, rest.Length);

            Assert.Equal(new[] { 'A', 0x20ac, 'B' }, handler.Codepoints);
        }

        [Fact]
        public void Feed_DispatchesSplitCsiSequence()
        {
            var handler = new RecordingHandler();
            var parser = new VtParser(handler);

            parser.Feed(new byte[] { 0x1b, (byte)'[', (byte)'3' }, 3);
            parser.Feed(new byte[] { (byte)'1', (byte)';', (byte)'4', (byte)'m' }, 4);

            var call = Assert.Single(handler.CsiCalls);
            Assert.Equal('m', call.Final);
            Assert.Equal(new[] { 31, 4 }, call.Parameters);
        }

        private sealed class RecordingHandler : IVtHandler
        {
            internal readonly List<int> Codepoints = [];
            internal readonly List<(char Final, int[] Parameters)> CsiCalls = [];

            public void Print(int codepoint) => Codepoints.Add(codepoint);
            public void Execute(byte control) { }
            public void CsiDispatch(char final, int[] pars, char prefix, char intermediate)
                => CsiCalls.Add((final, pars));
            public void EscDispatch(char final, char intermediate) { }
            public void OscDispatch(int command, string data) { }
        }
    }
}
