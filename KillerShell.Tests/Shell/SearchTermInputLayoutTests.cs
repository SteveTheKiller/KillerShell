using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using KillerShell.Models;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class SearchTermInputLayoutTests
{
    [Fact]
    public Task LoadedSearchTermsAcceptVisibleTextAndCaretAtNarrowAndNormalWidths()
        => RuntimeComponentProcess.Run(nameof(SearchTermInputLayoutScenario), 180000);
}

public static class SearchTermInputLayoutScenario
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
        var tab = window.Pane.Active;
        tab.Groups.Clear();
        for (int groupIndex = 0; groupIndex < 2; groupIndex++)
        {
            var restored = new TermGroup();
            for (int termIndex = 0; termIndex < 2; termIndex++)
                restored.Terms.Add(new SearchTerm { Pattern = "sample", MatchCount = 1 });
            tab.Groups.Add(restored);
        }
        string expectedFile = Path.Combine(fixture.Root, "needle.txt");
        File.WriteAllText(expectedFile, "needle");
        var view = new SearchFlyoutLayoutProbe(window);
        foreach (var locale in new[] { Locale.Fr, Locale.Bn })
        {
            SearchFooterScreen.SwitchLocale(window, locale);
            foreach (bool narrow in new[] { true, false })
            {
                window.Width = Math.Min(narrow ? 600 : 1100, SystemParameters.WorkArea.Width - 40);
                window.Height = Math.Min(narrow ? 520 : 800, SystemParameters.WorkArea.Height - 40);
                window.ApplyAppScale(narrow ? 2.5 : 1);
                view.Open();
                ShellWindowFixture.PumpUntil(() => { view.Refresh(); return SearchFlyoutLayoutProbe.Descendants<TextBox>(view.Terms).Count() == 4; });
                var fields = SearchFlyoutLayoutProbe.Descendants<TextBox>(view.Terms).ToArray();
                for (int fieldIndex = 0; fieldIndex < fields.Length; fieldIndex++)
                {
                    var field = fields[fieldIndex];
                    ((IValueProvider)UIElementAutomationPeer.CreatePeerForElement(field).GetPattern(PatternInterface.Value)).SetValue("needle");
                    field.BringIntoView();
                    field.Focus();
                    field.Select(field.Text.Length, 0);
                    (Rect Field, Rect Root, Rect Window, Matrix Scale, double Offset, Size Viewport, Size Extent)? previous = null;
                    int stable = 0;
                    var stableInterval = Stopwatch.StartNew();
                    ShellWindowFixture.PumpUntil(() =>
                    {
                        view.Refresh();
                        var current = (Field: SearchFooterScreen.Bounds(field), Root: SearchFooterScreen.Bounds(view.Root),
                            Window: SearchFooterScreen.Bounds(window), Scale: view.Root.LayoutTransform.Value,
                            Offset: view.Viewport.VerticalOffset, Viewport: new Size(view.Viewport.ViewportWidth, view.Viewport.ViewportHeight),
                            Extent: new Size(view.Viewport.ExtentWidth, view.Viewport.ExtentHeight));
                        if (previous.HasValue && current.Equals(previous.Value)) stable++;
                        else { stable = 1; stableInterval.Restart(); }
                        previous = current;
                        return stable >= 2 && stableInterval.ElapsedMilliseconds >= 250;
                    });
                    string state = locale + (narrow ? "-narrow" : "-normal") + "-term" + fieldIndex;
                    SearchTermInputScreen.ParkPointer(window, view.Root);
                    var settled = Stopwatch.StartNew();
                    ShellWindowFixture.PumpUntil(() => settled.ElapsedMilliseconds >= 250);
                    SearchFooterScreen.Capture(view.Root, "input-" + state + ".png");
                    var row = (Grid)field.Parent;
                    var mode = Assert.Single(SearchFlyoutLayoutProbe.Descendants<Button>(row), button => !Equals(button.Content, "\uE8BB"));
                    var remove = Assert.Single(SearchFlyoutLayoutProbe.Descendants<Button>(row), button => Equals(button.Content, "\uE8BB"));
                    var badge = Assert.Single(row.Children.OfType<TextBlock>());
                    Border? frame = null;
                    for (var current = VisualTreeHelper.GetParent(row); current != null; current = VisualTreeHelper.GetParent(current))
                        if (current is Border border && border.DataContext is TermGroup && border.BorderThickness.Left > 0)
                        { frame = border; break; }
                    Assert.NotNull(frame);
                    string directory = Environment.GetEnvironmentVariable("KILLERSHELL_TEST_EVIDENCE") ?? Path.GetTempPath();
                    File.WriteAllText(Path.Combine(directory, "row-" + state + ".txt"),
                        "mode=" + SearchFooterScreen.Bounds(mode) + Environment.NewLine
                        + "badge=" + SearchFooterScreen.Bounds(badge) + Environment.NewLine
                        + "remove=" + SearchFooterScreen.Bounds(remove) + Environment.NewLine
                        + "input=" + SearchFooterScreen.Bounds(field) + Environment.NewLine
                        + "frame=" + SearchFooterScreen.Bounds(frame!) + Environment.NewLine
                        + "frameContent=" + SearchFooterScreen.Bounds((FrameworkElement)frame!.Child));
                    Rect paddedContent = SearchFooterScreen.Bounds((FrameworkElement)frame!.Child);
                    foreach (var part in new FrameworkElement[] { mode, remove, field })
                        Assert.True(paddedContent.Contains(SearchFooterScreen.Bounds(part)),
                            state + "; part=" + part.GetType().Name + "; bounds=" + SearchFooterScreen.Bounds(part)
                            + "; padded group content=" + paddedContent);
                    if (!string.IsNullOrEmpty(badge.Text))
                        Assert.True(paddedContent.Contains(SearchFooterScreen.Bounds(badge)),
                            state + "; nonempty badge must fit padded group content.");
                    Assert.Equal("needle", field.Text);
                    var presenter = SearchFlyoutLayoutProbe.Descendants<ScrollContentPresenter>(view.Viewport)
                        .First(item => ReferenceEquals(item.TemplatedParent, view.Viewport));
                    Rect fieldBounds = SearchFooterScreen.Bounds(field), clip = SearchFooterScreen.Bounds(presenter);
                    for (int index = 0; index < field.Text.Length; index++)
                    {
                        Rect leading = field.GetRectFromCharacterIndex(index, false), trailing = field.GetRectFromCharacterIndex(index, true);
                        Assert.False(leading.IsEmpty || trailing.IsEmpty, state + "; actual character layout missing; field width=" + field.ActualWidth);
                        Rect character = Rect.Union(leading, trailing);
                        Rect screen = SearchTermInputScreen.ToScreen(field, character);
                        Assert.True(fieldBounds.Contains(screen), state + "; character=" + screen + "; field=" + fieldBounds);
                        Assert.True(clip.Contains(screen), state + "; actual character must fit the viewport.");
                    }
                    Rect caret = field.GetRectFromCharacterIndex(field.CaretIndex);
                    Assert.False(caret.IsEmpty, state + "; actual caret layout must exist.");
                    Rect caretScreen = SearchTermInputScreen.ToScreen(field, caret);
                    Assert.True(fieldBounds.Contains(caretScreen));
                    Assert.True(clip.Contains(caretScreen));
                    Point center = new Point(Math.Round(fieldBounds.Left + fieldBounds.Width / 2), Math.Round(fieldBounds.Top + fieldBounds.Height / 2));
                    var hit = view.Root.InputHitTest(view.Root.PointFromScreen(center)) as DependencyObject;
                    bool intended = false;
                    for (var current = hit; current != null; current = VisualTreeHelper.GetParent(current))
                        if (ReferenceEquals(current, field)) { intended = true; break; }
                    Assert.True(intended, state + "; center must hit the actual term input.");
                }
                if (!narrow) Assert.Equal(404, view.Named<FrameworkElement>("TitleSearchForm").ActualWidth, 1);
                view.Close();
            }
        }
        view.Open();
        var search = view.Named<Button>("SearchButton");
        var clear = Assert.Single(SearchFlyoutLayoutProbe.Descendants<Button>((Grid)search.Parent),
            button => Equals(button.Content, window.FindResource("Str_Btn_Clear")));
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(clear).GetPattern(PatternInterface.Invoke)).Invoke();
        ShellWindowFixture.PumpUntil(() => tab.Results.Count == 0);
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(search).GetPattern(PatternInterface.Invoke)).Invoke();
        ShellWindowFixture.PumpUntil(() => tab.HasSearch && !tab.IsSearching && tab.Results.Any(result => result.FilePath == expectedFile));
        view.Close();
    }
}

