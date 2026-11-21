using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using KillerShell.Controls;
using Xunit;

namespace KillerShell.Tests.Terminal;

/// <summary>
/// The System Tools rail button opens a tile card like the app's other 1.6.1 menus, not a row list:
/// the click card holds the five tools, the right-click card holds the elevated ones. Filled without
/// opening, so no popup is shown.
/// </summary>
[Trait("Category", "Window")]
public sealed class SystemToolsCardTests
{
    [Fact]
    public Task SystemToolsRailOpensTileCards() => RuntimeComponentProcess.Run(nameof(SystemToolsCardScenario));
}

public static class SystemToolsCardScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var button = window.SystemToolsRailBtn;

        var tools = (ContextMenu)button.Resources["SystemToolsMenu"];
        window.FillSystemToolsCard(tools, admin: false);
        AssertCard(tools, "processes", "performance", "storage", "registry", "eventviewer");

        window.FillSystemToolsCard(button.ContextMenu, admin: true);
        AssertCard(button.ContextMenu, "processesadmin", "storageadmin", "registry", "eventviewer");
    }

    private static void AssertCard(ContextMenu card, params string[] ids)
    {
        Assert.Same(card.FindResource("CardMenu"), card.Style);
        Assert.DoesNotContain(card.Items.OfType<MenuItem>(), item => item.Header is string);
        foreach (var id in ids) Assert.NotNull(CardMenus.TileButton(card, id));
        Assert.Equal(ids.Length, CardMenus.Buttons(card).Count());
    }
}
