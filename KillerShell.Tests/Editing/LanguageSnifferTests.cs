using KillerShell.Editing;
using Xunit;

namespace KillerShell.Tests.Editing
{
    public sealed class LanguageSnifferTests
    {
        [Theory]
        [InlineData(".ps1", "$FinalFree  = Get-FreeBytes\n$StartFree  = [int64]$StartSpace\nfunction Get-FreeColor { param([double]$Pct) if ($Pct -lt 10) { \"Red\" } else { \"Green\" } }\nWrite-Host \"Space Recovered\" -NoNewline -ForegroundColor $InfoCol\n")]
        [InlineData(".bat", "@echo off\nsetlocal\nset TARGET=%~dp0build\nif not exist \"%TARGET%\" mkdir \"%TARGET%\"\ngoto :done\n:done\n")]
        [InlineData(".reg", "Windows Registry Editor Version 5.00\n\n[HKEY_CURRENT_USER\\Software\\KillerTools]\n\"Theme\"=\"Dark\"\n")]
        [InlineData(".json", "{\n  \"name\": \"killershell\",\n  \"version\": \"1.5.0\"\n}\n")]
        [InlineData(".xml", "<?xml version=\"1.0\"?>\n<configuration><appSettings /></configuration>\n")]
        [InlineData(".html", "<!DOCTYPE html>\n<html><body><p>Hi</p></body></html>\n")]
        [InlineData(".cs", "using System;\nusing System.IO;\n\nnamespace Demo\n{\n    public static class Program\n    {\n        public static void Main() { }\n    }\n}\n")]
        [InlineData(".py", "import os\nfrom pathlib import Path\n\ndef clean(folder: str) -> int:\n    return 0\n\nif __name__ == \"__main__\":\n    print(clean(\".\"))\n")]
        [InlineData(".yml", "---\nservices:\n  - name: web\n    image: nginx\n  - name: db\n    image: postgres\n")]
        [InlineData(".ini", "[General]\nTheme=Dark\nAccent=Blue\n\n[Window]\nWidth=1200\nHeight=800\n")]
        [InlineData(".md", "# KillerShell\n\nA **fast** file browser. See [the site](https://thekiller.net).\n\n```powershell\nkillershell --help\n```\n")]
        public void RecognizesCommonFormats(string expected, string text)
            => Assert.Equal(expected, LanguageSniffer.Detect(text));

        [Theory]
        [InlineData("")]
        [InlineData("   \n  ")]
        [InlineData("Meeting notes\nCall the vendor back on Tuesday about the warranty.\n")]
        public void PlainTextStaysPlain(string text)
            => Assert.Null(LanguageSniffer.Detect(text));
    }
}
