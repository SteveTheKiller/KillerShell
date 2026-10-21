using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Tests.Editing;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class SearchContentBoundsTests
{
    [Fact]
    public Task HugeLogicalLineMatchesWithinAConstrainedChild()
        => EditorScenarioProcess.Run(typeof(SearchContentMemoryScenario).FullName!);

    [Theory]
    [InlineData(4094, "needle", false)]
    [InlineData(8190, "NEEDLE", false)]
    [InlineData(16382, "needle", true)]
    public async Task CrossBufferMatchHasABoundedMarkedPreview(int prefix, string pattern, bool sensitive)
    {
        var engine = new SearchEngine(_ => new MemoryStream(Encoding.UTF8.GetBytes(new string('x', prefix) + "needle\r\nnext")));
        var found = await Search(engine, pattern, sensitive);
        var line = Assert.Single(Assert.Single(Assert.Single(found).Matches).Lines);
        Assert.Equal(1, line.LineNumber);
        Assert.True(line.LineText.Length <= 1025, "Result preview retained the entire logical line.");
        Assert.EndsWith("\u2026", line.LineText);
    }

    [Fact]
    public async Task ReadFailureIsReportedAsAnIncompleteSearch()
    {
        var engine = new SearchEngine(_ => throw new IOException("injected read failure"));
        Assert.Empty(await Search(engine, "needle"));
        Assert.Equal(1, engine.IncompletePaths);
    }

    [Fact]
    public async Task UnsupportedHugePatternIsReportedAsIncomplete()
    {
        var engine = new SearchEngine(_ => new MemoryStream(Encoding.UTF8.GetBytes("short")));
        Assert.Empty(await Search(engine, new string('x', 65537)));
        Assert.Equal(1, engine.IncompletePaths);
    }

    internal static async Task<List<SearchResult>> Search(SearchEngine engine, string pattern, bool sensitive = false)
    {
        string path = Path.GetTempFileName();
        try
        {
            var group = new TermGroup();
            group.Terms.Add(new SearchTerm { Mode = SearchTerm.SearchMode.Content, Pattern = pattern });
            var found = new List<SearchResult>();
            engine.ResultsBatch += results => found.AddRange(results);
            await engine.SearchAsync(Path.GetDirectoryName(path)!, [group], [], "", "", sensitive, CancellationToken.None, [path]);
            return found;
        }
        finally { File.Delete(path); }
    }
}

public static class SearchContentMemoryScenario
{
    public static void Run()
    {
        // Enforce the budget in this disposable child before generating any content.
        using var budget = new ProcessMemoryBudget(256 * 1024 * 1024);
        foreach (long length in new[] { 1024L * 1024, 256L * 1024 * 1024 })
        {
            var source = new SyntheticLine(length);
            var engine = new SearchEngine(_ => source);
            var found = SearchContentBoundsTests.Search(engine, "needle").GetAwaiter().GetResult();
            var line = Assert.Single(Assert.Single(Assert.Single(found).Matches).Lines);
            Assert.Equal(0, engine.IncompletePaths);
            Assert.True(source.Disposed);
            Console.WriteLine("Matched logical line bytes=" + length + ", preview chars=" + line.LineText.Length);
        }
    }

    private sealed class SyntheticLine(long length) : Stream
    {
        public bool Disposed;
        private long _position;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = (int)Math.Min(count, length - _position);
            for (int i = 0; i < take; i++)
            {
                long position = _position + i;
                buffer[offset + i] = position >= length - 6 ? (byte)"needle"[(int)(position - (length - 6))] : (byte)'x';
            }
            _position += take;
            return take;
        }
        public override long Seek(long offset, SeekOrigin origin) => _position = origin == SeekOrigin.Begin ? offset : throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = value; }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class ProcessMemoryBudget : IDisposable
    {
        private readonly IntPtr _job;
        internal ProcessMemoryBudget(uint bytes)
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            var limit = new ExtendedLimit { Basic = new BasicLimit { Flags = 0x100 }, ProcessMemory = new UIntPtr(bytes) };
            if (_job == IntPtr.Zero || !SetInformationJobObject(_job, 9, ref limit, (uint)Marshal.SizeOf<ExtendedLimit>()) ||
                !AssignProcessToJobObject(_job, Process.GetCurrentProcess().Handle))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() => CloseHandle(_job);
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
        {
            public long PerProcess, PerJob;
            public uint Flags;
            public UIntPtr MinWorkingSet, MaxWorkingSet;
            public uint ActiveProcesses;
            public UIntPtr Affinity;
            public uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong A, B, C, D, E, F; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit
        {
            public BasicLimit Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int type, ref ExtendedLimit info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
