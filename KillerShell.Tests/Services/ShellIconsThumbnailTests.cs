using System;
using System.IO;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

// A picture that cannot be decoded (a corrupt file, a format with no codec installed) is
// remembered as having no thumbnail, so scrolling past it does not open and decode it again for
// every row redraw. Changing the file clears that, because the key carries its size and time.
public sealed class ShellIconsThumbnailTests
{
    [Fact]
    public void AnUndecodablePictureIsRememberedAsHavingNoThumbnail()
    {
        string folder = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "thumbs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string bad = Path.Combine(folder, "bad.png");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        try
        {
            Assert.Null(ShellIcons.Thumbnail(bad, 48));
            Assert.True(ShellIcons.HasThumbnailEntry(bad, 48), "The failed decode was not remembered.");
        }
        finally { Directory.Delete(folder, true); }
    }
}
