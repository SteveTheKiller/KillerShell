using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using KillerShell.Controls;
using Xunit;

namespace KillerShell.Tests.Terminal;

/// <summary>
/// The document bar's encoding readout opens a tile card with the current encoding's tile filled, and the pane
/// toolbar's Export button opens a small tile card with CSV and HTML. Both anchor to the button that opened them.
/// </summary>
[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class PaneDropdownCardTests
{
    [Fact]
    public Task EncodingAndExportOpenKitMenus() => RuntimeComponentProcess.Run(nameof(PaneDropdownCardScenario));
}

public static class PaneDropdownCardScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = window.Pane;

        // Export first, on the folder tab the window opens with.
        window.ExportButton_Click(pane.ExportBtn, new RoutedEventArgs());
        ContextMenu? export = null;
        ShellWindowFixture.PumpUntil(() => (export = Open("exportcard")) != null && export.ActualWidth > 0);
        Assert.Same(pane.ExportBtn, export!.PlacementTarget);
        Assert.Equal(new[] { "csv", "html" }, CardMenus.Buttons(export).Select(b => b.CommandParameter as string).OfType<string>().ToArray());
        Assert.Equal("Ctrl+Alt+Shift+E", System.Windows.Automation.AutomationProperties.GetAcceleratorKey(CardMenus.TileButton(export, "csv")!));
        Assert.Equal("Ctrl+Alt+E", System.Windows.Automation.AutomationProperties.GetAcceleratorKey(CardMenus.TileButton(export, "html")!));
        export.IsOpen = false;
        ShellWindowFixture.PumpUntil(() => Open("exportcard") == null);

        var create = (Button)window.FindName("NewDocRailBtn");
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(create).GetPattern(PatternInterface.Invoke)).Invoke();
        ShellWindowFixture.PumpUntil(() => pane.Active.Editor != null);
        var editor = pane.Active.Editor!;
        string before = editor.EncodingLabel;

        window.EditorEncoding_Click(pane.EditorEncodingBtn, new RoutedEventArgs());
        ContextMenu? encodings = null;
        ShellWindowFixture.PumpUntil(() => (encodings = Open("editorencoding")) != null && encodings.ActualWidth > 0);
        Assert.Same(pane.EditorEncodingBtn, encodings!.PlacementTarget);
        Assert.Equal(new[] { "utf8", "utf8bom", "utf16le", "utf16be", "ansi" },
            CardMenus.Buttons(encodings).Select(b => b.CommandParameter as string).OfType<string>().ToArray());
        Assert.Equal(new[] { "utf8" }, FilledTiles(encodings));

        // A changed encoding makes the document dirty, and a dirty document stops the fixture's window close on a
        // save prompt, which would turn any failed assert below into a hang. The finally puts it back first.
        try
        {
            PickAndReopen(window, pane, editor, encodings, before);
        }
        catch (System.Exception failure)
        {
            System.Console.Error.WriteLine(failure);
            throw;
        }
        finally
        {
            if (Open("editorencoding") is { } menu) menu.IsOpen = false;
            editor.SetEncoding(new System.Text.UTF8Encoding(false));
        }
    }

    private static void PickAndReopen(KillerShell.Shell.MainWindow window, FilePane pane, KillerShell.Editing.EditorControl editor,
        ContextMenu card, string before)
    {
        ContextMenu? encodings = null;
        Invoke(CardMenus.TileButton(card, "utf16le")!);
        ShellWindowFixture.PumpUntil(() => editor.EncodingLabel == "UTF-16 LE");
        Assert.NotEqual(before, editor.EncodingLabel);
        Assert.Equal("UTF-16 LE", pane.EditorEncodingBtn.Content);
        Assert.True(editor.Dirty);
        ShellWindowFixture.PumpUntil(() => Open("editorencoding") == null);

        // Reopened, the card fills the new encoding's tile; picking UTF-8 again puts the document back as it
        // was saved, so it is clean and the window closes without a save prompt.
        window.EditorEncoding_Click(pane.EditorEncodingBtn, new RoutedEventArgs());
        ShellWindowFixture.PumpUntil(() => (encodings = Open("editorencoding")) != null && encodings.ActualWidth > 0);
        Assert.Equal(new[] { "utf16le" }, FilledTiles(encodings!));
        Invoke(CardMenus.TileButton(encodings!, "utf8")!);
        ShellWindowFixture.PumpUntil(() => editor.EncodingLabel == "UTF-8" && Open("editorencoding") == null);
        Assert.False(editor.Dirty);
    }

    private static void Invoke(Button tile)
        => ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(tile).GetPattern(PatternInterface.Invoke)).Invoke();

    // A turned-on tile carries its accent fill as a local Background; the others keep the style's.
    private static string[] FilledTiles(ContextMenu card)
        => CardMenus.Buttons(card).Where(b => b.ReadLocalValue(Control.BackgroundProperty) != DependencyProperty.UnsetValue)
            .Select(b => b.CommandParameter as string).OfType<string>().ToArray();

    private static ContextMenu? Open(string tag)
    {
        foreach (PresentationSource source in PresentationSource.CurrentSources)
            if (source.RootVisual is Visual root && Visuals(root).OfType<ContextMenu>().FirstOrDefault(m => Equals(m.Tag, tag) && m.IsOpen) is { } menu)
                return menu;
        return null;
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var d in Visuals(VisualTreeHelper.GetChild(root, i))) yield return d;
    }
}
