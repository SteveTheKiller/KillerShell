using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerShell.Services;
using KillerShell.Tests.Editing;
using KillerShell.Tests.Terminal;
using Xunit;

namespace KillerShell.Tests;

[Trait("Category", "Window")]
public sealed class PickerThemeTests
{
    [Fact]
    public Task TextButtonsKeepTheThemeInterfaceFontWhenTheAppFontChanges()
        => EditorScenarioProcess.Run(typeof(PickerThemeScenario).FullName!);
}

public static class PickerThemeScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        ThemeManager.GetSetting = _ => null;
        ThemeManager.SetSetting = (_, _) => { };
        app.Resources["MonoFont"] = new FontFamily("Consolas");
        try
        {
            foreach (Theme theme in Enum.GetValues(typeof(Theme)))
            {
                ThemeManager.Apply(theme);
                string expected = theme == Theme.SE98 ? "Microsoft Sans Serif" : "Killer Sans UI";
                foreach (var picker in new Window[]
                {
                    new FileDialog(FileDialogMode.Open),
                    new FileDialog(FileDialogMode.Save),
                    new FolderPickerDialog()
                })
                {
                    try
                    {
                        var header = (Grid)picker.FindName("DetailsHeader");
                        var headers = Buttons(header).ToArray();
                        Assert.Equal(3, headers.Length);
                        var actions = Buttons(picker.Content).Where(button =>
                            ReferenceEquals(button.Style, picker.FindResource("OutlineButton")) ||
                            ReferenceEquals(button.Style, picker.FindResource("SurfaceButton"))).ToArray();
                        Assert.Equal(2, actions.Length);
                        foreach (Button button in headers.Concat(actions))
                            Assert.True(expected == PrimaryFamily(button.FontFamily),
                                theme + " " + picker.GetType().Name + " " + button.Name +
                                " resolved " + button.FontFamily.Source + " instead of " + expected);
                    }
                    finally { picker.Close(); }
                }
                var outline = new Button { Style = (Style)app.FindResource("OutlineButton") };
                Assert.Equal(expected, PrimaryFamily(outline.FontFamily));
            }
        }
        finally { app.Shutdown(); }
    }

    private static string PrimaryFamily(FontFamily family)
    {
        string primary = family.Source.Split(',')[0].Trim();
        return primary.Substring(primary.LastIndexOf('#') + 1);
    }

    private static IEnumerable<Button> Buttons(object node)
    {
        if (node is Button button) yield return button;
        if (node is DependencyObject parent)
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
                foreach (Button descendant in Buttons(child)) yield return descendant;
    }
}
