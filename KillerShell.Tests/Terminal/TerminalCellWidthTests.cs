using System.Text;
using System.Linq;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class TerminalCellWidthTests
{
    [Theory]
    [InlineData("A\u65e5\u672cB", 6, 5)]
    [InlineData("A\U0001F600B", 4, 3)]
    [InlineData("Ae\u0301B", 3, 2)]
    [InlineData("A\u00a1B", 3, 2)]
    [InlineData("A\U0001F469\u200d\U0001F4BBB", 6, 5)]
    [InlineData("A\U0001F1FA\U0001F1F8B", 6, 5)]
    [InlineData("A\U0001F600\U0001F3FBB", 4, 3)]
    public void FragmentedUnicodeUsesDisplayCells(string text, int cursor, int finalColumn)
    {
        var buffer = new TerminalBuffer(20, 3);
        var parser = new VtParser(buffer);
        foreach (byte value in Encoding.UTF8.GetBytes(text)) parser.Feed(new[] { value }, 1);
        Assert.Equal(cursor, buffer.CursorCol);
        Assert.Equal('B', buffer.LineAt(0)[finalColumn].Ch);
    }

    [Theory]
    [InlineData("\u001b[3G\u001b[X", "A  B")]
    [InlineData("\u001b[3Gx", "A xB")]
    [InlineData("\u001b[3G\u001b[@", "A   B")]
    [InlineData("\u001b[2G\u001b[P", "A B")]
    [InlineData("\u001b[3G\u001b[P", "A B")]
    [InlineData("\u001b[2G\u001b[K", "A")]
    public void EditingEitherHalfNeverLeavesHalfAWideGlyph(string operation, string expected)
    {
        var buffer = new TerminalBuffer(8, 2);
        Feed(buffer, "A\u65e5\u0301B" + operation);
        Assert.Equal(expected, Text(buffer, 0));
        AssertValid(buffer.LineAt(0));
    }

    [Fact]
    public void WrappingCombiningScrollbackAndAlternateResizePreserveWholeCells()
    {
        var buffer = new TerminalBuffer(4, 2);
        Feed(buffer, "ab\u65e5\u0301X");
        Assert.Equal("ab\u65e5\u0301", Text(buffer, 0));
        Assert.Equal("X", Text(buffer, 1));
        Assert.Equal(1, buffer.CursorRow);
        Assert.Equal(1, buffer.CursorCol);
        Feed(buffer, "\r\n");
        Assert.Equal(1, buffer.ScrollbackCount);
        Assert.Equal("ab\u65e5\u0301", Text(buffer, 0));
        Feed(buffer, "\u001b[?1049hA\u65e5B");
        buffer.Resize(2, 2);
        Assert.Equal("A", Text(buffer, buffer.ScrollbackCount));
        AssertValid(buffer.LineAt(buffer.ScrollbackCount));
        Feed(buffer, "\u001b[?1049l");
        Assert.Equal("X", Text(buffer, buffer.ScrollbackCount));
    }

    private static void Feed(TerminalBuffer buffer, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        new VtParser(buffer).Feed(bytes, bytes.Length);
    }

    [Fact]
    public void GrowingWhileWrapIsPendingKeepsNextCharacterOnTheRetainedRow()
    {
        var buffer = new TerminalBuffer(4, 2);
        Feed(buffer, "ab\u65e5");
        buffer.Resize(8, 2);
        Feed(buffer, "X");
        Assert.Equal("ab\u65e5X", Text(buffer, 0));
        Assert.Equal(5, buffer.CursorCol);
        Assert.Equal(0, buffer.CursorRow);
    }

    [Fact]
    public void RepeatCopiesWideBaseAndItsRetainedSuffix()
    {
        var buffer = new TerminalBuffer(12, 2);
        Feed(buffer, "\u65e5\u0301\u001b[2b");
        Assert.Equal("\u65e5\u0301\u65e5\u0301\u65e5\u0301", Text(buffer, 0));
        Assert.Equal(6, buffer.CursorCol);
        AssertValid(buffer.LineAt(0));
    }

    [Fact]
    public void WideCharacterWrapsBeforeTheEdgeOrUsesReplacementWhenItCannotFit()
    {
        var buffer = new TerminalBuffer(4, 2);
        Feed(buffer, "abc\u65e5X");
        Assert.Equal("abc", Text(buffer, 0));
        Assert.Equal("\u65e5X", Text(buffer, 1));
        var noWrap = new TerminalBuffer(4, 2);
        Feed(noWrap, "\u001b[?7labc\u65e5");
        Assert.Equal("abc\uFFFD", Text(noWrap, 0));
        var narrow = new TerminalBuffer(1, 2);
        Feed(narrow, "\u65e5");
        Assert.Equal("\uFFFD", Text(narrow, 0));
    }

    [Fact]
    public void CombiningSuffixIsBoundedWithoutSplittingASurrogate()
    {
        var buffer = new TerminalBuffer(8, 2);
        Feed(buffer, "e" + new string('\u0301', 63) + "\U0001F3FB\u0301\u0301B");
        Assert.Equal("e" + new string('\u0301', 64) + "B", Text(buffer, 0));
        Assert.Equal(2, buffer.CursorCol);
    }

    private static string Text(TerminalBuffer buffer, int line)
        => string.Concat(buffer.LineAt(line).Select(cell => cell.Text)).TrimEnd();

    private static void AssertValid(Cell[] line)
    {
        for (int col = 0; col < line.Length; col++)
        {
            if (line[col].IsContinuation) Assert.True(col > 0 && line[col - 1].Width == 2);
            if (line[col].Width == 2) Assert.True(col + 1 < line.Length && line[col + 1].IsContinuation);
        }
    }
}
