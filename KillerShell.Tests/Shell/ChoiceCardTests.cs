using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Controls;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

/// <summary>
/// The Storage depth and minimum-size pickers, the search scope chooser and the terminal profile picker open tile
/// cards: one tile per choice, the current one filled, and a picked tile changes the value and closes the card.
/// </summary>
[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class ChoiceCardTests
{
    [Fact]
    public Task StorageScopeAndProfileChoicesOpenTileCards() => RuntimeComponentProcess.Run(nameof(ChoiceCardScenario));
}

public static class ChoiceCardScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;

        var storage = new StorageAnalyzerControl(System.IO.Path.GetTempPath());
        var depth = storage.BuildDepthMenu();
        Assert.Equal(new[] { "0", "1", "2", "3", "4" }, Ids(depth));
        string depthBefore = Assert.Single(Filled(depth));
        var minSize = storage.BuildMinSizeMenu();
        Assert.Equal(new[] { "0", "1048576", "10485760", "104857600" }, Ids(minSize));
        string minBefore = Assert.Single(Filled(minSize));
        try
        {
            string depthPick = depthBefore == "2" ? "3" : "2";
            Click(CardMenus.TileButton(depth, depthPick)!);
            Assert.Equal(new[] { depthPick }, Filled(storage.BuildDepthMenu()));
            string minPick = minBefore == "10485760" ? "104857600" : "10485760";
            Click(CardMenus.TileButton(minSize, minPick)!);
            Assert.Equal(new[] { minPick }, Filled(storage.BuildMinSizeMenu()));
        }
        finally
        {
            Click(CardMenus.TileButton(storage.BuildDepthMenu(), depthBefore)!);
            Click(CardMenus.TileButton(storage.BuildMinSizeMenu(), minBefore)!);
        }

        // The scope card: three scopes, one filled, and the within-results tile only while a tab holds results.
        var scope = window.SearchScopeCard();
        Assert.Equal(new[] { "window", "tabs", "computer" }, Ids(scope));
        Assert.Single(Filled(scope));
        Assert.Equal("searchscope", scope.Tag);

        // The profile card: a tile per shown profile, F8 on the default, and the manager last.
        var profiles = window.TerminalProfilesCard();
        var ids = Ids(profiles);
        Assert.Equal("manage", ids.Last());
        Assert.True(ids.Length >= 2, "At least one profile tile and the manager.");
        Assert.Empty(Filled(profiles));
        string defaultId = KillerShell.Services.ThemeManager.GetSetting("DefaultTerminalProfile") ?? "pwsh";
        if (CardMenus.TileButton(profiles, defaultId) is { } main)
            Assert.Equal("F8", System.Windows.Automation.AutomationProperties.GetAcceleratorKey(main));
    }

    private static string[] Ids(ContextMenu card)
        => CardMenus.Buttons(card).Select(b => b.CommandParameter as string).OfType<string>().ToArray();

    private static string[] Filled(ContextMenu card)
        => CardMenus.Buttons(card).Where(b => b.ReadLocalValue(Control.BackgroundProperty) != DependencyProperty.UnsetValue)
            .Select(b => b.CommandParameter as string).OfType<string>().ToArray();

    // The automation Invoke posts its click for later; a raised Click runs the tile now.
    private static void Click(Button tile) => tile.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
}
