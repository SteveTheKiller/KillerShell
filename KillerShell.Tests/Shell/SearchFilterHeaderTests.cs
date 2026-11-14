using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class SearchFilterHeaderTests
{
    [Fact]
    public Task FiltersHeadingAndAddActionRemainWholeWithoutOverlapAtNarrowAndNormalWidths()
        => RuntimeComponentProcess.Run(nameof(SearchFilterHeaderScenario));
}

public static class SearchFilterHeaderScenario
{
    public static void CaptureScope() => Exercise(captureOnly: true);
    public static void Run() => Exercise(captureOnly: false);

    private static void Exercise(bool captureOnly)
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        window.Opacity = 1;
        window.WindowState = WindowState.Normal;
        window.Left = SystemParameters.WorkArea.Left + 20;
        window.Top = SystemParameters.WorkArea.Top + 20;
        window.Activate();
        var view = new SearchFlyoutLayoutProbe(window);
        view.SetRows(1, 0);
        foreach (var locale in new[] { Locale.Fr, Locale.EnUS })
        {
            SearchFooterScreen.SwitchLocale(window, locale);
            foreach (bool narrow in new[] { true, false })
            {
                window.Width = narrow ? 600 : 1100;
                window.Height = narrow ? 520 : 800;
                window.ApplyAppScale(narrow ? 2.5 : 1);
                view.Open();
                view.Viewport.ScrollToTop();
                WaitForStableBounds(view);
                string state = locale + (narrow ? "-narrow" : "-normal");
                if (captureOnly)
                {
                    SearchFooterScreen.Capture(view.Root, "scope-top-" + state + ".png");
                    var scope = view.Named<Button>("SearchScopeButton");
                    var label = view.Named<TextBlock>("SearchScopeLabel");
                    var icon = view.Named<TextBlock>("SearchScopeIcon");
                    var chevron = SearchFlyoutLayoutProbe.Descendants<TextBlock>(scope)
                        .Single(text => text.Text == "\uE70D");
                    string directory = Environment.GetEnvironmentVariable("KILLERSHELL_TEST_EVIDENCE") ?? Path.GetTempPath();
                    File.WriteAllText(Path.Combine(directory, "scope-geometry.txt"),
                        "button=" + SearchFooterScreen.Bounds(scope) + Environment.NewLine
                        + "label=" + SearchFooterScreen.Bounds(label) + Environment.NewLine
                        + "icon=" + SearchFooterScreen.Bounds(icon) + Environment.NewLine
                        + "chevron=" + SearchFooterScreen.Bounds(chevron));
                    return;
                }
                var section = view.Named<FrameworkElement>("SearchFiltersSection");
                var heading = Assert.Single(SearchFlyoutLayoutProbe.Descendants<TextBlock>(section),
                    text => text.Text == MainWindow.LocStatic("Str_Lbl_Filters"));
                var add = Assert.Single(SearchFlyoutLayoutProbe.Descendants<Button>(section),
                    button => Equals(button.Content, MainWindow.LocStatic("Str_Btn_AddFilter")));
                heading.BringIntoView();
                add.BringIntoView();
                WaitForStableBounds(view);
                SearchFooterScreen.Capture(view.Root, "filter-header-" + state + ".png");
                var presenter = SearchFlyoutLayoutProbe.Descendants<ScrollContentPresenter>(view.Viewport)
                    .First(item => ReferenceEquals(item.TemplatedParent, view.Viewport));
                var text = new FormattedText(heading.Text, CultureInfo.CurrentUICulture, heading.FlowDirection,
                    new Typeface(heading.FontFamily, heading.FontStyle, heading.FontWeight, heading.FontStretch),
                    heading.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(heading).PixelsPerDip);
                var fullHeading = new Rect(heading.PointToScreen(new Point()),
                    heading.PointToScreen(new Point(text.WidthIncludingTrailingWhitespace, text.Height)));
                Rect clip = SearchFooterScreen.Bounds(presenter), buttonBounds = SearchFooterScreen.Bounds(add);
                Assert.True(heading.ActualWidth >= text.WidthIncludingTrailingWhitespace - 0.5,
                    state + "; heading width=" + heading.ActualWidth + "; full text width=" + text.WidthIncludingTrailingWhitespace);
                Assert.True(clip.Contains(fullHeading), state + "; heading=" + fullHeading + "; clip=" + clip);
                Assert.False(fullHeading.IntersectsWith(buttonBounds),
                    state + "; heading=" + fullHeading + "; add button=" + buttonBounds);
                Assert.True(clip.Contains(buttonBounds), state + "; add button must fit the viewport.");
                Assert.True(SearchFooterScreen.WorkArea(window).Contains(fullHeading));
                if (!narrow) Assert.Equal(404, view.Named<FrameworkElement>("TitleSearchForm").ActualWidth, 1);
                view.Close();
            }
        }
    }

    private static void WaitForStableBounds(SearchFlyoutLayoutProbe view)
    {
        Rect before = Rect.Empty;
        int stable = 0;
        ShellWindowFixture.PumpUntil(() =>
        {
            view.Refresh();
            Rect current = SearchFooterScreen.Bounds(view.Root);
            stable = current == before ? stable + 1 : 0;
            before = current;
            return stable >= 5;
        });
    }
}
