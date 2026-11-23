using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using KillerShell.Controls;
using KillerShell.Editing;
using Xunit;

namespace KillerShell.Tests.Terminal;

/// <summary>
/// The document bar's gear opens a tile card under the gear button: the four reading options are on/off tiles
/// that flip in place and keep the card open, Indent is a flyout whose rows check the current width, and Fonts
/// hands off to the Fonts dialog.
/// </summary>
[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class EditorGearCardTests
{
    [Fact]
    public Task GearOpensATileCardWhoseOptionsFlipInPlace() => RuntimeComponentProcess.Run(nameof(EditorGearCardScenario));
}

public static class EditorGearCardScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var create = (Button)window.FindName("NewDocRailBtn");
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(create).GetPattern(PatternInterface.Invoke)).Invoke();
        ShellWindowFixture.PumpUntil(() => window.Pane.Active.Editor != null);

        var gearButton = window.Pane.EditorGearBtn;
        window.EditorGear_Click(gearButton, new RoutedEventArgs());
        ContextMenu? card = null;
        ShellWindowFixture.PumpUntil(() => (card = OpenGearCard()) != null && card.ActualWidth > 0);
        try
        {
            Assert.Same(gearButton, card!.PlacementTarget);
            string[] tiles = CardMenus.Buttons(card).Select(b => b.CommandParameter as string).OfType<string>().ToArray();
            Assert.Equal(new[] { "linenumbers", "currentline", "whitespace", "spaces", "indent", "fonts" }, tiles);

            string[] widths = CardMenus.Commands(card).OfType<MenuItem>().Select(row => (string)row.Tag).ToArray();
            Assert.Equal(new[] { "indent2", "indent4", "indent8" }, widths);
            Assert.Equal("indent" + EditorOptions.IndentSize,
                (string)CardMenus.Commands(card).OfType<MenuItem>().Single(row => row.IsChecked).Tag);

            // An option tile flips the setting, fills or clears itself, and the card stays open.
            var lineNumbers = CardMenus.TileButton(card, "linenumbers")!;
            bool before = EditorOptions.LineNumbers;
            Assert.Equal(before, Filled(lineNumbers));
            ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(lineNumbers).GetPattern(PatternInterface.Invoke)).Invoke();
            ShellWindowFixture.PumpUntil(() => EditorOptions.LineNumbers != before);
            Assert.True(card.IsOpen);
            Assert.Equal(!before, Filled(lineNumbers));
        }
        finally { if (card != null) card.IsOpen = false; }
    }

    private static bool Filled(Button tile) => tile.ReadLocalValue(Control.BackgroundProperty) != DependencyProperty.UnsetValue;

    private static ContextMenu? OpenGearCard()
    {
        foreach (PresentationSource source in PresentationSource.CurrentSources)
            if (source.RootVisual is Visual root && Visuals(root).OfType<ContextMenu>().FirstOrDefault(m => Equals(m.Tag, "editorgear") && m.IsOpen) is { } card)
                return card;
        return null;
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var d in Visuals(VisualTreeHelper.GetChild(root, i))) yield return d;
    }
}
