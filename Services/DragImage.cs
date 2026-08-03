using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerShell.Services
{
    /// <summary>
    /// Sets the real Windows shell drag image (IDragSourceHelper) before a DoDragDrop call, so
    /// dragging a file shows the file's own icon at reduced opacity following the cursor - the
    /// same thing Explorer does - instead of the bare default OS cursor a plain DataObject leaves
    /// you with (Steve, 2026-08-03: "right now its like a text cursor").
    ///
    /// This is genuinely how Explorer wires it, not a homemade stand-in: a system-rendered drag
    /// image is drawn by the same OLE drag loop that tracks the cursor, so it can never fight the
    /// drop-target hit-testing the way a topmost window following the cursor would (that window
    /// would itself be what WindowFromPoint sees under the cursor, breaking every drop target
    /// beneath it - which is exactly why this is not built as a floating WPF window instead).
    /// </summary>
    internal static class DragImage
    {
        /// <summary>
        /// Best-effort only: a missing drag image is cosmetic, never a reason to fail the drag
        /// itself, so every failure here is swallowed and the caller's DoDragDrop proceeds either
        /// way. <paramref name="data"/> must be a real native-COM IDataObject whose SetData
        /// actually works (see NativeDataObject) - a System.Windows.Forms.DataObject's SetData
        /// throws NotImplementedException, which InitializeFromBitmap below surfaces as
        /// hr=0x80004001 (E_NOTIMPL) and silently gets no drag image at all.
        /// </summary>
        public static void Attach(System.Runtime.InteropServices.ComTypes.IDataObject data, ImageSource? icon, int size = 48, double opacity = 0.5)
        {
            if (icon is not BitmapSource bmp)
            {
                System.Diagnostics.Debug.WriteLine("[DragDiag] DragImage.Attach: icon is not a BitmapSource - bailing");
                return;
            }

            IntPtr hBitmap = IntPtr.Zero;
            object? helperObj = null;
            try
            {
                hBitmap = ToPremultipliedHBitmap(bmp, size, opacity);
                if (hBitmap == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine("[DragDiag] DragImage.Attach: CreateDIBSection returned NULL");
                    return;
                }

                helperObj = new DragDropHelper();
                if (helperObj is not IDragSourceHelper helper)
                {
                    System.Diagnostics.Debug.WriteLine("[DragDiag] DragImage.Attach: DragDropHelper did not cast to IDragSourceHelper");
                    return;
                }

                var shdi = new SHDRAGIMAGE
                {
                    sizeDragImage = new SIZE { cx = size, cy = size },
                    ptOffset      = new POINT { x = size / 2, y = size / 2 },
                    hbmpDragImage = hBitmap,
                    crColorKey    = unchecked((int)0xFFFFFFFF),   // no color key - alpha carries it
                };

                // The helper takes ownership of the bitmap once this succeeds; only this code
                // frees it, and only when the call failed.
                int hr = helper.InitializeFromBitmap(ref shdi, data);
                System.Diagnostics.Debug.WriteLine($"[DragDiag] DragImage.Attach: InitializeFromBitmap hr=0x{hr:X8}");
                if (hr == 0) hBitmap = IntPtr.Zero;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DragDiag] DragImage.Attach THREW: {ex}"); }
            finally
            {
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                if (helperObj != null) Marshal.ReleaseComObject(helperObj);
            }
        }

        /// <summary>
        /// A 32bpp top-down DIB with premultiplied alpha - what the shell's drag image expects,
        /// and where the requested 50% fade is baked in (alpha and color both scaled by
        /// <paramref name="opacity"/>, since premultiplied means the color channels already carry
        /// the alpha).
        /// </summary>
        private static IntPtr ToPremultipliedHBitmap(BitmapSource src, int size, double opacity)
        {
            var scaled = new TransformedBitmap(src, new ScaleTransform(
                (double)size / src.PixelWidth, (double)size / src.PixelHeight));
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

            int stride = size * 4;
            var pixels = new byte[stride * size];
            converted.CopyPixels(pixels, stride, 0);

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                double factor = (a / 255.0) * opacity;
                pixels[i]     = (byte)(b * factor);
                pixels[i + 1] = (byte)(g * factor);
                pixels[i + 2] = (byte)(r * factor);
                pixels[i + 3] = (byte)(a * opacity);
            }

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize        = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth       = size,
                    biHeight      = -size,   // negative: top-down, matching CopyPixels' row order
                    biPlanes      = 1,
                    biBitCount    = 32,
                    biCompression = 0,       // BI_RGB
                },
            };

            IntPtr hBitmap = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;

            Marshal.Copy(pixels, 0, bits, pixels.Length);
            return hBitmap;
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)] private struct SIZE  { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SHDRAGIMAGE
        {
            public SIZE   sizeDragImage;
            public POINT  ptOffset;
            public IntPtr hbmpDragImage;
            public int    crColorKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int   biSize;
            public int   biWidth;
            public int   biHeight;
            public short biPlanes;
            public short biBitCount;
            public int   biCompression;
            public int   biSizeImage;
            public int   biXPelsPerMeter;
            public int   biYPelsPerMeter;
            public int   biClrUsed;
            public int   biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            // No color table: 32bpp BI_RGB needs none, this only pads the struct to the size
            // CreateDIBSection expects.
            public int bmiColors;
        }

        [ComImport, Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
        private class DragDropHelper { }

        [ComImport, Guid("DE5BF786-477A-11D2-839D-00C04FD918D0"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDragSourceHelper
        {
            [PreserveSig] int InitializeFromBitmap(ref SHDRAGIMAGE pshdi,
                System.Runtime.InteropServices.ComTypes.IDataObject pDataObject);
            [PreserveSig] int InitializeFromWindow(IntPtr hwnd, ref POINT ppt,
                System.Runtime.InteropServices.ComTypes.IDataObject pDataObject);
        }

    }

    /// <summary>
    /// The DROP side of the same shell drag-image contract: the layered "ghost" bitmap
    /// DragImage.Attach writes onto the data object is only ever drawn by whichever window's
    /// IDropTarget calls IDropTargetHelper as the drag passes over it - Explorer's own drop
    /// target does this automatically, which is why the icon can look fine dropping onto a real
    /// Explorer window. WPF's own AllowDrop plumbing does NOT call it, so a drag that never
    /// leaves KillerShell (pane to pane, or window to window of the same app) never got an image
    /// at all - confirmed the hard way: hr=0 out of InitializeFromBitmap, no GiveFeedback issue,
    /// and still nothing on screen for an all-KillerShell drag (Steve, 2026-08-03).
    ///
    /// One instance must live for the whole DragEnter..Drop/DragLeave span - the helper tracks its
    /// own layered window per instance, so calling DragOver on a fresh instance instead of the one
    /// DragEnter created would move nothing.
    /// </summary>
    internal sealed class DropTargetHelper : IDisposable
    {
        private object? _com;

        public bool Enter(IntPtr hwnd, System.Runtime.InteropServices.ComTypes.IDataObject data, System.Windows.Point screenPt, int effect)
        {
            _com = new DragDropHelperCoClass();
            if (_com is not IDropTargetHelperCom helper) { _com = null; return false; }
            var pt = new POINT { x = (int)screenPt.X, y = (int)screenPt.Y };
            int hr = helper.DragEnter(hwnd, data, ref pt, effect);
            System.Diagnostics.Debug.WriteLine($"[DragDiag] DropTargetHelper.Enter: hr=0x{hr:X8}");
            return hr == 0;
        }

        public void Over(System.Windows.Point screenPt, int effect)
        {
            if (_com is not IDropTargetHelperCom helper) return;
            var pt = new POINT { x = (int)screenPt.X, y = (int)screenPt.Y };
            helper.DragOver(ref pt, effect);
        }

        public void Drop(System.Runtime.InteropServices.ComTypes.IDataObject data, System.Windows.Point screenPt, int effect)
        {
            if (_com is not IDropTargetHelperCom helper) return;
            var pt = new POINT { x = (int)screenPt.X, y = (int)screenPt.Y };
            helper.Drop(data, ref pt, effect);
        }

        public void Leave()
        {
            (_com as IDropTargetHelperCom)?.DragLeave();
        }

        public void Dispose()
        {
            if (_com != null) { Marshal.ReleaseComObject(_com); _com = null; }
        }

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }

        [ComImport, Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
        private class DragDropHelperCoClass { }

        [ComImport, Guid("4657278B-411B-11D2-839A-00C04FD918D0"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDropTargetHelperCom
        {
            [PreserveSig] int DragEnter(IntPtr hwndTarget,
                System.Runtime.InteropServices.ComTypes.IDataObject dataObject, ref POINT pt, int effect);
            [PreserveSig] int DragLeave();
            [PreserveSig] int DragOver(ref POINT pt, int effect);
            [PreserveSig] int Drop(System.Runtime.InteropServices.ComTypes.IDataObject dataObject,
                ref POINT pt, int effect);
        }
    }
}
