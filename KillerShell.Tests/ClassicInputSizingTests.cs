using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using KillerShell.Services;
using KillerShell.Tests.Editing;
using KillerShell.Tests.Terminal;
using Xunit;

namespace KillerShell.Tests;

public sealed class ClassicInputSizingTests
{
    [Fact]
    public Task SingleLineFieldsBecomeCompactWithoutChangingMultilineOrModernLayout()
        => EditorScenarioProcess.Run(typeof(ClassicInputSizingScenario).FullName!);

    [Fact]
    public Task PickerFieldsUseTheCompactClassicHeightAndRestoreModernHeight()
        => EditorScenarioProcess.Run(typeof(ClassicPickerSizingScenario).FullName!);
}

public static class ClassicPickerSizingScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        try
        {
            ThemeManager.Apply(Theme.Dark);
            foreach (Window window in new Window[] { new FileDialog(), new FolderPickerDialog() })
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.ShowActivated = false;
                window.Show();
                foreach (Theme theme in new[] { Theme.Dark, Theme.SE98, Theme.Dark })
                {
                    ThemeManager.Apply(theme);
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var inputs = Descendants(window).OfType<TextBox>().Where(input => input.IsVisible).ToArray();
                    Assert.NotEmpty(inputs);
                    Assert.All(inputs, input => Assert.Equal(theme == Theme.SE98 ? 22 : 28, input.ActualHeight));
                    if (theme == Theme.SE98)
                    {
                        double originalFontSize = inputs[0].FontSize;
                        inputs[0].FontSize = 24;
                        window.UpdateLayout();
                        Assert.True(inputs[0].ActualHeight > 28, "Classic picker fields must grow for larger text.");
                        inputs[0].FontSize = originalFontSize;
                    }
                }
                window.Close();
            }
        }
        finally { app.Shutdown(); }
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}

public static class ClassicInputSizingScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        try
        {
            var single = CreateField(app, false);
            var multiline = CreateField(app, true);
            var panel = new StackPanel();
            panel.Children.Add(single);
            panel.Children.Add(multiline);
            var window = new Window { Content = panel, Width = 400, Height = 300,
                ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000 };
            window.Show();
            ThemeManager.Apply(Theme.Dark);
            double modernHeight = Height(single);
            double modernMultilineHeight = Height(multiline);
            ThemeManager.Apply(Theme.SE98);
            Assert.InRange(Height(single), 22, 24);
            Assert.True(Height(multiline) > 35, "Multiline fields must retain room for both lines.");
            single.FontSize = 24;
            Assert.True(Height(single) > 28, "Larger text must be allowed to grow the field.");
            single.FontSize = 12.5;
            ThemeManager.Apply(Theme.Dark);
            Assert.Equal(modernHeight, Height(single), 3);
            Assert.Equal(modernMultilineHeight, Height(multiline), 3);
            window.Close();
        }
        finally { app.Shutdown(); }
    }

    private static TextBox CreateField(Application app, bool multiline) => new TextBox
    {
        Style = (Style)app.FindResource("DarkTextBox"),
        FontSize = 12.5,
        Padding = new Thickness(6, 5, 6, 5),
        AcceptsReturn = multiline,
        Text = multiline ? "First line\nSecond line" : "Example.txt",
        UseLayoutRounding = true
    };

    private static double Height(TextBox field)
    {
        for (int pass = 0; pass < 3; pass++)
        {
            field.InvalidateMeasure();
            field.Measure(new Size(300, 200));
            field.Arrange(new Rect(0, 0, 300, field.DesiredSize.Height));
            field.UpdateLayout();
            field.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        }
        return field.ActualHeight;
    }
}
