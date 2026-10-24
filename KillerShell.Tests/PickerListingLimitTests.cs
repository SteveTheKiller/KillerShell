using System;
using System.IO;
using System.Threading;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests;

public sealed class PickerListingLimitTests
{
    // A folder past the limit still opens with what was read, marked incomplete. 1.5.0 refused
    // such folders outright, so a big Downloads folder could not be browsed in a picker at all.
    [Fact]
    public void ExactLimitCompletesAndAdditionalEntryListsWhatWasReadAsIncomplete()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShellPickerLimit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            for (int index = 0; index < 10000; index++) Directory.CreateDirectory(Path.Combine(root, index.ToString()));
            var full = PickerDirectoryReader.Read(root, CancellationToken.None)!;
            Assert.Equal(10000, full.Entries.Length);
            Assert.Equal(PickerListingLimit.None, full.Limit);

            Directory.CreateDirectory(Path.Combine(root, "overflow"));
            var capped = PickerDirectoryReader.Read(root, CancellationToken.None)!;
            Assert.Equal(PickerListingLimit.TooManyEntries, capped.Limit);
            Assert.Equal(10000, capped.Entries.Length);

            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Assert.Throws<OperationCanceledException>(() => PickerDirectoryReader.Read(root, canceled.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AnIncompleteListingSaysWhyInTheAppsLanguage()
    {
        string Loc(string key) => key switch
        {
            "Str_Dlg_ListingTooMany" => "first {0}",
            "Str_Dlg_ListingTooSlow" => "slow",
            _ => key
        };
        Assert.Equal(string.Format("first {0}", PickerDirectoryReader.MaximumEntries),
            PickerDirectoryReader.LimitNote(PickerListingLimit.TooManyEntries, Loc));
        Assert.Equal("slow", PickerDirectoryReader.LimitNote(PickerListingLimit.TooSlow, Loc));
        Assert.Null(PickerDirectoryReader.LimitNote(PickerListingLimit.None, Loc));
    }
}
