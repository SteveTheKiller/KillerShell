using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class EditorWrapAccessibilityTests
{
    [Fact]
    public Task WrapAutomationExposesAndChangesTheActualEditorState()
        => RuntimeComponentProcess.Run(nameof(EditorWrapAccessibilityScenario));
}

public static class EditorWrapAccessibilityScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var create = (Button)window.FindName("NewDocRailBtn");
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(create).GetPattern(PatternInterface.Invoke)).Invoke();
        ShellWindowFixture.PumpUntil(() => window.Pane.Active.Editor != null);
        var editor = window.Pane.Active.Editor!;
        var menu = editor.ContextMenu;
        menu.PlacementTarget = editor;
        menu.IsOpen = true;
        ShellWindowFixture.PumpUntil(() => menu.IsOpen && menu.ActualWidth > 0);
        try
        {
            // The document card is tiles since e1a0758: Word wrap is an on/off tile carrying its shortcut,
            // filled while wrap is on, and invoking it through automation flips the editor's own state.
            var wrap = KillerShell.Controls.CardMenus.TileButton(menu, "wrap");
            Assert.NotNull(wrap);
            Assert.Equal("Alt+Z", AutomationProperties.GetAcceleratorKey(wrap));
            bool before = editor.WordWrap;
            Assert.Equal(before, wrap!.ReadLocalValue(Control.BackgroundProperty) != System.Windows.DependencyProperty.UnsetValue);
            var peer = UIElementAutomationPeer.CreatePeerForElement(wrap);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            ShellWindowFixture.PumpUntil(() => editor.WordWrap != before);
        }
        finally { menu.IsOpen = false; }
    }
}
