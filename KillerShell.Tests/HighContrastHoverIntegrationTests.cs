using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerFamily.Accessibility;
using KillerShell.Services;
using KillerShell.Tests.Safety;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class InjectedHighContrastHoverTests
{
    [DisposableVmFact]
    [Trait("Category", "DisposableVm")]
    [Trait("Coverage", "InjectedColorRender")]
    public Task InjectedContrastColorsReachMenuAndUnselectedComboHover()
    {
        DisposableVmGuard.RequireDisposableVm();
        return RuntimeComponentProcess.Run(nameof(InjectedHighContrastHoverScenario));
    }
}

public static class InjectedHighContrastHoverScenario
{
    public static void Run()
    {
        DisposableVmGuard.RequireDisposableVm();
        string? evidence = Environment.GetEnvironmentVariable("KILLERSHELL_UI_EVIDENCE");
        var app = RuntimeComponentProcess.StartApplication();
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        Window? window = null;
        try
        {
            var menu = new HighlightableMenuItem
            {
                Header = "Highlighted menu item", InputGestureText = "Ctrl+O",
                IsCheckable = true, IsChecked = true,
                Style = (Style)app.FindResource(typeof(MenuItem)), Width = 360,
                Margin = new Thickness(12), UseLayoutRounding = true
            };
            menu.Items.Add(new MenuItem { Header = "Submenu item" });
            var combo = new HighlightableComboBoxItem
            {
                Content = "Highlighted unselected combo item", IsSelected = false,
                Style = (Style)app.FindResource(typeof(ComboBoxItem)), Width = 360,
                Margin = new Thickness(12), UseLayoutRounding = true
            };
            var panel = new StackPanel();
            panel.Children.Add(menu);
            panel.Children.Add(combo);
            window = new Window
            {
                Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
                ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000
            };
            window.Show();
            var failures = new List<string>();
            foreach (bool white in new[] { true, false })
            {
                string name = white ? "white" : "dark";
                Color background = white ? Colors.White : Colors.Black;
                Color foreground = white ? Colors.Black : Colors.White;
                var windowBrush = new SolidColorBrush(background);
                var windowTextBrush = new SolidColorBrush(foreground);
                HighContrastResources.Refresh(false, windowBrush, windowTextBrush, Brushes.Navy, Brushes.Yellow);
                ThemeManager.Apply(Theme.SE98);
                Color ordinaryHover = Paint((Brush)app.FindResource("MenuHoverBrush"));
                Color ordinaryText = Paint((Brush)app.FindResource("MenuHoverTextBrush"));

                // Exercise the production builder, Publish, and Restore with injected colors.
                // This does not verify Windows contrast switching or native accessibility clients.
                // A distinct selection pair prevents a mistaken HighlightText mapping from passing.
                HighContrastResources.Refresh(true, windowBrush, windowTextBrush, Brushes.Navy, Brushes.Yellow);
                Pump();
                Assert.True(HighContrastResources.GetIsActive(window), "The production high-contrast overlay is inactive.");
                Assert.Equal(background, Paint((Brush)app.FindResource("MenuHoverBrush")));

                menu.Highlight();
                combo.Highlight();
                Pump();
                window.UpdateLayout();
                Assert.True(menu.IsHighlighted);
                Assert.True(combo.IsHighlighted);
                Assert.False(combo.IsSelected, "Selection would exercise a different foreground trigger.");
                var menuBorder = Assert.IsType<Border>(menu.Template.FindName("MenuBd", menu));
                var comboBorder = Assert.IsType<Border>(combo.Template.FindName("bg", combo));
                Check(Paint(menuBorder.Background) == background, name + ": menu hover background", failures);
                Check(Paint(comboBorder.Background) == background, name + ": combo hover background", failures);
                Check(Paint(menu.Foreground) == foreground, name + ": menu hover foreground is " + Paint(menu.Foreground), failures);
                Check(Paint(combo.Foreground) == foreground, name + ": combo hover foreground is " + Paint(combo.Foreground), failures);
                foreach (string part in new[] { "Check", "Gesture", "SubArrow" })
                {
                    var text = Assert.IsType<TextBlock>(menu.Template.FindName(part, menu));
                    Check(Paint(text.Foreground) == foreground, name + ": menu " + part + " foreground", failures);
                }
                var icon = Assert.IsType<ContentPresenter>(menu.Template.FindName("IconPresenter", menu));
                Check(Paint(TextElement.GetForeground(icon)) == foreground, name + ": menu icon foreground", failures);
                Check(ColorContrast.Contrast(Paint(menu.Foreground), Paint(menuBorder.Background)) >= 7,
                    name + ": rendered menu foreground/background contrast", failures);
                Check(ColorContrast.Contrast(Paint(combo.Foreground), Paint(comboBorder.Background)) >= 7,
                    name + ": rendered combo foreground/background contrast", failures);
                Capture(menu, evidence, "InjectedContrast-98SE-" + name + "-menu", foreground, failures);
                Capture(combo, evidence, "InjectedContrast-98SE-" + name + "-combo", foreground, failures);
                Console.WriteLine("Injected-color render " + name + ": active=" + HighContrastResources.GetIsActive(window)
                    + "; Window=" + background + "; WindowText=" + foreground
                    + "; Menu=" + Paint(menu.Foreground) + "; Combo=" + Paint(combo.Foreground));

                HighContrastResources.Refresh(false, windowBrush, windowTextBrush, Brushes.Navy, Brushes.Yellow);
                Pump();
                Check(!HighContrastResources.GetIsActive(window), name + ": active overlay flag was not restored", failures);
                Check(Paint((Brush)app.FindResource("MenuHoverBrush")) == ordinaryHover,
                    name + ": ordinary hover brush was not restored", failures);
                Check(Paint((Brush)app.FindResource("MenuHoverTextBrush")) == ordinaryText,
                    name + ": ordinary hover text was not restored", failures);
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            try { HighContrastResources.Refresh(false, Brushes.White, Brushes.Black, Brushes.Navy, Brushes.Yellow); }
            finally
            {
                try { window?.Close(); }
                finally { app.Shutdown(); }
            }
        }
    }

    private static Color Paint(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    private static void Check(bool condition, string failure, List<string> failures)
    {
        if (!condition) failures.Add(failure);
    }

    private static void Pump()
        => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

    private static void Capture(FrameworkElement element, string? directory, string name,
        Color foreground, List<string> failures)
    {
        element.UpdateLayout();
        // Render in control-local bounds so the parent row and margin do not clip the capture.
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element), null,
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),
            (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(directory, name + ".png"));
            encoder.Save(file);
        }
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        int textPixels = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
            if (pixels[offset + 3] >= 240
                && Math.Abs(pixels[offset] - foreground.B) <= 8
                && Math.Abs(pixels[offset + 1] - foreground.G) <= 8
                && Math.Abs(pixels[offset + 2] - foreground.R) <= 8) textPixels++;
        Console.WriteLine(name + ": injected text-color pixels=" + textPixels);
        Check(textPixels >= 4, name + ": no visible injected text-color pixels in the rendered control", failures);
    }

    // Set the real read-only highlight state through its protected setter. All templates,
    // resources, and foreground/background resolution remain the shipped production ones.
    private sealed class HighlightableMenuItem : MenuItem
    {
        internal void Highlight() => IsHighlighted = true;
    }

    private sealed class HighlightableComboBoxItem : ComboBoxItem
    {
        internal void Highlight() => IsHighlighted = true;
    }
}
