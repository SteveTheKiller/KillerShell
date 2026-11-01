using System;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class UpdateNotVerifiedTextTests
{
    [Fact]
    public void TurkishNotVerifiedMessageUsesTheNeutralUpdateWord()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Strings", "tr-TR.xaml"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var xaml = XDocument.Load(Path.Combine(dir!, "Strings", "tr-TR.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string text = null!;
        foreach (var e in xaml.Descendants())
            if ((string?)e.Attribute(x + "Key") == "Str_About_UpdateNotVerified") text = e.Value;
        Assert.Equal("İndirilen güncelleme doğrulanamadı ve silindi.", text);
    }
}
