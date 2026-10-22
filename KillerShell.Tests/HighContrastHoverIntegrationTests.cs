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
using HoverRender = KillerShell.Tests.Terminal.InjectedHighContrastHoverScenario;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class InjectedHighContrastHoverTests
{
    [DisposableVmFact]
    [Trait("Category", "DisposableVm")]
    [Trait("Coverage", "InjectedColorRender")]
    public Task InjectedContrastColorsReachMenuAndBothComboHoverStates()
    {
        DisposableVmGuard.RequireDisposableVm();
        return RuntimeComponentProcess.Run(nameof(InjectedHighContrastHoverScenario));
    }

    [DisposableVmFact]
    [Trait("Category", "DisposableVm")]
    [Trait("Coverage", "AccentColorRender")]
    public Task AllSe98AccentsKeepSelectedAndUnselectedComboTextVisible()
    {
        DisposableVmGuard.RequireDisposableVm();
        return RuntimeComponentProcess.Run(nameof(Se98ComboHoverScenario));
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
            var selectedCombo = new HighlightableComboBoxItem
            {
                Content = "Highlighted selected combo item", IsSelected = true,
                Style = (Style)app.FindResource(typeof(ComboBoxItem)), Width = 360,
                Margin = new Thickness(12), UseLayoutRounding = true
            };
            var panel = new StackPanel();
            panel.Children.Add(menu);
            panel.Children.Add(combo);
            panel.Children.Add(selectedCombo);
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
                selectedCombo.Highlight();
                Pump();
                window.UpdateLayout();
                Assert.True(menu.IsHighlighted);
                Assert.True(combo.IsHighlighted);
                Assert.False(combo.IsSelected);
                Assert.True(selectedCombo.IsHighlighted);
                Assert.True(selectedCombo.IsSelected);
                var menuBorder = Assert.IsType<Border>(menu.Template.FindName("MenuBd", menu));
                var comboBorder = Assert.IsType<Border>(combo.Template.FindName("bg", combo));
                var selectedBorder = Assert.IsType<Border>(selectedCombo.Template.FindName("bg", selectedCombo));
                Check(Paint(menuBorder.Background) == background, name + ": menu hover background", failures);
                Check(Paint(comboBorder.Background) == background, name + ": combo hover background", failures);
                Check(Paint(selectedBorder.Background) == background, name + ": selected combo hover background", failures);
                Check(Paint(menu.Foreground) == foreground, name + ": menu hover foreground is " + Paint(menu.Foreground), failures);
                Check(Paint(combo.Foreground) == foreground, name + ": combo hover foreground is " + Paint(combo.Foreground), failures);
                Check(Paint(selectedCombo.Foreground) == foreground,
                    name + ": selected combo hover foreground is " + Paint(selectedCombo.Foreground), failures);
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
                Check(ColorContrast.Contrast(Paint(selectedCombo.Foreground), Paint(selectedBorder.Background)) >= 7,
                    name + ": rendered selected combo foreground/background contrast", failures);
                Capture(menu, evidence, "InjectedContrast-98SE-" + name + "-menu", foreground, failures);
                Capture(combo, evidence, "InjectedContrast-98SE-" + name + "-combo", foreground, failures);
                Capture(selectedCombo, evidence, "InjectedContrast-98SE-" + name + "-selected-combo", foreground, failures);
                Console.WriteLine("Injected-color render " + name + ": active=" + HighContrastResources.GetIsActive(window)
                    + "; Window=" + background + "; WindowText=" + foreground
                    + "; Menu=" + Paint(menu.Foreground) + "; Combo=" + Paint(combo.Foreground)
                    + "; SelectedCombo=" + Paint(selectedCombo.Foreground));

                selectedCombo.Highlight(false);
                combo.Highlight(false);
                Pump();
                Check(Paint(selectedCombo.Foreground) == Colors.Navy,
                    name + ": selection-only text did not retain the injected accent", failures);
                Check(Paint(selectedBorder.Background).A == 0,
                    name + ": selection-only row retained a hover fill", failures);
                Check(Paint(combo.Foreground) == foreground,
                    name + ": unselected rest text did not retain WindowText", failures);

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

    internal static Color Paint(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    internal static void Check(bool condition, string failure, List<string> failures)
    {
        if (!condition) failures.Add(failure);
    }

    internal static void Pump()
        => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

    internal static void Capture(FrameworkElement element, string? directory, string name,
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
        Console.WriteLine(name + ": text-color pixels=" + textPixels);
        Check(textPixels >= 4, name + ": no visible text-color pixels in the rendered control", failures);
    }

    // Set the real read-only highlight state through its protected setter. All templates,
    // resources, and foreground/background resolution remain the shipped production ones.
    private sealed class HighlightableMenuItem : MenuItem
    {
        internal void Highlight() => IsHighlighted = true;
    }

    internal sealed class HighlightableComboBoxItem : ComboBoxItem
    {
        internal void Highlight(bool value = true) => IsHighlighted = value;
    }
}

public static class Se98ComboHoverScenario
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
            var unselected = new HoverRender.HighlightableComboBoxItem
            {
                Content = "Unselected combo item", IsSelected = false,
                Style = (Style)app.FindResource(typeof(ComboBoxItem)), Width = 360,
                Margin = new Thickness(12), UseLayoutRounding = true
            };
            var selected = new HoverRender.HighlightableComboBoxItem
            {
                Content = "Selected combo item", IsSelected = true,
                Style = (Style)app.FindResource(typeof(ComboBoxItem)), Width = 360,
                Margin = new Thickness(12), UseLayoutRounding = true
            };
            var panel = new StackPanel();
            panel.Children.Add(unselected);
            panel.Children.Add(selected);
            window = new Window
            {
                Content = panel, SizeToContent = SizeToContent.WidthAndHeight,
                ShowActivated = false, ShowInTaskbar = false, Left = -32000, Top = -32000
            };
            window.Show();
            HighContrastResources.Refresh(false, Brushes.White, Brushes.Black, Brushes.Navy, Brushes.Yellow);
            ThemeManager.Apply(Theme.SE98);
            var failures = new List<string>();
            // Expected colors are the 98SE accent palette, independent of resource aliases.
            var palettes = new Dictionary<Accent, (string Primary, string Hover, string Text)>
            {
                [Accent.Green] = ("#004F00", "#004F00", "#FFFFFF"),
                [Accent.Red] = ("#800000", "#800000", "#FFFFFF"),
                [Accent.Blue] = ("#000080", "#000080", "#FFFFFF"),
                [Accent.Purple] = ("#4B286D", "#4B286D", "#FFFFFF"),
                [Accent.Orange] = ("#7D3E00", "#7D3E00", "#FFFFFF"),
                [Accent.Teal] = ("#005858", "#005858", "#FFFFFF"),
                [Accent.Yellow] = ("#2B2A24", "#FFD94D", "#24231C"),
                [Accent.Magenta] = ("#750052", "#750052", "#FFFFFF")
            };
            Assert.Equal(palettes.Count, Enum.GetValues(typeof(Accent)).Length);
            foreach (Accent accent in Enum.GetValues(typeof(Accent)))
            {
                ThemeManager.ApplyAccent(Theme.SE98, accent);
                HoverRender.Pump();
                window.UpdateLayout();
                Assert.False(HighContrastResources.GetIsActive(window));
                var palette = palettes[accent];
                Color primary = (Color)ColorConverter.ConvertFromString(palette.Primary);
                Color fill = (Color)ColorConverter.ConvertFromString(palette.Hover);
                Color foreground = (Color)ColorConverter.ConvertFromString(palette.Text);
                var selectedBorder = Assert.IsType<Border>(selected.Template.FindName("bg", selected));
                var unselectedBorder = Assert.IsType<Border>(unselected.Template.FindName("bg", unselected));

                void CheckRest(string phase)
                {
                    Assert.True(selected.IsSelected);
                    Assert.False(unselected.IsSelected);
                    Assert.False(selected.IsHighlighted);
                    Assert.False(unselected.IsHighlighted);
                    HoverRender.Check(HoverRender.Paint(selected.Foreground) == primary,
                        accent + " " + phase + ": selection-only text changed", failures);
                    HoverRender.Check(HoverRender.Paint(unselected.Foreground) == Colors.Black,
                        accent + " " + phase + ": unselected rest text changed", failures);
                    HoverRender.Check(HoverRender.Paint(selectedBorder.Background).A == 0
                        && HoverRender.Paint(unselectedBorder.Background).A == 0,
                        accent + " " + phase + ": resting rows must not have a hover fill", failures);
                }

                CheckRest("before hover");
                selected.Highlight();
                unselected.Highlight();
                HoverRender.Pump();
                window.UpdateLayout();
                Assert.True(selected.IsSelected);
                Assert.False(unselected.IsSelected);
                foreach (var item in new[] { unselected, selected })
                {
                    string state = item.IsSelected ? "selected" : "unselected";
                    Assert.True(item.IsHighlighted);
                    var border = Assert.IsType<Border>(item.Template.FindName("bg", item));
                    HoverRender.Check(HoverRender.Paint(border.Background) == fill,
                        accent + " " + state + ": hover fill does not follow the accent", failures);
                    HoverRender.Check(HoverRender.Paint(item.Foreground) == foreground,
                        accent + " " + state + ": highlighted text is " + HoverRender.Paint(item.Foreground), failures);
                    HoverRender.Check(ColorContrast.Contrast(HoverRender.Paint(item.Foreground),
                        HoverRender.Paint(border.Background)) >= 4.5,
                        accent + " " + state + ": highlighted text has insufficient contrast", failures);
                    HoverRender.Capture(item, evidence, "SE98-" + accent + "-" + state + "-combo-hover",
                        foreground, failures);
                }
                selected.Highlight(false);
                unselected.Highlight(false);
                HoverRender.Pump();
                CheckRest("after hover");
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally
        {
            try { window?.Close(); }
            finally { app.Shutdown(); }
        }
    }
}