internal static class SearchTermInputScreen
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }

    internal static Rect ToScreen(FrameworkElement element, Rect rectangle)
        => new Rect(element.PointToScreen(rectangle.TopLeft), element.PointToScreen(rectangle.BottomRight));

    internal static void ParkPointer(Window window, FrameworkElement popup)
    {
        FrameworkElement title = (FrameworkElement)window.FindName("TitleIcon");
        while (title is not Grid) title = (FrameworkElement)title.Parent;
        Rect popupBounds = SearchFooterScreen.Bounds(popup);
        IntPtr owner = new WindowInteropHelper(window).Handle;
        foreach (double y in new[] { title.ActualHeight / 4, title.ActualHeight / 2, title.ActualHeight * 3 / 4 })
        for (double x = 8; x < title.ActualWidth - 8; x += 8)
        {
            Point screen = title.PointToScreen(new Point(x, y));
            var point = new NativePoint { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
            var rounded = new Point(point.X, point.Y);
            if (popupBounds.Contains(rounded) || WindowFromPoint(point) != owner) continue;
            var hit = window.InputHitTest(window.PointFromScreen(rounded)) as DependencyObject;
            if (hit is not Grid && hit is not Border) continue;
            bool interactive = false;
            for (var ancestor = hit; ancestor != null; ancestor = VisualTreeHelper.GetParent(ancestor))
                if (ancestor is ButtonBase || ancestor is TextBoxBase || ancestor is ComboBox) { interactive = true; break; }
            if (interactive) continue;
            Assert.True(SetCursorPos(point.X, point.Y));
            Mouse.Synchronize();
            return;
        }
        Assert.Fail("No verified noninteractive owned titlebar point was available for capture.");
    }
}
