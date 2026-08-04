using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace KillerShell.Services
{
    /// <summary>
    /// A minimal, real native-COM IDataObject for OLE drag-and-drop.
    ///
    /// Built because System.Windows.Forms.DataObject's IDataObject.SetData throws
    /// NotImplementedException - it is written only to be read FROM, as a drag source's data,
    /// never written TO - and the shell's DragDropHelper (IDragSourceHelper.InitializeFromBitmap)
    /// needs SetData to work so it can stuff its own drag-image formats onto the same object.
    /// Confirmed the hard way: hr=0x80004001 (E_NOTIMPL, exactly the HRESULT a NotImplementedException
    /// becomes crossing a CCW) out of InitializeFromBitmap when handed a WinForms DataObject
    /// (Steve, 2026-08-03). This class implements SetData for real, so the shell can write its
    /// formats, and GetData/QueryGetData for real, so a drop target (Explorer, or KillerShell's
    /// own Window_Drop) can still read FileDrop/UnicodeText back out.
    ///
    /// Storage is HGLOBAL, allocated with the Win32 GlobalAlloc/GlobalLock pair - not
    /// Marshal.AllocHGlobal, which is a different allocator. OLE's data-transfer contract is that
    /// an hGlobal handed across a STGMEDIUM can be GlobalLock'd by whoever receives it, and only a
    /// real GMEM_MOVEABLE handle satisfies that.
    /// </summary>
    internal sealed class NativeDataObject : System.Runtime.InteropServices.ComTypes.IDataObject
    {
        public const short CF_UNICODETEXT = 13;
        public const short CF_HDROP       = 15;

        private readonly List<(FORMATETC Format, STGMEDIUM Medium)> _entries = new();

        public void SetHGlobal(short cfFormat, IntPtr hGlobal)
        {
            var format = new FORMATETC
            {
                cfFormat = cfFormat,
                ptd      = IntPtr.Zero,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex   = -1,
                tymed    = TYMED.TYMED_HGLOBAL,
            };
            var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = hGlobal };
            _entries.Add((format, medium));
        }

        private int FindMatch(ref FORMATETC format)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var f = _entries[i].Format;
                if (f.cfFormat == format.cfFormat &&
                    (f.tymed & format.tymed) != 0 &&
                    f.dwAspect == format.dwAspect)
                    return i;
            }
            return -1;
        }

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            System.Diagnostics.Debug.WriteLine($"[NativeDataObject.GetData] cfFormat={format.cfFormat}, tymed={format.tymed}");
            // A real COM caller (the shell, or ole32 itself) always crosses a true marshaled
            // boundary, where .NET converts a thrown exception into the DV_E_FORMATETC HRESULT
            // automatically - fine. But a drag that never leaves KillerShell hands the SAME
            // managed object back around, and some of the calls into it along that path are
            // plain in-process C# dispatch with no marshaling boundary to do that conversion, so
            // a thrown exception here becomes a real unhandled exception instead and takes the
            // whole drag down with it - confirmed the hard way (Steve, 2026-08-03). Returning an
            // empty medium instead of throwing is not strictly correct IDataObject behavior, but
            // every real caller checks QueryGetData/GetDataPresent first anyway, so nothing that
            // matters ever sees this fallback.
            int i = FindMatch(ref format);
            if (i < 0)
            {
                System.Diagnostics.Debug.WriteLine($"[NativeDataObject.GetData] No match found, returning empty");
                medium = new STGMEDIUM { tymed = TYMED.TYMED_NULL, unionmember = IntPtr.Zero };
                return;
            }
            try
            {
                System.Diagnostics.Debug.WriteLine($"[NativeDataObject.GetData] Found match at index {i}, duplicating...");
                medium = DuplicateMedium(_entries[i].Medium);
                System.Diagnostics.Debug.WriteLine($"[NativeDataObject.GetData] Duplicate succeeded");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NativeDataObject.GetData] DuplicateMedium threw: {ex}");
                medium = new STGMEDIUM { tymed = TYMED.TYMED_NULL, unionmember = IntPtr.Zero };
            }
        }

        // Not throwing here either, for the same in-process reason as GetData above - GetDataHere
        // (caller supplies the medium, we fill it) is never actually used by this class's own
        // formats, which are all plain HGLOBAL via GetData, but a no-op is safer than a throw.
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) { }

        public int QueryGetData(ref FORMATETC format)
            => FindMatch(ref format) >= 0 ? 0 : unchecked((int)0x80040064); // S_OK or DV_E_FORMATETC

        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
        {
            formatOut = formatIn;
            return unchecked((int)0x80004001); // E_NOTIMPL - optional, no canonical translation offered
        }

        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release)
        {
            // Replace any existing entry for the same format - the shell re-sets a few of these
            // (e.g. "IsShowingLayered") as the drag progresses.
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Format.cfFormat == formatIn.cfFormat)
                {
                    ReleaseMedium(_entries[i].Medium);
                    _entries.RemoveAt(i);
                    break;
                }
            }
            _entries.Add((formatIn, release ? medium : DuplicateMedium(medium)));
        }

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
        {
            // DATADIR_SET has no meaningful enumeration for this class - an empty enumerator
            // (not a throw, same in-process reasoning as GetData above) tells a caller "nothing
            // to enumerate" without risking an unhandled exception on a non-marshaled call path.
            var formats = direction == DATADIR.DATADIR_GET
                ? _entries.ConvertAll(e => e.Format).ToArray()
                : Array.Empty<FORMATETC>();
            return new FormatEtcEnumerator(formats);
        }

        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
        {
            connection = 0;
            return unchecked((int)0x80004001); // E_NOTIMPL - no advise-sink support needed for drag/drop
        }

        public void DUnadvise(int connection) { }

        public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
        {
            enumAdvise = null!;
            return unchecked((int)0x80004001); // E_NOTIMPL
        }

        // ── HGLOBAL plumbing ─────────────────────────────────────

        private static STGMEDIUM DuplicateMedium(STGMEDIUM src)
        {
            if (src.tymed != TYMED.TYMED_HGLOBAL || src.unionmember == IntPtr.Zero)
                return src; // best-effort: only HGLOBAL is used by this class

            IntPtr size = GlobalSize(src.unionmember);
            IntPtr dst  = GlobalAlloc(GMEM_MOVEABLE, size);
            IntPtr srcPtr = GlobalLock(src.unionmember);
            IntPtr dstPtr = GlobalLock(dst);
            try
            {
                byte[] buf = new byte[(long)size];
                Marshal.Copy(srcPtr, buf, 0, buf.Length);
                Marshal.Copy(buf, 0, dstPtr, buf.Length);
            }
            finally
            {
                GlobalUnlock(dst);
                GlobalUnlock(src.unionmember);
            }
            return new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = dst };
        }

        private static void ReleaseMedium(STGMEDIUM medium)
        {
            if (medium.tymed == TYMED.TYMED_HGLOBAL && medium.unionmember != IntPtr.Zero)
                GlobalFree(medium.unionmember);
        }

        /// <summary>Builds a CF_HDROP (DROPFILES) global for the given absolute paths.</summary>
        public static IntPtr BuildHDrop(string[] paths)
        {
            const int headerSize = 20; // DROPFILES: int pFiles, POINT pt (2 ints), BOOL fNC, BOOL fWide
            int bodySize = 2; // final extra terminator
            foreach (var p in paths) bodySize += (p.Length + 1) * 2;

            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (IntPtr)(headerSize + bodySize));
            IntPtr ptr = GlobalLock(hGlobal);
            try
            {
                Marshal.WriteInt32(ptr, 0, headerSize);  // DROPFILES.pFiles
                Marshal.WriteInt32(ptr, 4, 0);            // pt.x
                Marshal.WriteInt32(ptr, 8, 0);             // pt.y
                Marshal.WriteInt32(ptr, 12, 0);            // fNC
                Marshal.WriteInt32(ptr, 16, 1);            // fWide = TRUE

                int offset = headerSize;
                foreach (var p in paths)
                {
                    var chars = p.ToCharArray();
                    Marshal.Copy(chars, 0, ptr + offset, chars.Length);
                    offset += chars.Length * 2;
                    Marshal.WriteInt16(ptr, offset, 0);
                    offset += 2;
                }
                Marshal.WriteInt16(ptr, offset, 0);
            }
            finally { GlobalUnlock(hGlobal); }
            return hGlobal;
        }

        /// <summary>Builds a CF_UNICODETEXT global for the given text.</summary>
        public static IntPtr BuildUnicodeText(string text)
        {
            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE, (IntPtr)((text.Length + 1) * 2));
            IntPtr ptr = GlobalLock(hGlobal);
            try
            {
                var chars = text.ToCharArray();
                Marshal.Copy(chars, 0, ptr, chars.Length);
                Marshal.WriteInt16(ptr, chars.Length * 2, 0);
            }
            finally { GlobalUnlock(hGlobal); }
            return hGlobal;
        }

        private const uint GMEM_MOVEABLE = 0x0002;
        private const uint GMEM_ZEROINIT = 0x0040;

        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, IntPtr bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll")] private static extern bool   GlobalUnlock(IntPtr hMem);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalSize(IntPtr hMem);
    }

    /// <summary>Bare IEnumFORMATETC over a fixed array - only Next/Reset/Clone/Skip are ever called.</summary>
    internal sealed class FormatEtcEnumerator : IEnumFORMATETC
    {
        private readonly FORMATETC[] _formats;
        private int _index;

        public FormatEtcEnumerator(FORMATETC[] formats, int index = 0)
        {
            _formats = formats;
            _index   = index;
        }

        public int Next(int celt, FORMATETC[] rgelt, int[] pceltFetched)
        {
            int n = 0;
            while (n < celt && _index < _formats.Length) rgelt[n++] = _formats[_index++];
            if (pceltFetched != null && pceltFetched.Length > 0) pceltFetched[0] = n;
            return n == celt ? 0 : 1; // S_OK or S_FALSE
        }

        public int Skip(int celt) { _index += celt; return 0; }
        public int Reset() { _index = 0; return 0; }
        public void Clone(out IEnumFORMATETC newEnum) => newEnum = new FormatEtcEnumerator(_formats, _index);
    }

    /// <summary>
    /// The drag source's IDropSource: says "keep dragging" until the button is released, and asks
    /// for the OS's own cursors so the shell's layered drag-image window (which the same drag
    /// loop draws for us, via DragDropHelper) actually gets shown.
    /// </summary>
    internal sealed class SimpleDropSource : IDropSource
    {
        private const int MK_LBUTTON = 0x0001;

        public int QueryContinueDrag(bool escapePressed, int keyState)
        {
            if (escapePressed) return 0x00040101;               // DRAGDROP_S_CANCEL
            if ((keyState & MK_LBUTTON) == 0) return 0x00040100; // DRAGDROP_S_DROP
            return 0; // S_OK: continue
        }

        public int GiveFeedback(int effect) => 0x00040102; // DRAGDROP_S_USEDEFAULTCURSORS
    }

    [ComImport, Guid("00000121-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag(bool escapePressed, int keyState);
        [PreserveSig] int GiveFeedback(int effect);
    }

    /// <summary>
    /// P/Invoke of ole32's native DoDragDrop - used instead of WPF's DragDrop.DoDragDrop because
    /// WPF wraps whatever it is given in its own System.Windows.DataObject, which would throw
    /// away the SetData behavior NativeDataObject exists for. Calling ole32 directly keeps the
    /// shell's drag-image writes and our own data on the exact same object the drag loop uses.
    /// </summary>
    internal static class NativeDragDrop
    {
        public static int DoDragDrop(System.Runtime.InteropServices.ComTypes.IDataObject dataObject,
            IDropSource dropSource, int okEffects, out int effect)
            => OleDoDragDrop(dataObject, dropSource, okEffects, out effect);

        [DllImport("ole32.dll", EntryPoint = "DoDragDrop")]
        private static extern int OleDoDragDrop(
            System.Runtime.InteropServices.ComTypes.IDataObject dataObject,
            IDropSource dropSource, int okEffects, out int effect);
    }
}
