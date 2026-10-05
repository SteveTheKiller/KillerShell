using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerShell.Tests
{
    public sealed class MenuSeparatorThemeTests
    {
        private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
        private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        [Fact]
        public void DeliriumOverridesOnlyMenuDividerColors()
        {
            string root = FindRepoRoot();
            foreach (string path in Directory.GetFiles(Path.Combine(root, "Themes"), "*.xaml"))
            {
                var resources = XDocument.Load(path).Root!.Elements().ToList();
                var divider = resources.SingleOrDefault(item => (string?)item.Attribute(X + "Key") == "MenuSeparatorBrush");
                if (Path.GetFileNameWithoutExtension(path) == "Delirium")
                {
                    Assert.NotNull(divider);
                    Assert.Equal("#666666", (string?)divider!.Attribute("Color"));
                }
                else
                {
                    Assert.Null(divider);
                    Assert.Contains(resources, item => (string?)item.Attribute(X + "Key") == "MenuBorderBrush");
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
