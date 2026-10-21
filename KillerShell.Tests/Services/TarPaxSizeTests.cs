using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class TarPaxSizeTests
{
    [Theory]
    [InlineData(0, false, true)]
    [InlineData(511, false, true)]
    [InlineData(512, false, true)]
    [InlineData(513, false, true)]
    [InlineData(600, false, false)]
    [InlineData(0, true, true)]
    [InlineData(511, true, true)]
    [InlineData(512, true, true)]
    [InlineData(513, true, true)]
    [InlineData(600, true, false)]
    public void PathAndSizeMetadataSurviveHeaderSequencesPaddingAndCompression(int size, bool gzip, bool split)
    {
        string root = Path.Combine(Path.GetTempPath(), "ks-pax-variant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string archive = Path.Combine(root, gzip ? "fixture.tar.gz" : "fixture.tar");
            TarPaxFixture.WriteVariant(archive, size, gzip, split);
            var entries = ArchiveProvider.List(archive, "", out string? error);
            Assert.Null(error);
            Assert.Equal(new[] { TarPaxFixture.LongName, "following.txt" }, entries.Select(entry => entry.Name));
            Assert.Equal(new long[] { size, 9 }, entries.Select(entry => entry.Size));
            string output = Path.Combine(root, "out");
            Assert.True(ArchiveProvider.ExtractAll(archive, output, CancellationToken.None, out error), error);
            Assert.Equal(new string('A', size), File.ReadAllText(Path.Combine(output, TarPaxFixture.LongName)));
            Assert.Equal("following", File.ReadAllText(Path.Combine(output, "following.txt")));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => ArchiveProvider.List(archive, "", out _, canceled.Token));
            Assert.ThrowsAny<OperationCanceledException>(() => ArchiveProvider.ExtractAll(archive,
                Path.Combine(root, "canceled"), canceled.Token, out _));
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "canceled"), "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("length-overflow")]
    [InlineData("length-beyond-payload")]
    [InlineData("missing-newline")]
    [InlineData("negative-size")]
    [InlineData("overflow-size")]
    [InlineData("fractional-size")]
    [InlineData("empty-size")]
    [InlineData("truncated-metadata")]
    [InlineData("GNU.sparse.size")]
    [InlineData("GNU.sparse.realsize")]
    [InlineData("SCHILY.realsize")]
    public void InvalidOrUnsupportedPaxMetadataDoesNotProduceAListing(string mode)
    {
        string root = Path.Combine(Path.GetTempPath(), "ks-pax-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string archive = Path.Combine(root, "fixture.tar");
            string record = mode switch
            {
                "length-overflow" => "999999999999999999999 size=1\n",
                "length-beyond-payload" => "99 size=1\n",
                "missing-newline" => "9 size=1!",
                "negative-size" => TarPaxFixture.Record("size", "-1"),
                "overflow-size" => TarPaxFixture.Record("size", "9223372036854775808"),
                "fractional-size" => TarPaxFixture.Record("size", "1.5"),
                "empty-size" => TarPaxFixture.Record("size", ""),
                "truncated-metadata" => "9 size=1\n",
                _ => TarPaxFixture.Record(mode, "600")
            };
            using (var file = File.Create(archive))
            {
                TarPaxFixture.Member(file, "PaxHeaders/first", Encoding.ASCII.GetBytes(record), 'x');
                TarPaxFixture.Member(file, "first.txt", Encoding.ASCII.GetBytes("payload"), '0');
                file.Write(new byte[1024], 0, 1024);
                if (mode == "truncated-metadata") file.SetLength(512 + record.Length - 1);
            }
            Assert.Empty(ArchiveProvider.List(archive, "", out string? error));
            Assert.NotNull(error);
            string output = Path.Combine(root, "out");
            Assert.False(ArchiveProvider.ExtractAll(archive, output, CancellationToken.None, out error));
            Assert.Empty(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(500)]
    [InlineData(700)]
    public void TruncatedPaxSizedPayloadOrPaddingIsRejectedBeforeExtraction(int bytesAfterHeader)
    {
        string root = Path.Combine(Path.GetTempPath(), "ks-pax-truncated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string archive = Path.Combine(root, "fixture.tar");
            TarPaxFixture.Write(archive);
            using (var truncated = new FileStream(archive, FileMode.Open, FileAccess.Write))
                truncated.SetLength(1536 + bytesAfterHeader);
            Assert.Empty(ArchiveProvider.List(archive, "", out string? error));
            Assert.NotNull(error);
            string output = Path.Combine(root, "out");
            Assert.False(ArchiveProvider.ExtractAll(archive, output, CancellationToken.None, out error));
            Assert.NotNull(error);
            Assert.Empty(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("size")]
    [InlineData("path")]
    public void GlobalPathAndSizeOverridesAreRefusedBeforeExtractingFiles(string key)
    {
        string root = Path.Combine(Path.GetTempPath(), "ks-pax-global-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string archive = Path.Combine(root, "fixture.tar");
            TarPaxFixture.WriteGlobal(archive, key);
            var entries = ArchiveProvider.List(archive, "", out string? error);
            Assert.Empty(entries);
            Assert.Contains("global PAX", error);
            string output = Path.Combine(root, "out");
            Assert.False(ArchiveProvider.ExtractAll(archive, output, CancellationToken.None, out error));
            Assert.Contains("global PAX", error);
            Assert.Empty(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void LocalPaxSizeControlsListingExtractionAndFollowingHeaderAlignment()
    {
        string root = Path.Combine(Path.GetTempPath(), "ks-pax-size-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string archive = Path.Combine(root, "fixture.tar");
            TarPaxFixture.Write(archive);
            var entries = ArchiveProvider.List(archive, "", out string? error);
            Assert.Null(error);
            Assert.Equal(new[] { "first.txt", "following.txt" }, entries.Select(entry => entry.Name));
            Assert.Equal(600, entries[0].Size);
            Assert.Equal(9, entries[1].Size);

            string output = Path.Combine(root, "out");
            Assert.True(ArchiveProvider.ExtractAll(archive, output, CancellationToken.None, out error), error);
            Assert.Equal(new string('A', 600), File.ReadAllText(Path.Combine(output, "first.txt")));
            Assert.Equal("following", File.ReadAllText(Path.Combine(output, "following.txt")));
        }
        finally { Directory.Delete(root, true); }
    }
}

public static class TarPaxFixture
{
    internal static string LongName => new string('p', 110) + ".txt";

    public static void WriteVariant(string path, int size, bool gzip, bool split)
    {
        using var file = File.Create(path);
        using Stream output = gzip ? new GZipStream(file, CompressionMode.Compress) : file;
        Member(output, "GlobalHeader", Encoding.ASCII.GetBytes(Record("comment", "global metadata")), 'g');
        string pathRecord = Record("path", LongName);
        string sizeRecord = Record("size", size.ToString(CultureInfo.InvariantCulture));
        if (split)
        {
            Member(output, "PaxHeaders/path", Encoding.ASCII.GetBytes(pathRecord), 'x');
            Member(output, "PaxHeaders/size", Encoding.ASCII.GetBytes(sizeRecord), 'x');
        }
        else Member(output, "PaxHeaders/first", Encoding.ASCII.GetBytes(pathRecord + sizeRecord), 'x');
        Member(output, "short.txt", Encoding.ASCII.GetBytes(new string('A', size)), '0', 0);
        Member(output, "following.txt", Encoding.ASCII.GetBytes("following"), '0');
        output.Write(new byte[1024], 0, 1024);
    }

    public static void WriteGlobal(string path, string key = "size")
    {
        using var output = File.Create(path);
        Member(output, "GlobalHeader", Encoding.ASCII.GetBytes(key == "size" ? "12 size=600\n" : "20 path=renamed.txt\n"), 'g');
        Member(output, "first.txt", Encoding.ASCII.GetBytes(new string('A', 600)), '0', 1);
        Member(output, "PaxHeaders/second", Encoding.ASCII.GetBytes("9 size=9\n"), 'x');
        Member(output, "following.txt", Encoding.ASCII.GetBytes("following"), '0', 1);
        Member(output, "third.txt", Encoding.ASCII.GetBytes(new string('B', 600)), '0', 1);
        output.Write(new byte[1024], 0, 1024);
    }

    public static void Write(string path)
    {
        using var output = File.Create(path);
        Member(output, "PaxHeaders/first", Encoding.ASCII.GetBytes("12 size=600\n"), 'x');
        Member(output, "first.txt", Encoding.ASCII.GetBytes(new string('A', 600)), '0', 1);
        Member(output, "following.txt", Encoding.ASCII.GetBytes("following"), '0');
        output.Write(new byte[1024], 0, 1024);
    }

    internal static string Record(string key, string value)
    {
        string body = " " + key + "=" + value + "\n";
        int length = body.Length + 1;
        while (length.ToString(CultureInfo.InvariantCulture).Length + body.Length != length)
            length = length.ToString(CultureInfo.InvariantCulture).Length + body.Length;
        return length.ToString(CultureInfo.InvariantCulture) + body;
    }

    internal static void Member(Stream output, string name, byte[] content, char type, int? headerSize = null)
    {
        byte[] header = new byte[512];
        void Field(int offset, string value)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(value);
            Array.Copy(bytes, 0, header, offset, bytes.Length);
        }
        Field(0, name);
        Field(100, "0000644\0");
        Field(108, "0000000\0");
        Field(116, "0000000\0");
        Field(124, Convert.ToString(headerSize ?? content.Length, 8).PadLeft(11, '0') + "\0");
        Field(136, "00000000000\0");
        Field(148, "        ");
        header[156] = (byte)type;
        Field(257, "ustar\000");
        Field(148, Convert.ToString(header.Sum(value => (int)value), 8).PadLeft(6, '0') + "\0 ");
        output.Write(header, 0, header.Length);
        output.Write(content, 0, content.Length);
        int padding = (512 - content.Length % 512) % 512;
        output.Write(new byte[padding], 0, padding);
    }
}
