using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit;

namespace KillerShell.Tests
{
    public sealed class MenuSeparatorThemeTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
        private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        [Fact]
        public void EveryThemeDeclaresOpaqueMenuDividerColors()
        {
            string root = FindRepoRoot();
            foreach (string path in Directory.GetFiles(Path.Combine(root, "Themes"), "*.xaml"))
            {
                var resources = XDocument.Load(path).Root!.Elements().ToList();
                var divider = resources.SingleOrDefault(item => (string?)item.Attribute(X + "Key") == "MenuSeparatorBrush");
                Assert.NotNull(divider);
                Assert.Equal(W + "SolidColorBrush", divider!.Name);
                string colorText = (string?)divider.Attribute("Color") ??
                    throw new InvalidDataException("MenuSeparatorBrush must declare its color.");
                var color = (Color)ColorConverter.ConvertFromString(colorText);
                Assert.Equal((byte)255, color.A);
                string? opacityText = (string?)divider.Attribute("Opacity");
                double opacity = opacityText == null ? 1.0 :
                    double.Parse(opacityText, CultureInfo.InvariantCulture);
                Assert.Equal(1.0, opacity);
                if (Path.GetFileNameWithoutExtension(path) == "Delirium")
                {
                    Assert.Equal(Color.FromRgb(0x66, 0x66, 0x66), color);
                }
            }
        }

        [Fact]
        public void OnlyKeyedMenuSeparatorsUseTheNewBrush()
        {
            var styles = XDocument.Load(Path.Combine(FindRepoRoot(), "Controls", "Controls.xaml"))
                .Descendants(W + "Style").Where(style => (string?)style.Attribute("TargetType") == "Separator").ToList();
            var menu = styles.Single(style => (string?)style.Attribute(X + "Key") == "{x:Static MenuItem.SeparatorStyleKey}");
            var generic = styles.Single(style => style.Attribute(X + "Key") == null);
            Assert.Equal("{DynamicResource MenuSeparatorBrush}", Background(menu));
            Assert.Equal("{DynamicResource MenuBorderBrush}", Background(generic));
        }

        [Fact]
        public void OtherThemesRetainTheirPreviousFallbackAfterAccentOverlay()
        {
            string root = FindRepoRoot();
            string source = File.ReadAllText(Path.Combine(root, "Services", "ThemeManager.cs"));
            const string fallback = "Mirror(\"MenuSeparatorBrush\", \"MenuBorderBrush\");";
            Assert.Contains(fallback, source);
            Assert.True(source.IndexOf(fallback, StringComparison.Ordinal) >
                source.IndexOf("combined[key] = accentDict[key];", StringComparison.Ordinal));
        }

        private static string? Background(XElement style) =>
            (string?)style.Elements(W + "Setter").Single(setter => (string?)setter.Attribute("Property") == "Background").Attribute("Value");

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerShell.csproj")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("KillerShell repository root not found.");
        }
    }
}
