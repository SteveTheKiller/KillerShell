using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShell.Services;
using KillerShell.Tests.Safety;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class LightElevatedTagContrastTests
{
    [DisposableVmFact]
    [Trait("Category", "DisposableVm")]
    [Trait("Coverage", "RuntimeThemeResources")]
    public Task EveryLightAccentKeepsTheElevatedTagReadableWithoutChangingPrimary()
    {
        DisposableVmGuard.RequireDisposableVm();
        return RuntimeComponentProcess.Run(nameof(LightElevatedTagContrastScenario));
    }
}

public static class LightElevatedTagContrastScenario
{
    public static void Run()
    {
        DisposableVmGuard.RequireDisposableVm();
        var app = RuntimeComponentProcess.StartApplication();
        try
        {
            var primaryColors = new Dictionary<Accent, string>
            {
                [Accent.Green] = "#1B5E20", [Accent.Red] = "#931A1A",
                [Accent.Blue] = "#18608E", [Accent.Purple] = "#5A1690",
                [Accent.Orange] = "#E8962C", [Accent.Teal] = "#0B6C69",
                [Accent.Yellow] = "#F2C500", [Accent.Magenta] = "#A60070"
            };
            var accents = (Accent[])Enum.GetValues(typeof(Accent));
            Assert.Equal(primaryColors.Count, accents.Length);
            var expectedTitleBar = Color.FromRgb(0xCC, 0xCC, 0xCC);
            var firstTagColors = new Dictionary<Accent, Color>();
            var failures = new List<string>();
            ThemeManager.Apply(Theme.Light);

            // Reverse the second pass so aliases must follow different preceding accents.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int index = 0; index < accents.Length; index++)
                {
                    Accent accent = accents[pass == 0 ? index : accents.Length - 1 - index];
                    Color expectedPrimary = (Color)ColorConverter.ConvertFromString(primaryColors[accent]);

                    void CheckResources(string phase)
                    {
                        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                        var titleBar = Assert.IsType<SolidColorBrush>(app.FindResource("TitleBarBrush"));
                        var primary = Assert.IsType<SolidColorBrush>(app.FindResource("PrimaryBrush"));
                        var elevated = Assert.IsType<SolidColorBrush>(app.FindResource("ElevatedTagBrush"));
                        string state = "Light " + accent + " pass " + pass + " " + phase;
                        Check(titleBar.Color == expectedTitleBar, state + ": title bar changed from #CCCCCC", failures);
                        Check(primary.Color == expectedPrimary, state + ": Primary palette changed", failures);
                        Check(titleBar.Opacity == 1 && primary.Opacity == 1 && elevated.Opacity == 1
                            && titleBar.Color.A == 255 && primary.Color.A == 255 && elevated.Color.A == 255,
                            state + ": contrast requires opaque brushes", failures);
                        double contrast = ColorContrast.Contrast(elevated.Color, titleBar.Color);
                        Check(contrast >= 4.5, state + ": ElevatedTag contrast is " + contrast
                            + " for " + elevated.Color + " on " + titleBar.Color, failures);

                        // Already readable accents must keep their color. The others are judged
                        // by the visible contrast requirement, not the adjustment algorithm.
                        if (ColorContrast.Contrast(expectedPrimary, expectedTitleBar) >= 4.5)
                            Check(elevated.Color == expectedPrimary,
                                state + ": an already readable accent was changed or became stale", failures);
                        if (firstTagColors.TryGetValue(accent, out Color first))
                            Check(elevated.Color == first, state + ": reapplying the accent changed its label color", failures);
                        else
                            firstTagColors.Add(accent, elevated.Color);
                        Console.WriteLine(state + ": TitleBar=" + titleBar.Color + "; Primary=" + primary.Color
                            + "; ElevatedTag=" + elevated.Color + "; Contrast=" + contrast);
                    }

                    ThemeManager.ApplyAccent(Theme.Light, accent);
                    CheckResources("accent switch");
                    ThemeManager.Apply(Theme.Light);
                    CheckResources("repeat apply");
                    ThemeManager.Apply(Theme.Dark);
                    ThemeManager.Apply(Theme.Light);
                    CheckResources("theme round trip");
                }
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally { app.Shutdown(); }
    }

    private static void Check(bool condition, string failure, List<string> failures)
    {
        if (!condition) failures.Add(failure);
    }
}
