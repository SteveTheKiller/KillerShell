using KillerShell.Services;
using Microsoft.Win32;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class RegistryEditorLogicTests
    {
        [Theory]
        [InlineData("", 1)]
        [InlineData("   ", 1)]
        [InlineData("Software\\KillerShell", 2)]
        [InlineData("KillerShell", 0)]
        public void ValidateName_ReturnsExpectedResult(string value, int expected)
            => Assert.Equal(expected, (int)RegistryEditorLogic.ValidateName(value));

        [Fact]
        public void TrySplitPath_SeparatesHiveAndSubkey()
        {
            Assert.True(RegistryEditorLogic.TrySplitPath(
                "HKEY_CURRENT_USER\\Software\\KillerShell", out string hive, out string subkey));
            Assert.Equal("HKEY_CURRENT_USER", hive);
            Assert.Equal("Software\\KillerShell", subkey);
            Assert.Equal("HKEY_CURRENT_USER\\Software",
                RegistryEditorLogic.ParentPath("HKEY_CURRENT_USER\\Software\\KillerShell"));
        }

        [Theory]
        [InlineData(RegistryValueKind.String, "text", "text")]
        [InlineData(RegistryValueKind.DWord, 42, "0x0000002a (42)")]
        [InlineData(RegistryValueKind.QWord, 42L, "0x000000000000002a (42)")]
        public void DataLabel_FormatsCommonValueKinds(RegistryValueKind kind, object value, string expected)
            => Assert.Equal(expected, RegistryEditorLogic.DataLabel(value, kind));

        [Fact]
        public void DataLabel_FormatsBinaryAndMultiStringValues()
        {
            Assert.Equal("00 0f ff", RegistryEditorLogic.DataLabel(
                new byte[] { 0x00, 0x0f, 0xff }, RegistryValueKind.Binary));
            Assert.Equal("one  |  two", RegistryEditorLogic.DataLabel(
                new[] { "one", "two" }, RegistryValueKind.MultiString));
        }
    }
}
