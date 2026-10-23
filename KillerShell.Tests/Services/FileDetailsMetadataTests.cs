using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class FileDetailsMetadataTests
    {
        [Theory]
        [InlineData(".png")]
        [InlineData(".pdf")]
        [InlineData(".mp4")]
        [InlineData(".exe")]
        public void Read_MissingFilesHaveNoInventedMetadata(string extension)
        {
            var details = FileDetailsMetadata.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension));
            Assert.Equal(0, details.PixelWidth);
            Assert.Null(details.HasAlpha);
            Assert.Null(details.PageCount);
            Assert.Null(details.Duration);
            Assert.Empty(details.FileVersion);
        }

        [Fact]
        public void Read_CorruptImageLeavesFileDetailsUnavailable()
        {
            string path = Path.Combine(Path.GetTempPath(), "KillerShell.Metadata-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllText(path, "This is not an image.");
                var details = FileDetailsMetadata.Read(path);
                Assert.Equal(0, details.PixelWidth);
                Assert.Null(details.HasAlpha);
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void Read_OpaqueImageDoesNotClaimTransparency()
        {
            string path = Path.Combine(Path.GetTempPath(), "KillerShell.Metadata-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var bitmap = BitmapSource.Create(30, 20, 96, 96, PixelFormats.Bgr24, null, new byte[30 * 20 * 3], 30 * 3);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(path)) encoder.Save(output);
                var details = FileDetailsMetadata.Read(path);
                Assert.Equal("3:2", details.AspectRatio);
                Assert.Equal(24, details.BitsPerPixel);
                Assert.False(details.HasAlpha);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void Read_ExecutableReportsItsFileVersion()
        {
            var assembly = typeof(FileDetailsMetadata).Assembly;
            FileDetailsMetadata details = FileDetailsMetadata.Read(assembly.Location);

            Assert.Equal(assembly.GetName().Version.ToString(), details.FileVersion);
            Assert.Equal(0, details.PixelWidth);
        }

        [Fact]
        public void Read_ImageReportsStoredDimensionsRatioDepthAndAlphaWithoutKeepingFileOpen()
        {
            string path = Path.Combine(Path.GetTempPath(), "KillerShell.Metadata-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var pixels = new byte[80 * 40 * 4];
                var bitmap = BitmapSource.Create(80, 40, 96, 96, PixelFormats.Bgra32, null, pixels, 80 * 4);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(path)) encoder.Save(output);

                FileDetailsMetadata details = FileDetailsMetadata.Read(path);

                Assert.Equal(80, details.PixelWidth);
                Assert.Equal(40, details.PixelHeight);
                Assert.Equal("2:1", details.AspectRatio);
                Assert.Equal(32, details.BitsPerPixel);
                Assert.True(details.HasAlpha);
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
