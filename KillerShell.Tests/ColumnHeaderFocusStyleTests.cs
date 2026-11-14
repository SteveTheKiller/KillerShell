using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerShell.Tests
{
    public sealed class ColumnHeaderFocusStyleTests
    {
        // Grid column headers are tab stops so the Apps key can open the column menu on them. A
        // header that takes keyboard focus has to show it, with an accent edge and no shadow.
        [Fact]
        public void GridColumnHeadersShowKeyboardFocus()
        {
            var xaml = XDocument.Load(Path.Combine(Root(), "Controls", "Controls.xaml"));
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var style = xaml.Descendants().Single(e => e.Name.LocalName == "Style"
                && (string?)e.Attribute(x + "Key") == "DarkDataGridColumnHeader");
            var trigger = style.Descendants().SingleOrDefault(e => e.Name.LocalName == "Trigger"
                && (string?)e.Attribute("Property") == "IsKeyboardFocused" && (string?)e.Attribute("Value") == "True");
            Assert.NotNull(trigger);
            Assert.Contains(trigger!.Elements(), s => (string?)s.Attribute("Value") == "{DynamicResource PrimaryBrush}");
            Assert.DoesNotContain(style.Descendants(), e => e.Name.LocalName.Contains("DropShadow"));
        }

        private static string Root()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "KillerShell.csproj"))) return dir.FullName;
            throw new InvalidOperationException("KillerShell.csproj was not found above the test output.");
        }
    }
}
