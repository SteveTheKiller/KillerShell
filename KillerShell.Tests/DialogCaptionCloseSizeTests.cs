using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerShell.Tests
{
    // A Win98 caption button is the caption minus 4 tall and minus 2 wide, so the X on a dialog's
    // 20px band is 18x16. The main caption's 16x14 (CaptionButton*, AboutClose*) looked undersized
    // there. Every dialog caption close takes the band-derived DialogClose* size, centered.
    public sealed class DialogCaptionCloseSizeTests
    {
        [Theory]
        [InlineData("Controls/ConfirmDialog.xaml")]
        [InlineData("Controls/FileDialog.xaml")]
        [InlineData("Controls/FolderPickerDialog.xaml")]
        [InlineData("Controls/RegistryInputDialog.xaml")]
        [InlineData("Controls/RenameDialog.xaml")]
        [InlineData("Controls/TerminalProfilesDialog.xaml")]
        public void DialogCaptionCloseUsesTheBandDerivedSize(string file)
        {
            string xaml = File.ReadAllText(Path.Combine(FindRepoRoot(), file));
            var closes = Regex.Matches(xaml, "<Button[^>]*AboutCloseGlyph[^>]*>", RegexOptions.Singleline)
                .Cast<Match>().Select(m => m.Value).ToList();
            Assert.Single(closes);
            string close = closes[0];
            Assert.Contains("Width=\"{DynamicResource DialogCloseWidth}\"", close);
            Assert.Contains("Height=\"{DynamicResource DialogCloseHeight}\"", close);
            Assert.Contains("VerticalAlignment=\"Center\"", close);
        }

        [Fact]
        public void DialogCloseSizeDerivesFromTheDialogBand()
        {
            string tm = File.ReadAllText(Path.Combine(FindRepoRoot(), "Services/ThemeManager.cs"));
            Assert.Contains("SetIfAbsent(\"DialogCloseWidth\", pickerCaption ? bar - 2 : 28.0);", tm);
            Assert.Contains("SetIfAbsent(\"DialogCloseHeight\", pickerCaption ? bar - 4 : 26.0);", tm);
            Assert.Contains("combined[\"CaptionGlyphSize\"] = flat ? 8.0 : 10.0;", tm);
            string controls = File.ReadAllText(Path.Combine(FindRepoRoot(), "Controls/Controls.xaml"));
            Assert.Contains("<Setter Property=\"FontSize\" Value=\"{DynamicResource CaptionGlyphSize}\"/>", controls);
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerShell.csproj")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("KillerShell repository root not found.");
        }
    }
}
