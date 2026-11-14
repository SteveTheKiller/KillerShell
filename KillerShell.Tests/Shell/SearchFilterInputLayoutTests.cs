using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KillerShell.Models;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class SearchFilterInputLayoutTests
{
    [Fact]
    public Task FilterValuesAndCaretRemainVisibleAndReachableAtNarrowAndNormalWidths()
        => RuntimeComponentProcess.Run(nameof(SearchFilterInputLayoutScenario), 180000);
}

public static class SearchFilterInputLayoutScenario
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
        view.Open();
        var add = view.Named<Button>("SearchAddFilterButton");
        Ready(view, add, "Add filter");
        Pattern<IInvokeProvider>(add, PatternInterface.Invoke).Invoke();
        ShellWindowFixture.PumpUntil(() =>
        {
            view.Refresh();
            return Frames(view).Count() == 1;
        }, diagnostic: () => "The real Add filter action must create one rendered filter frame.");
        view.Close();

        foreach (var locale in new[] { Locale.Fr, Locale.Bn })
        {
            SearchFooterScreen.SwitchLocale(window, locale);
            foreach (bool narrow in new[] { true, false })
            {
                window.Width = narrow ? 600 : Math.Min(1100, SystemParameters.WorkArea.Width - 40);
                window.Height = narrow ? 520 : Math.Min(800, SystemParameters.WorkArea.Height - 40);
                window.ApplyAppScale(narrow ? 2.5 : 1);
                view.Open();
                foreach (string resource in new[] { "Str_Filter_Ext", "Str_Filter_Date", "Str_Filter_Size" })
                {
                    string state = locale + (narrow ? "-narrow-" : "-normal-") + resource;
                    var frame = Assert.Single(Frames(view));
                    string label = (string)window.FindResource(resource);
                    var selector = Assert.Single(SearchFlyoutLayoutProbe.Descendants<ComboBox>(frame),
                        box => box.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Content, label)));
                    selector.BringIntoView();
                    Ready(view, selector, state + "; field selector");
                    Pattern<IExpandCollapseProvider>(selector, PatternInterface.ExpandCollapse).Expand();
                    var option = Assert.Single(selector.Items.OfType<ComboBoxItem>(), item => Equals(item.Content, label));
                    ShellWindowFixture.PumpUntil(() => selector.IsDropDownOpen && option.IsVisible && option.IsEnabled,
                        diagnostic: () => state + "; localized field item must be visible and enabled.");
                    SelectItem(selector, option);
                    Pattern<IExpandCollapseProvider>(selector, PatternInterface.ExpandCollapse).Collapse();
                    ShellWindowFixture.PumpUntil(() => !selector.IsDropDownOpen && ReferenceEquals(selector.SelectedItem, option));

                    bool isDate = resource == "Str_Filter_Date", isSize = resource == "Str_Filter_Size";
                    DatePicker? date = null;
                    TextBox? input = null;
                    ShellWindowFixture.PumpUntil(() =>
                    {
                        view.Refresh();
                        date = SearchFlyoutLayoutProbe.Descendants<DatePicker>(frame).SingleOrDefault(item => item.IsVisible);
                        input = isDate
                            ? (date == null ? null : SearchFlyoutLayoutProbe.Descendants<DatePickerTextBox>(date).SingleOrDefault(item => item.IsVisible))
                            : SearchFlyoutLayoutProbe.Descendants<TextBox>(frame).SingleOrDefault(item => item.IsVisible);
                        return input != null && input.IsLoaded && input.IsEnabled && (!isDate || date != null);
                    }, diagnostic: () => state + "; selected field must expose its actual value input.");
                    var field = input!;
                    frame.BringIntoView();
                    view.Refresh();
                    if (!SearchFooterScreen.Bounds(Presenter(view)).Contains(SearchFooterScreen.Bounds(field)))
                        field.BringIntoView();
                    string expected = isDate ? "12/12/2026" : isSize ? "12" : "txt;log";
                    if (isDate)
                    {
                        Ready(view, date!, state + "; date picker");
                        Pattern<IValueProvider>(date!, PatternInterface.Value).SetValue(expected);
                        ShellWindowFixture.PumpUntil(() => date!.SelectedDate == new DateTime(2026, 12, 12)
                            && field.Text == date.Text && field.Text.Length == 10,
                            diagnostic: () => state + "; date=" + date!.SelectedDate + "; displayed=" + field.Text);
                        expected = field.Text;
                    }
                    else
                    {
                        Pattern<IValueProvider>(field, PatternInterface.Value).SetValue(expected);
                        ShellWindowFixture.PumpUntil(() => field.Text == expected);
                    }
                    Assert.True(field.Focus(), state + "; value field must accept focus.");
                    field.Select(field.Text.Length, 0);
                    ComboBox? unit = isSize ? Assert.Single(SearchFlyoutLayoutProbe.Descendants<ComboBox>(frame),
                        box => !ReferenceEquals(box, selector) && box.IsVisible) : null;
                    if (unit != null) Ready(view, unit, state + "; size unit selector");
                    WaitForStableSnapshot(window, view, frame, field, date, unit, state);
                    if (narrow)
                    {
                        SearchTermInputScreen.ParkPointer(window, view.Root);
                        var settled = Stopwatch.StartNew();
                        ShellWindowFixture.PumpUntil(() => settled.ElapsedMilliseconds >= 250);
                        SearchFooterScreen.Capture(view.Root, "filter-" + state + ".png");
                        string directory = Environment.GetEnvironmentVariable("KILLERSHELL_TEST_EVIDENCE") ?? Path.GetTempPath();
                        File.WriteAllText(Path.Combine(directory, "filter-" + state + ".txt"),
                            "field=" + SearchFooterScreen.Bounds(field) + Environment.NewLine
                            + "frame=" + SearchFooterScreen.Bounds(frame) + Environment.NewLine
                            + "padded=" + SearchFooterScreen.Bounds((FrameworkElement)frame.Child) + Environment.NewLine
                            + "clip=" + SearchFooterScreen.Bounds(Presenter(view)) + Environment.NewLine
                            + "text=" + field.Text);
                    }
                    Assert.Equal(expected, field.Text);
                    Assert.True(field.IsKeyboardFocusWithin, state + "; actual value input must keep focus.");
                    Assert.Equal(expected.Length, field.CaretIndex);
                    if (isDate) Assert.Equal(new DateTime(2026, 12, 12), date!.SelectedDate);

                    var presenter = Presenter(view);
                    Rect clip = Rect.Intersect(SearchFooterScreen.Bounds(presenter), SearchFooterScreen.WorkArea(window));
                    Rect fieldBounds = SearchFooterScreen.Bounds(field);
                    for (int index = 0; index < field.Text.Length; index++)
                    {
                        Rect leading = field.GetRectFromCharacterIndex(index, false);
                        Rect trailing = field.GetRectFromCharacterIndex(index, true);
                        Assert.False(leading.IsEmpty || trailing.IsEmpty, state + "; actual character layout missing at " + index);
                        AssertInside(ToScreen(field, Rect.Union(leading, trailing)), fieldBounds, clip, state + "; character " + index);
                    }
                    Rect caret = field.GetRectFromCharacterIndex(field.CaretIndex);
                    Assert.False(caret.IsEmpty, state + "; actual caret layout missing.");
                    AssertInside(ToScreen(field, caret), fieldBounds, clip, state + "; caret");
                    AssertCenterHit(view, field, clip, state);
                    Rect padded = SearchFooterScreen.Bounds(Assert.IsAssignableFrom<FrameworkElement>(frame.Child));
                    var outerControls = SearchFlyoutLayoutProbe.Descendants<Control>(frame)
                        .Where(control => control.IsVisible && !HasControlAncestorInside(control, frame)).ToArray();
                    foreach (var control in outerControls)
                        AssertInside(SearchFooterScreen.Bounds(control), padded, clip, state + "; outer " + control.GetType().Name);
                    if (unit != null)
                    {
                        AssertCenterHit(view, unit, clip, state + "; size units");
                        Pattern<IExpandCollapseProvider>(unit, PatternInterface.ExpandCollapse).Expand();
                        var mb = Assert.Single(unit.Items.OfType<ComboBoxItem>(), item => Equals(item.Content, "MB"));
                        ShellWindowFixture.PumpUntil(() => unit.IsDropDownOpen && mb.IsVisible && mb.IsEnabled);
                        SelectItem(unit, mb);
                        Pattern<IExpandCollapseProvider>(unit, PatternInterface.ExpandCollapse).Collapse();
                        ShellWindowFixture.PumpUntil(() => !unit.IsDropDownOpen && ReferenceEquals(unit.SelectedItem, mb));
                        AssertCenterHit(view, unit, clip, state + "; selected size units");
                    }
                }
                if (!narrow) Assert.Equal(404, view.Named<FrameworkElement>("TitleSearchForm").ActualWidth, 1);
                view.Close();
            }
        }
    }

    private static IEnumerable<Border> Frames(SearchFlyoutLayoutProbe view)
        => SearchFlyoutLayoutProbe.Descendants<Border>(view.Filters)
            .Where(border => border.DataContext is SearchFilter && border.BorderThickness.Left > 0 && border.Padding.Left > 0
                && SearchFlyoutLayoutProbe.Descendants<DatePicker>(border).Any()
                && SearchFlyoutLayoutProbe.Descendants<ComboBox>(border).Any(box => box.Items.Count == 3));

    private static T Pattern<T>(UIElement element, PatternInterface pattern) where T : class
        => Assert.IsAssignableFrom<T>(UIElementAutomationPeer.CreatePeerForElement(element).GetPattern(pattern));

    private static void SelectItem(ComboBox selector, ComboBoxItem option)
    {
        var owner = UIElementAutomationPeer.CreatePeerForElement(selector);
        var item = Assert.Single(owner.GetChildren(), peer => peer.GetName() == (string)option.Content);
        var provider = Assert.IsAssignableFrom<ISelectionItemProvider>(item.GetPattern(PatternInterface.SelectionItem));
        string directory = Environment.GetEnvironmentVariable("KILLERSHELL_TEST_EVIDENCE") ?? Path.GetTempPath();
        File.AppendAllText(Path.Combine(directory, "selection-providers.txt"),
            "owner=" + owner.GetType().FullName + "; wrapper=" + UIElementAutomationPeer.CreatePeerForElement(option).GetType().FullName
            + "; item=" + item.GetType().FullName + "; SelectionItem=" + provider.GetType().FullName
            + "; label=" + item.GetName() + Environment.NewLine);
        provider.Select();
    }

    private static void Ready(SearchFlyoutLayoutProbe view, FrameworkElement control, string state)
        => ShellWindowFixture.PumpUntil(() =>
        {
            view.Refresh();
            return control.IsLoaded && control.IsVisible && control.IsEnabled && control.ActualWidth > 0 && control.ActualHeight > 0;
        }, diagnostic: () => state + "; loaded=" + control.IsLoaded + "; visible=" + control.IsVisible
            + "; enabled=" + control.IsEnabled + "; size=" + control.RenderSize);

    private static ScrollContentPresenter Presenter(SearchFlyoutLayoutProbe view)
        => SearchFlyoutLayoutProbe.Descendants<ScrollContentPresenter>(view.Viewport)
            .First(item => ReferenceEquals(item.TemplatedParent, view.Viewport));

    private static void WaitForStableSnapshot(Window window, SearchFlyoutLayoutProbe view, Border frame,
        TextBox input, DatePicker? date, ComboBox? unit, string state)
    {
        object[]? previous = null;
        int matches = 0;
        var interval = Stopwatch.StartNew();
        ShellWindowFixture.PumpUntil(() =>
        {
            view.Refresh();
            var snapshot = new List<object>
            {
                SearchFooterScreen.Bounds(window), SearchFooterScreen.Bounds(view.Root),
                view.Root.LayoutTransform.Value, SearchFooterScreen.Bounds(Presenter(view)),
                SearchFooterScreen.Bounds(frame), SearchFooterScreen.Bounds((FrameworkElement)frame.Child),
                SearchFooterScreen.Bounds(input), input.Text, input.CaretIndex, input.IsKeyboardFocusWithin,
                view.Viewport.VerticalOffset, view.Viewport.HorizontalOffset,
                new Size(view.Viewport.ViewportWidth, view.Viewport.ViewportHeight),
                new Size(view.Viewport.ExtentWidth, view.Viewport.ExtentHeight),
                input.GetRectFromCharacterIndex(input.CaretIndex)
            };
            foreach (var control in SearchFlyoutLayoutProbe.Descendants<Control>(frame).Where(item => item.IsVisible))
                snapshot.Add(SearchFooterScreen.Bounds(control));
            for (int i = 0; i < input.Text.Length; i++)
            {
                snapshot.Add(input.GetRectFromCharacterIndex(i, false));
                snapshot.Add(input.GetRectFromCharacterIndex(i, true));
            }
            if (date != null) { snapshot.Add(date.Text); snapshot.Add(date.SelectedDate ?? DateTime.MinValue); }
            if (unit != null) { snapshot.Add(SearchFooterScreen.Bounds(unit)); snapshot.Add(unit.SelectedIndex); }
            object[] current = snapshot.ToArray();
            if (previous != null && current.SequenceEqual(previous)) matches++;
            else { matches = 1; interval.Restart(); }
            previous = current;
            return matches >= 2 && interval.ElapsedMilliseconds >= 250;
        }, diagnostic: () => state + "; public window, viewport, row, value and character geometry did not stabilize.");
    }

    private static Rect ToScreen(FrameworkElement control, Rect rectangle)
        => new Rect(control.PointToScreen(rectangle.TopLeft), control.PointToScreen(rectangle.BottomRight));

    private static void AssertInside(Rect actual, Rect container, Rect clip, string state)
    {
        Assert.True(!actual.IsEmpty && actual.Width >= 0 && actual.Height > 0, state + "; bounds=" + actual);
        Assert.True(container.Contains(actual), state + "; bounds=" + actual + "; container=" + container);
        Assert.True(clip.Contains(actual), state + "; bounds=" + actual + "; visible clip=" + clip);
    }

    private static void AssertCenterHit(SearchFlyoutLayoutProbe view, FrameworkElement control, Rect clip, string state)
    {
        Rect bounds = SearchFooterScreen.Bounds(control);
        Assert.True(clip.Contains(bounds), state + "; target must remain visible.");
        var center = new Point(Math.Round(bounds.Left + bounds.Width / 2), Math.Round(bounds.Top + bounds.Height / 2));
        var hit = view.Root.InputHitTest(view.Root.PointFromScreen(center)) as DependencyObject;
        for (var current = hit; current != null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, control)) return;
        Assert.Fail(state + "; center must hit the actual " + control.GetType().Name + ".");
    }

    private static bool HasControlAncestorInside(DependencyObject control, Border frame)
    {
        for (var parent = VisualTreeHelper.GetParent(control); parent != null && !ReferenceEquals(parent, frame);
            parent = VisualTreeHelper.GetParent(parent))
            if (parent is Control) return true;
        return false;
    }
}
