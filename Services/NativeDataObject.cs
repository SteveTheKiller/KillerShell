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
    /// (2026-08-03). This class implements SetData for real, so the shell can write its
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

        private readonly List<(FORMATETC Format, STGMEDIUM Medium)> _entries = [];

        /// <summary>
        /// Releases every stored medium. MUST be called after DoDragDrop returns, and this - not
        /// anything in DragImage.cs - is what takes the shell's drag-image window down.
        /// </summary>
        /// <remarks>
        /// The shell's drag helper stores its formats here through SetData(release: true), and
        /// those media carry pUnkForRelease: a reference to the shell object that OWNS the layered
        /// drag-image window. Nothing released _entries when a drag finished, so that object's
        /// refcount never reached zero and its window sat on screen until the process exited -
        /// the ghost icon, reproduced on every external drop (GIMP 2026-08-03, Telegram
        /// 2026-08-10). ReleaseMedium goes through ReleaseStgMedium, which is what actually drops
        /// that reference; the pre-2026-08-10 GlobalFree version could never have, which is why
        /// disposing the DragSourceHelper wrapper alone never fixed it.
        ///
        /// An earlier investigation wanted this same determinism but prescribed
        /// Marshal.ReleaseComObject(data), which throws ArgumentException on a managed object -
        /// this class is not an RCW. Releasing the stored media is the form of it that works.
        /// </remarks>
        public void ReleaseAll()
        {
            foreach (var (_, Medium) in _entries) ReleaseMedium(Medium);
            _entries.Clear();
        }

        public void SetHGlobal(short cfFormat, IntPtr hGlobal)
        {
            // A failed build (BuildHDrop/BuildUnicodeText answer IntPtr.Zero now rather than
            // writing through a null pointer) must not be registered: QueryGetData would then
            // advertise a format this object cannot actually produce.
            if (hGlobal == IntPtr.Zero) return;

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
            // whole drag down with it - confirmed the hard way (2026-08-03). Returning an
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

                // The caller owns and frees whatever comes back, so it only ever gets memory this
                // object allocated. A medium that cannot be copied answers empty rather than
                // handing out a handle two parties would then both free.
                if (!TryDuplicateMedium(_entries[i].Medium, out medium))
                    System.Diagnostics.Debug.WriteLine($"[NativeDataObject.GetData] Not duplicable, returning empty");
                else
                    System.Diagnostics.Debug.WriteLine($"[NativeDataObject.GetData] Duplicate succeeded");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NativeDataObject.GetData] TryDuplicateMedium threw: {ex}");
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
            // release == true hands ownership to this object, so the medium is kept as it stands
            // and ReleaseMedium will let OLE dispose of it properly later. release == false leaves
            // the caller owning it, so the only thing safe to keep is a copy of our own - and if no
            // copy can be made the set is IGNORED rather than storing the caller's handle. Storing
            // it is what left this object holding memory somebody else was going to free.
            STGMEDIUM stored;
            if (release) stored = medium;
            else if (!TryDuplicateMedium(medium, out stored)) return;

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
            _entries.Add((formatIn, stored));
        }

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
        {
            // DATADIR_SET has no meaningful enumeration for this class - an empty enumerator
            // (not a throw, same in-process reasoning as GetData above) tells a caller "nothing
            // to enumerate" without risking an unhandled exception on a non-marshaled call path.
            var formats = direction == DATADIR.DATADIR_GET
                ? _entries.ConvertAll(e => e.Format).ToArray()
                : [];
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

        /// <summary>
        /// A private copy of a medium, in memory this object allocated itself, or false when no
        /// safe copy can be made.
        /// </summary>
        /// <remarks>
        /// This used to return <paramref name="src"/> unchanged for anything that was not an
        /// HGLOBAL, which handed back a handle this object does NOT own. Stored against a
        /// SetData(release: false), the real owner then frees it and the entry left behind is
        /// dangling - a use-after-free, and half of the heap corruption crash. There is no generic
        /// way to deep-copy an arbitrary tymed here, so a medium that cannot be copied is refused
        /// rather than aliased.
        ///
        /// Copying an HGLOBAL whose pUnkForRelease is set IS fine and is deliberately still
        /// allowed: reading somebody else's bytes into a buffer of our own is safe. Ownership only
        /// ever matters when RELEASING, which is ReleaseMedium's problem, not this one.
        /// </remarks>
        private static bool TryDuplicateMedium(STGMEDIUM src, out STGMEDIUM copy)
        {
            copy = new STGMEDIUM { tymed = TYMED.TYMED_NULL, unionmember = IntPtr.Zero, pUnkForRelease = null };

            if (src.tymed != TYMED.TYMED_HGLOBAL || src.unionmember == IntPtr.Zero) return false;

            IntPtr size = GlobalSize(src.unionmember);
            if ((long)size <= 0) return false;

            IntPtr dst = GlobalAlloc(GMEM_MOVEABLE, size);
            if (dst == IntPtr.Zero) return false;

            // Both locks are checked before a single byte moves. Neither was checked before, so a
            // failed lock walked straight into Marshal.Copy with a bad destination pointer - the
            // access violation that landed two seconds ahead of the heap-corruption crash.
            IntPtr srcPtr = GlobalLock(src.unionmember);
            IntPtr dstPtr = GlobalLock(dst);
            if (srcPtr == IntPtr.Zero || dstPtr == IntPtr.Zero)
            {
                if (srcPtr != IntPtr.Zero) GlobalUnlock(src.unionmember);
                if (dstPtr != IntPtr.Zero) GlobalUnlock(dst);
                GlobalFree(dst);   // ours, never handed out - safe to free here and only here
                return false;
            }

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

            copy = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = dst, pUnkForRelease = null };
            return true;
        }

        /// <summary>
        /// Releases a medium this object owns, by the OLE rules rather than by assuming it is a
        /// plain HGLOBAL.
        /// </summary>
        /// <remarks>
        /// ReleaseStgMedium, NEVER GlobalFree. A STGMEDIUM's third field, pUnkForRelease, names the
        /// object that owns the memory: when it is set, the medium is released by calling Release
        /// on THAT object and the handle itself must not be touched. The shell sets media carrying
        /// it while a drag with a drag image is running, and SetData frees the previous entry every
        /// time the shell re-sets a format - which by SetData's own comment happens repeatedly as
        /// the drag progresses. GlobalFree on the shell's own memory does not throw; it corrupts
        /// the process heap, which is why the failure showed up as 0xc0000374 inside ntdll rather
        /// than as any kind of managed exception (dragging to Telegram, 2026-08-10).
        ///
        /// It also gets every other tymed right - ISTREAM, ISTORAGE, GDI, FILE - where the old code
        /// silently leaked anything that was not HGLOBAL.
        /// </remarks>
        private static void ReleaseMedium(STGMEDIUM medium)
        {
            if (medium.tymed == TYMED.TYMED_NULL && medium.pUnkForRelease == null) return;
            ReleaseStgMedium(ref medium);
        }

        /// <summary>Builds a CF_HDROP (DROPFILES) global for the given absolute paths.</summary>
        public static IntPtr BuildHDrop(string[] paths)
        {
            const int headerSize = 20; // DROPFILES: int pFiles, POINT pt (2 ints), BOOL fNC, BOOL fWide
            int bodySize = 2; // final extra terminator
            foreach (var p in paths) bodySize += (p.Length + 1) * 2;

            IntPtr hGlobal = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (IntPtr)(headerSize + bodySize));
            if (hGlobal == IntPtr.Zero) return IntPtr.Zero;

            // Checked, not assumed: writing through a null lock is an access violation, and an
            // access violation in a drag is indistinguishable from the heap damage this file's
            // ReleaseMedium used to cause. A caller that gets IntPtr.Zero back ends up with a drag
            // carrying no data, which is a dud gesture rather than a dead process.
            IntPtr ptr = GlobalLock(hGlobal);
            if (ptr == IntPtr.Zero) { GlobalFree(hGlobal); return IntPtr.Zero; }

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
            if (hGlobal == IntPtr.Zero) return IntPtr.Zero;

            IntPtr ptr = GlobalLock(hGlobal);   // same check as BuildHDrop, same reason
            if (ptr == IntPtr.Zero) { GlobalFree(hGlobal); return IntPtr.Zero; }

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

        // The only correct way to dispose of a STGMEDIUM: it honors pUnkForRelease and knows how
        // to release every tymed, neither of which GlobalFree does. See ReleaseMedium above.
        [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    }

    /// <summary>Bare IEnumFORMATETC over a fixed array - only Next/Reset/Clone/Skip are ever called.</summary>
    internal sealed class FormatEtcEnumerator(FORMATETC[] formats, int index = 0) : IEnumFORMATETC
    {
        private readonly FORMATETC[] _formats = formats;
        private int _index = index;

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
