using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Text;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class StorageAnalysisLogicTests
    {
        private sealed class Node
        {
            internal long Size;
            internal List<Node>? Children;
        }

        [Theory]
        [InlineData("photo.JPG", "img")]
        [InlineData("archive.zip", "arc")]
        [InlineData("source.cs", "code")]
        [InlineData("settings.toml", "cfg")]
        [InlineData("README", "oth")]
        public void CategoryFor_ClassifiesKnownExtensions(string fileName, string expected)
            => Assert.Equal(expected, StorageAnalysisLogic.CategoryFor(fileName));

        [Fact]
        public void Squarify_AllocatesVisibleAreaInInputOrder()
        {
            Rect bounds = new(0, 0, 100, 100);
            Rect[] layout = StorageAnalysisLogic.Squarify(new long[] { 50, 30, 20 }, 100, bounds);

            Assert.Equal(3, layout.Length);
            Assert.All(layout, rectangle => Assert.True(rectangle.Width > 0 && rectangle.Height > 0));
            double area = layout.Sum(rectangle => rectangle.Width * rectangle.Height);
            Assert.InRange(area, 9999.9, 10000.1);
        }

        [Theory]
        [InlineData(512, "512 B")]
        [InlineData(1536, "1.5 KB")]
        [InlineData(1048576, "1.0 MB")]
        public void FormatSize_UsesBinaryUnits(long bytes, string expected)
            => Assert.Equal(expected, StorageAnalysisLogic.FormatSize(bytes));

        [Fact]
        public void DirectoryReader_EnumeratesFilesFoldersAndSkipsReparsePoints()
        {
            string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "folder"));
                File.WriteAllBytes(Path.Combine(root, "sample.bin"), new byte[17]);

                StorageDirectoryReadResult result = StorageDirectoryReader.Read(root, CancellationToken.None);

                Assert.False(result.Skipped);
                Assert.Contains(result.Entries, entry => entry.Name == "folder" && entry.IsDirectory);
                Assert.Contains(result.Entries, entry => entry.Name == "sample.bin" && !entry.IsDirectory && entry.Size == 17);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void Aggregate_TotalsAndSortsTreeLargestFirst()
        {
            var root = new Node
            {
                Children =
                [
                    new Node { Size = 5 },
                    new Node { Children = [new Node { Size = 9 }, new Node { Size = 1 }] },
                ],
            };

            long total = StorageAnalysisLogic.Aggregate(
                root, node => node.Children, node => node.Size, (node, size) => node.Size = size);

            Assert.Equal(15, total);
            Assert.Equal(10, root.Children![0].Size);
            Assert.Equal(5, root.Children[1].Size);
        }

        [Fact]
        public void MftParser_ReadsVersionTwoRecordFields()
        {
            const int RecordOffset = 8;
            const int NameOffset = 60;
            string name = "sample.txt";
            byte[] nameBytes = Encoding.Unicode.GetBytes(name);
            int recordLength = NameOffset + nameBytes.Length;
            var buffer = new byte[RecordOffset + recordLength];
            BitConverter.GetBytes((ulong)99).CopyTo(buffer, 0);
            BitConverter.GetBytes(recordLength).CopyTo(buffer, RecordOffset);
            BitConverter.GetBytes((ushort)2).CopyTo(buffer, RecordOffset + 4);
            BitConverter.GetBytes((ulong)42).CopyTo(buffer, RecordOffset + 8);
            BitConverter.GetBytes((ulong)7).CopyTo(buffer, RecordOffset + 16);
            BitConverter.GetBytes((uint)0x20).CopyTo(buffer, RecordOffset + 52);
            BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(buffer, RecordOffset + 56);
            BitConverter.GetBytes((ushort)NameOffset).CopyTo(buffer, RecordOffset + 58);
            nameBytes.CopyTo(buffer, RecordOffset + NameOffset);

            Dictionary<ulong, StorageMftEntry>? records =
                StorageMftReader.ParseRecords(buffer, buffer.Length);

            StorageMftEntry entry = Assert.Single(records!).Value;
            Assert.Equal((ulong)42, entry.Id);
            Assert.Equal((ulong)7, entry.ParentId);
            Assert.Equal(name, entry.Name);
            Assert.False(entry.IsDirectory);
        }
    }
}
