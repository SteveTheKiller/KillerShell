using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class SearchScopeAccessibilityTests
{
    [Fact]
    public Task ScopeIconFullLabelAndChevronFitAndExposeTheActualScopeAtNarrowAndNormalWidths()
        => RuntimeComponentProcess.Run(nameof(SearchScopeAccessibilityScenario));
}

public static class SearchScopeAccessibilityScenario
{
    public static void Run()
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
                var button = view.Named<Button>("SearchScopeButton");
                var label = view.Named<TextBlock>("SearchScopeLabel");
                var icon = view.Named<TextBlock>("SearchScopeIcon");
                var chevron = Assert.Single(SearchFlyoutLayoutProbe.Descendants<TextBlock>(button),
                    text => text.Text == "\uE70D");
                var peer = UIElementAutomationPeer.CreatePeerForElement(button);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                ShellWindowFixture.PumpUntil(() => button.ContextMenu?.IsOpen == true);
                Assert.True(KillerShell.Controls.CardMenus.Buttons(button.ContextMenu).Count() >= 3, "The actual scope chooser must open.");
                button.ContextMenu.IsOpen = false;
                view.Viewport.ScrollToTop();
                Rect previous = Rect.Empty;
                int stable = 0;
                ShellWindowFixture.PumpUntil(() =>
                {
                    view.Refresh();
                    Rect bounds = SearchFooterScreen.Bounds(button);
                    stable = previous == bounds ? stable + 1 : 0;
                    previous = bounds;
                    return stable >= 5;
                });
                string state = locale + (narrow ? "-narrow" : "-normal");
                SearchFooterScreen.Capture(view.Root, "scope-" + state + ".png");
                var presenter = SearchFlyoutLayoutProbe.Descendants<ScrollContentPresenter>(view.Viewport)
                    .First(item => ReferenceEquals(item.TemplatedParent, view.Viewport));
                Rect buttonBounds = SearchFooterScreen.Bounds(button), clip = SearchFooterScreen.Bounds(presenter);
                Rect iconBounds = SearchFooterScreen.Bounds(icon), labelBounds = SearchFooterScreen.Bounds(label),
                    chevronBounds = SearchFooterScreen.Bounds(chevron), work = SearchFooterScreen.WorkArea(window);
                foreach (var part in new[] { iconBounds, labelBounds, chevronBounds })
                {
                    Assert.True(buttonBounds.Contains(part), state + "; part=" + part + "; button=" + buttonBounds);
                    Assert.True(clip.Contains(part), state + "; part must fit the viewport.");
                    Assert.True(work.Contains(part), state + "; part must fit the monitor work area.");
                }
                Assert.False(iconBounds.IntersectsWith(labelBounds), state + "; icon overlaps label.");
                Assert.False(labelBounds.IntersectsWith(chevronBounds), state + "; label overlaps chevron.");
                Assert.Equal(TextTrimming.None, label.TextTrimming);
                Assert.Equal(label.Text, peer.GetName());
                if (!narrow) Assert.Equal(404, view.Named<FrameworkElement>("TitleSearchForm").ActualWidth, 1);
                view.Close();
            }
        }
    }
}

