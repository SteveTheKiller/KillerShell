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

        [Theory]
        [InlineData("\u0007", false)]
        [InlineData("\u001b\\", false)]
        [InlineData("\u001b\\", true)]
        public void Feed_DispatchesOscTerminators(string terminator, bool splitTerminator)
        {
            var handler = new RecordingHandler();
            var parser = new VtParser(handler);
            byte[] bytes = Encoding.UTF8.GetBytes("\u001b]9;9;C:\\Temp" + terminator + "X");

            if (splitTerminator)
            {
                parser.Feed(bytes, bytes.Length - 2);
                Assert.Empty(handler.OscCalls);
                parser.Feed(new byte[] { (byte)'\\', (byte)'X' }, 2);
            }
            else
                parser.Feed(bytes, bytes.Length);

            var call = Assert.Single(handler.OscCalls);
            Assert.Equal(9, call.Command);
            Assert.Equal("9;C:\\Temp", call.Data);
            Assert.Equal(new[] { (int)'X' }, handler.Codepoints);
        }

        [Theory]
        [InlineData(9, "9;C:\\caf\u00e9", "\u0007", false)]
        [InlineData(0, "9;C:\\caf\u00e9", "\u0007", false)]
        [InlineData(9, "9;C:\\caf\u00e9\\\U0001f4c1", "\u0007", false)]
        [InlineData(9, "9;C:\\caf\u00e9\\\U0001f4c1", "\u0007", true)]
        [InlineData(9, "9;C:\\caf\u00e9\\\U0001f4c1", "\u001b\\", false)]
        [InlineData(9, "9;C:\\caf\u00e9\\\U0001f4c1", "\u001b\\", true)]
        public void Feed_DecodesUtf8OscPayload(int command, string payload, string terminator, bool splitBytes)
        {
            var handler = new RecordingHandler();
            var parser = new VtParser(handler);
            byte[] bytes = Encoding.UTF8.GetBytes("\u001b]" + command + ";" + payload + terminator);

            if (splitBytes)
            {
                foreach (byte b in bytes)
                    parser.Feed(new[] { b }, 1);
            }
            else
                parser.Feed(bytes, bytes.Length);

            var call = Assert.Single(handler.OscCalls);
            Assert.Equal(command, call.Command);
            Assert.Equal(payload, call.Data);
            Assert.Empty(handler.Codepoints);
        }

        [Theory]
        [InlineData("\u0007")]
        [InlineData("\u001b\\")]
        public void Feed_RecoversFromMalformedUtf8OscPayload(string terminator)
        {
            var handler = new RecordingHandler();
            var parser = new VtParser(handler);
            byte[] prefix = Encoding.UTF8.GetBytes("\u001b]0;");
            parser.Feed(prefix, prefix.Length);
            foreach (byte b in new byte[] { 0xc3, (byte)'X', 0xf0, 0x9f })
                parser.Feed(new[] { b }, 1);

            byte[] rest = Encoding.UTF8.GetBytes(terminator + "Y\u001b]9;9;C:\\caf\u00e9" + terminator);
            parser.Feed(rest, rest.Length);

            Assert.Equal(new[] { (0, "\ufffdX\ufffd"), (9, "9;C:\\caf\u00e9") }, handler.OscCalls);
            Assert.Equal(new[] { (int)'Y' }, handler.Codepoints);
        }

        private sealed class RecordingHandler : IVtHandler
        {
            internal readonly List<int> Codepoints = [];
            internal readonly List<(char Final, int[] Parameters)> CsiCalls = [];
            internal readonly List<(int Command, string Data)> OscCalls = [];

            public void Print(int codepoint) => Codepoints.Add(codepoint);
            public void Execute(byte control) { }
            public void CsiDispatch(char final, int[] pars, char prefix, char intermediate)
                => CsiCalls.Add((final, pars));
            public void EscDispatch(char final, char intermediate) { }
            public void OscDispatch(int command, string data) => OscCalls.Add((command, data));
        }
    }
}
