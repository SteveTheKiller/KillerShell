using System;
using KillerShell.Models;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

public sealed class SessionFilterRowTests
{
    [Fact]
    public void TildeInTextAndSizeTextSurvivesTheRoundTrip()
    {
        var filter = new SearchFilter { FieldIndex = SearchFilter.FieldSize, ConditionIndex = 1, Text = "a~b", SizeText = "1~2", UnitIndex = 1 };

        var back = MainWindow.DecodeSessionFilter(MainWindow.EncodeSessionFilter(filter));

        Assert.NotNull(back);
        Assert.Equal("a~b", back!.Text);
        Assert.Equal("1~2", back.SizeText);
        Assert.Equal(SearchFilter.FieldSize, back.FieldIndex);
        Assert.Equal(1, back.ConditionIndex);
        Assert.Equal(1, back.UnitIndex);
    }

    [Fact]
    public void TheWrittenRowHasExactlySixFields()
    {
        var row = MainWindow.EncodeSessionFilter(new SearchFilter { Text = "~~", SizeText = "~" });

        Assert.Equal(6, row.Split('~').Length);
        Assert.Contains("%7E", row);
    }

    [Fact]
    public void AnOldRowStillReads()
    {
        long ticks = new DateTime(2020, 5, 6).Ticks;

        var back = MainWindow.DecodeSessionFilter($"1~1~old%20text~{ticks}~12~1");

        Assert.NotNull(back);
        Assert.Equal(SearchFilter.FieldDate, back!.FieldIndex);
        Assert.Equal(1, back.ConditionIndex);
        Assert.Equal("old text", back.Text);
        Assert.Equal(new DateTime(ticks), back.Date);
        Assert.Equal("12", back.SizeText);
        Assert.Equal(1, back.UnitIndex);
    }
}
