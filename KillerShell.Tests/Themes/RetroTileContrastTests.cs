using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit;

namespace KillerShell.Tests.Themes;

/// <summary>
/// 98SE fills a checked card tile solid with the accent's RowSelectedBrush and draws its glyph, caption and
/// shortcut in TileCheckedTextBrush. Every accent's pair has to read: white suits the dark accents, and the
/// Yellow accent's bright fill needs dark text, as its other selected text already is.
/// </summary>
public sealed class RetroTileContrastTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void EveryRetroAccentsCheckedTileTextReadsOnItsFill()
    {
        string repo = RepoRoot();
        var theme = XDocument.Load(Path.Combine(repo, "Themes", "98SE.xaml"));
        Color baseText = ColorOf(theme, "TileCheckedTextBrush")!.Value;
        Color baseFill = ColorOf(theme, "RowSelectedBrush")!.Value;

        var accents = Directory.GetFiles(Path.Combine(repo, "Themes", "Accents", "98SE"), "*.xaml");
        Assert.NotEmpty(accents);
        foreach (string file in accents)
        {
            var accent = XDocument.Load(file);
            Color fill = ColorOf(accent, "RowSelectedBrush") ?? baseFill;
            Color text = ColorOf(accent, "TileCheckedTextBrush") ?? baseText;
            double ratio = Contrast(fill, text);
            Assert.True(ratio >= 4.5, $"{Path.GetFileNameWithoutExtension(file)}: checked tile text {text} on {fill} is {ratio:0.00}:1");
        }
    }

    private static Color? ColorOf(XDocument doc, string key)
    {
        var brush = doc.Root!.Elements().FirstOrDefault(e => e.Name.LocalName == "SolidColorBrush" && (string?)e.Attribute(X + "Key") == key);
        return brush == null ? null : (Color)ColorConverter.ConvertFromString((string)brush.Attribute("Color")!);
    }

    private static double Contrast(Color a, Color b)
    {
        static double Channel(byte v) { double x = v / 255.0; return x <= 0.04045 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        static double L(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        double la = L(a), lb = L(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Themes", "98SE.xaml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("KillerShell repo root");
    }
}
