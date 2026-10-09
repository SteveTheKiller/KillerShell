using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace KillerShell
{
    // The shared ContextMenu template (Controls.xaml) wraps the card in a transparent shadow
    // halo, and the style's HorizontalOffset/VerticalOffset pull the popup back so the CARD
    // lands just off the pointer tip. Those offsets only line up when the menu opens down and
    // to the right. Near the bottom or right screen edge WPF opens the menu the other way, the
    // offsets are not mirrored, and the halo ends up between the card and the pointer: the
    // menu floated about 40px above a saved place at the bottom of the sidebar.
    //
    // Registered once for every ContextMenu (App.OnStartup). When a mouse-opened menu has
    // flipped, the offset on the flipped axis is corrected so that card edge sits on the
    // pointer. Menus that open normally, keyboard-opened menus and menus with an explicit
    // placement are left alone. Closing restores the offsets the menu had before.
    internal static class ContextMenuFlip
    {
        internal static void Register()
        {
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
                new RoutedEventHandler(OnOpened));
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent,
                new RoutedEventHandler(OnClosed));
        }

        private static readonly ConditionalWeakTable<ContextMenu, object[]> Saved = new();

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT p);

        private static void OnOpened(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu cm || e.OriginalSource != cm) return;
            if (cm.Placement != PlacementMode.MousePoint) return;
            if (InputManager.Current.MostRecentInputDevice is not MouseDevice) return;

            // Measured once the popup has been positioned and laid out.
            cm.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Correct(cm)));
        }

        private static void Correct(ContextMenu cm)
        {
            if (!cm.IsOpen) return;
            if (cm.Template?.FindName("MenuRoot", cm) is not FrameworkElement card) return;
            if (card.ActualWidth <= 0 || card.ActualHeight <= 0) return;
            var source = PresentationSource.FromVisual(card);
            if (source?.CompositionTarget == null || !GetCursorPos(out var cursor)) return;

            Point bottomRight = card.PointToScreen(new Point(card.ActualWidth, card.ActualHeight));
            var toDip = source.CompositionTarget.TransformFromDevice;

            bool flipUp   = bottomRight.Y <= cursor.Y;
            bool flipLeft = bottomRight.X <= cursor.X;
            if (!flipUp && !flipLeft) return;

            // Remember whatever the menu had before, so closing puts exactly that back.
            Saved.Remove(cm);
            Saved.Add(cm, new[] { cm.ReadLocalValue(ContextMenu.HorizontalOffsetProperty),
                                  cm.ReadLocalValue(ContextMenu.VerticalOffsetProperty) });

            // Flipped up: the whole card sits above the pointer. Move it down so its bottom
            // edge meets the pointer tip.
            if (flipUp)
            {
                double dy = toDip.Transform(new Vector(0, cursor.Y - bottomRight.Y)).Y;
                cm.VerticalOffset += dy;
            }

            // Flipped left: the whole card sits left of the pointer. Same correction sideways.
            if (flipLeft)
            {
                double dx = toDip.Transform(new Vector(cursor.X - bottomRight.X, 0)).X;
                cm.HorizontalOffset += dx;
            }
        }

        private static void OnClosed(object sender, RoutedEventArgs e)
        {
            if (sender is not ContextMenu cm || e.OriginalSource != cm) return;
            if (!Saved.TryGetValue(cm, out var old)) return;
            Saved.Remove(cm);
            Restore(cm, ContextMenu.HorizontalOffsetProperty, old[0]);
            Restore(cm, ContextMenu.VerticalOffsetProperty, old[1]);
        }

        private static void Restore(ContextMenu cm, DependencyProperty dp, object local)
        {
            if (local == DependencyProperty.UnsetValue) cm.ClearValue(dp);
            else cm.SetValue(dp, local);
        }
    }
}
