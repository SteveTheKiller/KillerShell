using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class AddressSentinelAcceptanceTests
{
    [Fact]
    public Task RelativeThisPcAddressesCannotOpenTheProcessWorkingDirectory()
        => RuntimeComponentProcess.Run(nameof(AddressSentinelAcceptanceScenario));
}

public static class AddressSentinelAcceptanceScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = window.Pane;
        window.Opacity = 1;
        window.Activate();
        string previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = fixture.Root;
            File.WriteAllText(Path.Combine(fixture.Root, "cwd-only.txt"), "This file must not be opened.");
            Enter(window, pane, MainWindow.ThisPc);
            ShellWindowFixture.PumpUntil(() => pane.Active.IsBrowsing && pane.Active.CurrentFolder == MainWindow.ThisPc);
            var tab = pane.Active;
            int tabCount = pane.Tabs.Count;
            foreach (string typed in new[] { "..", "cwd-only.txt" })
            {
                Enter(window, pane, typed);
                ShellWindowFixture.PumpUntil(() => tab.StatusKey == "Str_Status_BadPath"
                    && tab.StatusArgs?.SingleOrDefault() as string == typed);
                Assert.Same(tab, pane.Active);
                Assert.Equal(MainWindow.ThisPc, tab.CurrentFolder);
                Assert.Equal(tabCount, pane.Tabs.Count);
            }
        }
        finally { Environment.CurrentDirectory = previousDirectory; }
    }

    private static void Enter(MainWindow window, FilePane pane, string typed)
    {
        window.BeginEditAddress();
        ShellWindowFixture.PumpUntil(() => pane.AddressBox.IsVisible && pane.AddressBox.IsKeyboardFocusWithin);
        ((IValueProvider)UIElementAutomationPeer.CreatePeerForElement(pane.AddressBox)
            .GetPattern(PatternInterface.Value)).SetValue(typed);
        pane.AddressBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(pane.AddressBox), Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }
}
