using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class SearchFooterAccessibilityTests
{
    [Fact]
    public Task EverySearchFooterActionRemainsVisibleAndHitTestableAtNarrowAndNormalWidths()
        => RuntimeComponentProcess.Run(nameof(SearchFooterAccessibilityScenario));
}

public static class SearchFooterAccessibilityScenario
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
        foreach (var locale in new[] { Locale.EnUS, Locale.Fr })
        {
            SearchFooterScreen.SwitchLocale(window, locale);
            Assert.Equal(MainWindow.LocStatic("Str_Btn_Search"), view.Named<Button>("SearchButton").Content);
            foreach (bool narrow in new[] { true, false })
            {
                window.Width = narrow ? 600 : 1100;
                window.Height = narrow ? 520 : 800;
                window.ApplyAppScale(narrow ? 2.5 : 1);
                view.Open();
                var search = view.Named<Button>("SearchButton");
                var actions = SearchFlyoutLayoutProbe.Descendants<Button>((Grid)search.Parent).ToArray();
                Assert.Equal(3, actions.Length);
                Assert.Contains(actions, button => Equals(button.Content, MainWindow.LocStatic("Str_Btn_Clear")));
                Assert.Contains(actions, button => Equals(button.Content, "+"));
                search.BringIntoView();
                view.Viewport.ScrollToEnd();
                view.Refresh();
                var presenter = SearchFlyoutLayoutProbe.Descendants<ScrollContentPresenter>(view.Viewport)
                    .First(item => ReferenceEquals(item.TemplatedParent, view.Viewport));
                Rect previousWindow = Rect.Empty, previousClip = Rect.Empty;
                int stableSamples = 0;
                ShellWindowFixture.PumpUntil(() =>
                {
                    view.Refresh();
                    Rect currentWindow = SearchFooterScreen.Bounds(window);
                    Rect currentClip = SearchFooterScreen.Bounds(presenter);
                    stableSamples = previousWindow == currentWindow && previousClip == currentClip ? stableSamples + 1 : 0;
                    previousWindow = currentWindow;
                    previousClip = currentClip;
                    return stableSamples >= 5;
                });
                string state = locale + (narrow ? "-narrow" : "-normal");
                SearchFooterScreen.Capture(view.Root, "footer-" + state + ".png");
                Rect clip = SearchFooterScreen.Bounds(presenter);
                Rect work = SearchFooterScreen.WorkArea(window);
                foreach (var button in actions)
                {
                    Rect bounds = SearchFooterScreen.Bounds(button);
                    Assert.True(clip.Contains(bounds), state + " action=" + button.Content + "; bounds=" + bounds + "; clip=" + clip);
                    Assert.True(work.Contains(bounds), state + " action must fit the monitor work area.");
                    var center = new Point(Math.Round(bounds.Left + bounds.Width / 2), Math.Round(bounds.Top + bounds.Height / 2));
                    var hit = view.Root.InputHitTest(view.Root.PointFromScreen(center)) as DependencyObject;
                    bool intended = false;
                    for (var current = hit; current != null; current = VisualTreeHelper.GetParent(current))
                        if (ReferenceEquals(current, button)) { intended = true; break; }
                    Assert.True(intended, state + " action=" + button.Content + "; rounded center hit another control.");
                }
                if (!narrow) Assert.Equal(404, view.Named<FrameworkElement>("TitleSearchForm").ActualWidth, 1);
                view.Close();
            }
        }
    }
}

internal static class SearchFooterScreen
{
    internal static void SwitchLocale(MainWindow window, Locale locale)
    {
        var trigger = (Button)window.FindName("LangButton");
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(trigger).GetPattern(PatternInterface.Invoke)).Invoke();
        var menu = (ContextMenu)window.FindName("LangMenu");
        ShellWindowFixture.PumpUntil(() => menu.IsOpen);
        var columns = (StackPanel)menu.Items[0];
        var radio = columns.Children.OfType<StackPanel>().SelectMany(column => column.Children.OfType<RadioButton>())
            .Single(button => Equals(button.Tag, locale.ToString()));
        ((ISelectionItemProvider)UIElementAutomationPeer.CreatePeerForElement(radio).GetPattern(PatternInterface.SelectionItem)).Select();
        menu.IsOpen = false;
        ShellWindowFixture.PumpUntil(() => LocaleManager.Current == locale);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }

    internal static Rect Bounds(FrameworkElement element)
        => new Rect(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));

    internal static Rect WorkArea(Window window)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf(typeof(MonitorInfo)) };
        Assert.True(GetMonitorInfo(MonitorFromWindow(new WindowInteropHelper(window).Handle, 2), ref info));
        return new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
    }

    internal static void Capture(FrameworkElement surface, string name)
    {
        Rect bounds = Bounds(surface);
        int width = (int)Math.Ceiling(bounds.Width), height = (int)Math.Ceiling(bounds.Height);
        IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen);
        IntPtr bitmap = CreateCompatibleBitmap(screen, width, height), old = SelectObject(memory, bitmap);
        try
        {
            Assert.True(BitBlt(memory, 0, 0, width, height, screen, (int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top), 0x00CC0020));
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            string directory = Environment.GetEnvironmentVariable("KILLERSHELL_TEST_EVIDENCE") ?? Path.GetTempPath();
            Directory.CreateDirectory(directory);
            using var output = File.Create(Path.Combine(directory, name));
            encoder.Save(output);
        }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
