using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class DialogChromeTests
{
    [Theory]
    [InlineData(nameof(NoButtonShadowDarkScenario))]
    [InlineData(nameof(NoButtonShadowCyanoticScenario))]
    [InlineData(nameof(NoButtonShadowLightScenario))]
    public Task NoButtonStyleCastsAShadow(string scenario) => RuntimeComponentProcess.Run(scenario);
}

public static class NoButtonShadowDarkScenario
{
    public static void Run() => NoButtonShadowScenario.Run(Theme.Dark);
}

public static class NoButtonShadowCyanoticScenario
{
    public static void Run() => NoButtonShadowScenario.Run(Theme.Cyanotic);
}

public static class NoButtonShadowLightScenario
{
    public static void Run() => NoButtonShadowScenario.Run(Theme.Light);
}

public static class NoButtonShadowScenario
{
    public static void Run(Theme theme)
    {
        System.IO.Packaging.PackUriHelper.Create(new Uri("application:///"));
        var app = RuntimeComponentProcess.StartApplication();
        try
        {
            ThemeManager.Apply(theme);
            var offenders = new List<string>();
            foreach (var dictionary in app.Resources.MergedDictionaries)
                foreach (object key in dictionary.Keys)
                {
                    if (dictionary[key] is not Style style ||
                        !typeof(System.Windows.Controls.Primitives.ButtonBase).IsAssignableFrom(style.TargetType)) continue;
                    var button = (Control)Activator.CreateInstance(style.TargetType)!;
                    button.Style = style;
                    if (button is ContentControl content) content.Content = "Unlock";
                    button.Width = 120;
                    button.Height = 32;
                    var host = new Grid();
                    host.Children.Add(button);
                    host.Measure(new Size(200, 60));
                    host.Arrange(new Rect(0, 0, 200, 60));
                    host.UpdateLayout();
                    foreach (var element in new[] { (DependencyObject)button }.Concat(Descendants(button)).OfType<UIElement>())
                        if (element.Effect is System.Windows.Media.Effects.DropShadowEffect or System.Windows.Media.Effects.BlurEffect)
                            offenders.Add(theme + ": " + key + ": " + element.GetType().Name + "#" + (element as FrameworkElement)?.Name);
                }
            Assert.Empty(offenders);
        }
        finally { app.Shutdown(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
