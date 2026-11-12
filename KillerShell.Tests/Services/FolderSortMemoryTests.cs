using System.Collections.Generic;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class FolderSortMemoryTests
{
    private readonly Dictionary<string, string> _settings = new();

    private FolderSortMemory Memory()
        => new("FolderSorts", k => _settings.TryGetValue(k, out var v) ? v : null, (k, v) => _settings[k] = v);

    [Fact]
    public void AnUnknownFolderOpensNewestFirst()
        => Assert.Equal((4, false), Memory().For(@"C:\Users\steve\Pictures"));

    [Fact]
    public void AFolderKeepsTheSortItWasGiven()
    {
        Memory().Remember(@"C:\Users\steve\Pictures\Screenshots", 1, true);
        Assert.Equal((1, true), Memory().For(@"C:\Users\steve\Pictures\Screenshots"));
        Assert.Equal((4, false), Memory().For(@"C:\Users\steve\Pictures"));
    }

    [Fact]
    public void CaseAndATrailingSeparatorNameTheSameFolder()
    {
        Memory().Remember(@"C:\Work\", 3, false);
        Assert.Equal((3, false), Memory().For(@"c:\work"));
        Memory().Remember(@"D:", 5, true);
        Assert.Equal((5, true), Memory().For(@"D:\"));
    }

    [Fact]
    public void SettingAFolderAgainReplacesItsEntry()
    {
        var memory = Memory();
        memory.Remember(@"C:\Work", 1, true);
        memory.Remember(@"C:\Work", 3, false);
        Assert.Equal((3, false), memory.For(@"C:\Work"));
        Assert.Single(_settings["FolderSorts"].Split('\n'));
    }

    [Fact]
    public void TheDefaultSortIsForgottenRatherThanStored()
    {
        var memory = Memory();
        memory.Remember(@"C:\Work", 1, true);
        memory.Remember(@"C:\Work", 4, false);
        Assert.Equal(string.Empty, _settings["FolderSorts"]);
        Assert.Equal((4, false), memory.For(@"C:\Work"));
    }

    [Fact]
    public void AsFoundIsNeverStoredForAFolder()
    {
        Memory().Remember(@"C:\Work", 0, true);
        Assert.False(_settings.ContainsKey("FolderSorts"));
    }

    [Fact]
    public void TheOldestFoldersFallOffPastTheCap()
    {
        var memory = Memory();
        for (int i = 0; i <= FolderSortMemory.Capacity; i++) memory.Remember(@"C:\F" + i, 1, true);
        Assert.Equal(FolderSortMemory.Capacity, _settings["FolderSorts"].Split('\n').Length);
        Assert.Equal((4, false), memory.For(@"C:\F0"));
        Assert.Equal((1, true), memory.For(@"C:\F" + FolderSortMemory.Capacity));
    }

    [Fact]
    public void ABrokenLineIsSkippedWithoutLosingTheRest()
    {
        _settings["FolderSorts"] = "junk\n9|1|C:\\Bad\n2|1|C:\\Good";
        Assert.Equal((2, true), Memory().For(@"C:\Good"));
        Assert.Equal((4, false), Memory().For(@"C:\Bad"));
    }
}
