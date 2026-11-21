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
    public void TheTerminalCardOffersEveryCommandAsATile()
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
                // Every command is a tile with its shortcut. Close is not on the card: a lone X read
                // as "close this menu" and ended a running shell (Ctrl+W still closes). The card is
                // customizable, and Fonts and Reset prompt start in its Hidden drawer for three full rows.
                var ids = KillerShell.Controls.CardMenus.Buttons(menu).Select(b => b.CommandParameter as string).Where(id => id != null).ToArray();
                Assert.Equal(new[] { "copy", "paste", "selectall", "clear", "folder", "newshell",
                    "editprompt", "reloadprofile", "profile" }, ids);
                Assert.All(KillerShell.Controls.CardMenus.Buttons(menu).Where(b => b.CommandParameter is string),
                    b => Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetAcceleratorKey(b))));

                // Open folder still asks the window to open the folder.
                KillerShell.Controls.CardMenus.TileButton(menu, "folder")!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(TerminalMenuCommand.OpenFolder, raised);

                // A tile and its chord run the same command.
                KillerShell.Controls.CardMenus.TileButton(menu, "editprompt")!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(TerminalMenuCommand.EditPrompt, raised);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The terminal card fixture timed out.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
