using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class ImageOrientationTests
    {
        [Fact]
        public void Load_DecodesAndDownsamplesTemporaryImage()
        {
            string path = Path.Combine(Path.GetTempPath(), "KillerShell.Tests-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var pixels = new byte[80 * 40 * 4];
                var bitmap = BitmapSource.Create(80, 40, 96, 96, PixelFormats.Bgra32, null, pixels, 80 * 4);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(path)) encoder.Save(output);

                BitmapSource? loaded = ImageOrientation.Load(path, 20, true);

                Assert.NotNull(loaded);
                Assert.Equal(20, loaded!.PixelWidth);
                Assert.Equal(10, loaded.PixelHeight);
                Assert.True(loaded.IsFrozen);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Load_ReturnsNullForUnreadableImage()
            => Assert.Null(ImageOrientation.Load("missing-image-" + Guid.NewGuid().ToString("N"), 32, true));
    }
}
