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
            var wrap = menu.Items.OfType<MenuItem>().Single(item => item.InputGestureText == "Alt+Z");
            var peer = UIElementAutomationPeer.CreatePeerForElement(wrap);
            var toggle = Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle));
            bool before = editor.WordWrap;
            Assert.Equal(before ? ToggleState.On : ToggleState.Off, toggle.ToggleState);
            toggle.Toggle();
            ShellWindowFixture.PumpUntil(() => editor.WordWrap != before);
            Assert.Equal(!before ? ToggleState.On : ToggleState.Off, toggle.ToggleState);
            toggle.Toggle();
            ShellWindowFixture.PumpUntil(() => editor.WordWrap == before);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            ShellWindowFixture.PumpUntil(() => editor.WordWrap != before);
        }
        finally { menu.IsOpen = false; }
    }
}
