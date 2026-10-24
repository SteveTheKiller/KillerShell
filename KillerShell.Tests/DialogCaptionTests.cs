using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShell.Controls;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Tests.Editing;
using Xunit;

namespace KillerShell.Tests;

[Trait("Category", "Window")]
public sealed class DialogCaptionTests
{
    [Fact]
    public Task PromptsShowTheAppIconAndSwitchToTheClassicCaption()
        => EditorScenarioProcess.Run(typeof(DialogCaptionScenario).FullName!);

    [Fact]
    public Task SecondaryDialogsShowIconsAndTheFullClassicRaisedFrame()
        => EditorScenarioProcess.Run(typeof(DialogCaptionScenario).FullName!, "secondary");
}

public static class DialogCaptionScenario
{
    public static void Run(string? scope = null)
    {
        ThemeManager.GetSetting = _ => null;
        ThemeManager.SetSetting = (_, _) => { };
        Application.ResourceAssembly = typeof(RenameDialog).Assembly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resource in new[] { "Themes/Dark.xaml", "Controls/Controls.xaml", "Controls/AppStyles.xaml", "Strings/en-US.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KillerShell;component/" + resource)
            });
        try
        {
            foreach (Theme theme in new[] { Theme.Dark, Theme.SE98 })
            {
                ThemeManager.Apply(theme);
                Window[] prompts =
                {
                    new RenameDialog("Example.txt"),
                    new RegistryInputDialog("Name", "Example", "Rename"),
                    new ConfirmDialog("Confirm", null, "OK")
                };
                Window[] dialogs = scope == null ? prompts : prompts.Concat(new Window[]
                {
                    new FileDialog(),
                    new FolderPickerDialog(),
                    new RegistryValueEditDialog("Example", Microsoft.Win32.RegistryValueKind.String, "Value"),
                    new ProcessDetailsDialog(new[] { new ProcessInfo(42) { Name = "Example" } }, 0),
                    new ServiceDetailsDialog(new[] { new ServiceInfo("Example") }, 0),
                    new EventDetailsDialog(new[] { new EventLogEntryInfo("Application", "Information",
                        new DateTime(2026, 1, 1), "Example", 1, "", "Message", "", "", "", "", "", "", "", "", "<Event/>") }, 0),
                    new TerminalProfilesDialog()
                }).ToArray();
                foreach (Window dialog in dialogs)
                {
                    dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                    dialog.Left = -24000;
                    dialog.Top = -24000;
                    dialog.ShowActivated = false;
                    dialog.Show();
                    dialog.UpdateLayout();
                    try
                    {
                        var icon = Assert.Single(Descendants(dialog).OfType<AppIcon>());
                        Assert.NotNull(icon.Source);
                        Assert.True(icon.IsVisible && icon.ActualWidth > 0 && icon.ActualHeight > 0,
                            dialog.GetType().Name + " must show its app icon.");
                        var caption = Descendants(dialog).OfType<TextBlock>()
                            .SingleOrDefault(text => text.Text == "KillerShell" && text.FontWeight == FontWeights.Bold);
                        Assert.NotNull(caption);
                        Assert.Equal(theme == Theme.SE98, caption!.IsVisible);
                        if (theme == Theme.SE98)
                        {
                            Assert.Equal(11, caption.FontSize);
                            Assert.Equal(dialog.FindResource("ChromeFontFamily"), caption.FontFamily);
                            if (scope != null)
                            {
                                var root = Assert.IsAssignableFrom<Border>(dialog.FindName("RootBorder"));
                                var surface = Assert.IsAssignableFrom<FrameworkElement>(root.Child);
                                var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight,
                                    96, 96, PixelFormats.Pbgra32);
                                bitmap.Render(surface);
                                Assert.Equal(Colors.White, Pixel(bitmap, 1, 10));
                                Assert.Equal(Color.FromRgb(128, 128, 128), Pixel(bitmap, bitmap.PixelWidth - 2, 10));
                            }
                        }
                    }
                    finally { dialog.Close(); }
                }
            }
        }
        finally { app.Shutdown(); }
    }

    private static Color Pixel(BitmapSource bitmap, int x, int y)
    {
        var bytes = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        return Color.FromArgb(bytes[3], bytes[2], bytes[1], bytes[0]);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
