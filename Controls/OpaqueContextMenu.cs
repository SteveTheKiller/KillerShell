using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace KillerShell
{
    /// <summary>
    /// A standard ContextMenu hosted in an opaque popup, which lets WPF use ClearType.
    /// ContextMenu hard-codes its private parent Popup to AllowsTransparency=true, so the popup
    /// must be created and corrected before the menu opens.
    /// </summary>
    public sealed class OpaqueContextMenu : ContextMenu
    {
        private static readonly MethodInfo? Hookup = typeof(ContextMenu).GetMethod(
            "HookupParentPopup", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo? ParentPopup = typeof(ContextMenu).GetField(
            "_parentPopup", BindingFlags.Instance | BindingFlags.NonPublic);

        public OpaqueContextMenu()
        {
            Hookup?.Invoke(this, null);
            if (ParentPopup?.GetValue(this) is Popup popup)
                popup.AllowsTransparency = false;

            Opened += (_, __) => NativePopupShadow.Apply(this);
        }
    }

    /// <summary>
    /// Opaque host for submenu content. The ordinary Popup in the menu template preserves
    /// ClearType too, but using this type also gives each submenu its own native DWM shadow.
    /// </summary>
    public sealed class OpaquePopup : Popup
    {
        public OpaquePopup()
        {
            AllowsTransparency = false;
            Opened += (_, __) =>
            {
                if (Child is Visual child)
                    NativePopupShadow.Apply(child);
            };
        }
    }

    /// <summary>
    /// Asks the Desktop Window Manager to draw outside the popup HWND. Unlike a WPF
    /// DropShadowEffect this does not make the popup transparent or bitmap its contents, so
    /// menu text stays on the ClearType rendering path.
    /// </summary>
    internal static class NativePopupShadow
    {
        private const int DwmwaNcRenderingPolicy = 2;
        private const int DwmncrpEnabled = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins
        {
            internal int Left;
            internal int Right;
            internal int Top;
            internal int Bottom;
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attribute, ref int value, int valueSize);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

        internal static void Apply(Visual visual)
        {
            try
            {
                if (PresentationSource.FromVisual(visual) is not HwndSource source ||
                    source.Handle == IntPtr.Zero)
                    return;

                int policy = DwmncrpEnabled;
                _ = DwmSetWindowAttribute(source.Handle, DwmwaNcRenderingPolicy,
                                      ref policy, Marshal.SizeOf<int>());

                // A one-pixel frame is enough to make DWM provide the standard system shadow;
                // the opaque WPF menu still paints the entire client area above it.
                var margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
                _ = DwmExtendFrameIntoClientArea(source.Handle, ref margins);
            }
            catch (DllNotFoundException)
            {
                // Pre-DWM Windows: retain the crisp opaque menu without a shadow.
            }
            catch (EntryPointNotFoundException)
            {
                // Same graceful fallback for an older dwmapi implementation.
            }
        }
    }
}
