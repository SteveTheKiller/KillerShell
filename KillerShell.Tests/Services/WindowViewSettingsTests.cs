using System;
using System.Collections.Generic;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class WindowViewSettingsTests
{
    [Fact]
    public void WindowsKeepSeparateValuesAndReuseReleasedSlots()
    {
        var values = new Dictionary<string, string>();
        string role = "test" + Guid.NewGuid().ToString("N");
        string? Read(string key) => values.TryGetValue(key, out var value) ? value : null;
        void Write(string key, string value) => values[key] = value;
        using var first = new WindowViewSettings(role, Read, Write);
        var second = new WindowViewSettings(role, Read, Write);
        try
        {
            first.Set("ResultsViewL", "0");
            second.Set("ResultsViewL", "2");
            Assert.NotEqual(first.Slot, second.Slot);
            Assert.Equal("0", first.Get("ResultsViewL"));
            Assert.Equal("2", second.Get("ResultsViewL"));
            int slot = second.Slot;
            second.Dispose();
            using var reopened = new WindowViewSettings(role, Read, Write);
            Assert.Equal(slot, reopened.Slot);
            Assert.Equal("2", reopened.Get("ResultsViewL"));
            Assert.Equal("0", first.Get("ResultsViewL"));
        }
        finally { second.Dispose(); }
    }
}
