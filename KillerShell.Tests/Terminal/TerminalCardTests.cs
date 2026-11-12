using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class TerminalCardTests
{
    [Fact]
    public void TheTerminalCardPutsTheEverydayVerbsInItsQuickStrip()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var terminal = new TerminalControl(TerminalSkin.Default) { ShellKind = TerminalShellKind.PowerShell };
                TerminalMenuCommand? raised = null;
                terminal.MenuCommand += command => raised = command;
                var menu = (ContextMenu)typeof(TerminalControl)
                    .GetMethod("BuildMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(terminal, null)!;

                Assert.Single(menu.Items.OfType<MenuItem>(), item => (string?)item.Tag == "cardhead");
                var quick = Assert.Single(menu.Items.OfType<MenuItem>(), item => (string?)item.Tag == "cardquick");
                var buttons = TabCardScenario.Buttons(quick);
                Assert.Equal(6, buttons.Count);

                // Open folder is a button now, and it still asks the window to open the folder.
                buttons[4].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(TerminalMenuCommand.OpenFolder, raised);

                // The rows that stayed rows keep the gestures the shortcut handler looks for.
                foreach (var gesture in new[] { "F8", "Ctrl+Shift+,", "Ctrl+Shift+E", "Ctrl+Shift+R", "Ctrl+Shift+Q", "Ctrl+," })
                    Assert.Single(menu.Items.OfType<MenuItem>(), item => item.InputGestureText == gesture);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The terminal card fixture timed out.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
