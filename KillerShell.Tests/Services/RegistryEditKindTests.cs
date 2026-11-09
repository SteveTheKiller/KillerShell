using KillerShell.Services;
using Microsoft.Win32;
using Xunit;

namespace KillerShell.Tests.Services;

// REG_NONE and unmapped value types hold raw bytes. Modify edits them as bytes and writes them
// back as the same type, and the list shows them as hex, never "System.Byte[]".
public sealed class RegistryEditKindTests
{
    [Fact]
    public void ANoneValueEditsAsBytesAndKeepsItsType()
        => Assert.Equal(RegistryValueKind.None, RegistryEditorLogic.EditKindFor(RegistryValueKind.None));

    [Fact]
    public void AnUnmappedTypeCannotBeWrittenBack()
        => Assert.Null(RegistryEditorLogic.EditKindFor(RegistryValueKind.Unknown));

    [Theory]
    [InlineData(RegistryValueKind.String)]
    [InlineData(RegistryValueKind.ExpandString)]
    [InlineData(RegistryValueKind.MultiString)]
    [InlineData(RegistryValueKind.DWord)]
    [InlineData(RegistryValueKind.QWord)]
    [InlineData(RegistryValueKind.Binary)]
    public void OrdinaryTypesEditAsThemselves(RegistryValueKind kind)
        => Assert.Equal(kind, RegistryEditorLogic.EditKindFor(kind));

    [Theory]
    [InlineData(RegistryValueKind.None)]
    [InlineData(RegistryValueKind.Unknown)]
    public void RawBytesAreListedAsHex(RegistryValueKind kind)
        => Assert.Equal("01 ab ff", RegistryEditorLogic.DataLabel(new byte[] { 0x01, 0xAB, 0xFF }, kind));
}
