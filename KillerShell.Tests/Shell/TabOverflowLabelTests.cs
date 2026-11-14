using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class TabOverflowLabelTests
{
    [Fact]
    public Task OverflowDisplaysLiteralUnderscores()
        => RuntimeComponentProcess.Run(nameof(TabOverflowLabelScenario));
}

public static class TabOverflowLabelScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        window.Width = 800;
        window.FocusPane(pane);
        var target = pane.Active;
        target.Title = "Backup_Nightly__2026";
        for (int i = 0; i < 12; i++)
        {
            int expected = pane.Tabs.Count + 1;
            ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(pane.NewTabBtn)
                .GetPattern(PatternInterface.Invoke)).Invoke();
            ShellWindowFixture.PumpUntil(() => pane.Tabs.Count == expected);
        }
        ShellWindowFixture.PumpUntil(() => !target.IsStripVisible && pane.TabOverflowBtn.IsVisible);
        ContextMenu? opened = null;
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is ContextMenu menu && menu.PlacementTarget == pane.TabOverflowBtn) opened = menu;
            }));
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(pane.TabOverflowBtn)
            .GetPattern(PatternInterface.Invoke)).Invoke();
        ShellWindowFixture.PumpUntil(() => opened?.IsOpen == true);
        try
        {
            var labels = opened!.Items.OfType<MenuItem>().Select(item => item.Header).OfType<DockPanel>()
                .SelectMany(panel => panel.Children.OfType<TextBlock>()).Select(text => text.Text).ToArray();
            Assert.Contains("Backup_Nightly__2026", labels);
            Assert.DoesNotContain("Backup__Nightly____2026", labels);
        }
        finally { opened!.IsOpen = false; }
    }
}
