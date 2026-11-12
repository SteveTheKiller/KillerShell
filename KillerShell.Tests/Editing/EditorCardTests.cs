using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using KillerShell.Editing;
using KillerShell.Tests.Terminal;
using Xunit;

namespace KillerShell.Tests.Editing;

public sealed class EditorCardTests
{
    [Fact]
    public void TheDocumentCardNamesTheFileAndDimsWhatItCannotDo()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var editor = new EditorControl(string.Empty);
                editor.LoadEmpty();
                EditorMenuCommand? raised = null;
                editor.MenuCommand += command => raised = command;
                var menu = editor.ContextMenu!;

                var head = Assert.Single(menu.Items.OfType<MenuItem>(), item => (string?)item.Tag == "cardhead");
                var quick = Assert.Single(menu.Items.OfType<MenuItem>(), item => (string?)item.Tag == "cardquick");
                var buttons = TabCardScenario.Buttons(quick);
                Assert.Equal(6, buttons.Count);

                // With no source the card opens without a header; with one it names the file.
                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
                Assert.Equal(Visibility.Collapsed, head.Visibility);
                editor.CardHeaderSource = () => (null, "notes.txt", @"C:\Work");
                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
                Assert.Equal(Visibility.Visible, head.Visibility);
                Assert.Equal("notes.txt", AutomationProperties.GetName(head));

                // A fresh document has nothing to undo, redo, cut or copy; Paste and Save stay lit.
                Assert.Equal(new[] { false, false, false, false, true, true }, buttons.Select(b => b.IsEnabled).ToArray());

                // With a selection and an edit behind it, the four come back.
                editor.Document.Text = "hello world";
                editor.Select(0, 5);
                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
                Assert.True(buttons[0].IsEnabled, "Undo");
                Assert.True(buttons[2].IsEnabled, "Cut");
                Assert.True(buttons[3].IsEnabled, "Copy");

                // Save is the window's to carry out, so the button raises it.
                buttons[5].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Assert.Equal(EditorMenuCommand.Save, raised);

                // The rows that stayed rows keep their gestures.
                foreach (var gesture in new[] { "Ctrl+A", "Ctrl+F", "Ctrl+G", "Alt+Z", "Ctrl+W" })
                    Assert.Single(menu.Items.OfType<MenuItem>(), item => item.InputGestureText == gesture);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The editor card fixture timed out.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
