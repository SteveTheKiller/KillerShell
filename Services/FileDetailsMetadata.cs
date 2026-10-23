using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerShell.Services
{
    /// <summary>Best-effort file details. Call off the dispatcher; no file handle survives a read.</summary>
    internal sealed class FileDetailsMetadata
    {
        internal int PixelWidth { get; private set; }
        internal int PixelHeight { get; private set; }
        internal int BitsPerPixel { get; private set; }
        internal bool? HasAlpha { get; private set; }
        internal string AspectRatio { get; private set; } = string.Empty;
        internal int? PageCount { get; private set; }
        internal TimeSpan? Duration { get; private set; }
        internal string FileVersion { get; private set; } = string.Empty;

        internal static FileDetailsMetadata ForDemo(string path)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            var details = new FileDetailsMetadata();
            if (IsImage(extension))
            {
                bool alpha = extension == ".png" || extension == ".gif" || extension == ".ico";
                details.PixelWidth = 1920;
                details.PixelHeight = 1080;
                details.AspectRatio = "16:9";
                details.BitsPerPixel = alpha ? 32 : 24;
                details.HasAlpha = alpha;
            }
            else if (IsDocument(extension)) details.PageCount = 8;
            else if (IsMedia(extension)) details.Duration = TimeSpan.FromSeconds(204);
            else if (extension == ".exe" || extension == ".dll") details.FileVersion = "1.0.0.0";
            return details;
        }

        private static bool IsImage(string extension) => extension == ".png" || extension == ".jpg" || extension == ".jpeg"
            || extension == ".gif" || extension == ".bmp" || extension == ".webp" || extension == ".ico"
            || extension == ".tif" || extension == ".tiff";

        private static bool IsDocument(string extension) => extension == ".pdf" || extension == ".doc"
            || extension == ".docx" || extension == ".docm" || extension == ".odt";

        private static bool IsMedia(string extension) => extension == ".mp3" || extension == ".wav" || extension == ".flac"
            || extension == ".m4a" || extension == ".aac" || extension == ".ogg" || extension == ".wma"
            || extension == ".mp4" || extension == ".mkv" || extension == ".avi" || extension == ".mov" || extension == ".wmv";

        internal static FileDetailsMetadata Read(string path)
        {
            var details = new FileDetailsMetadata();
            string extension;
            try { extension = Path.GetExtension(path).ToLowerInvariant(); }
            catch { return details; }
            if (extension == ".exe" || extension == ".dll")
            {
                try { details.FileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion ?? string.Empty; }
                catch { /* No readable version resource is available. */ }
                return details;
            }
            if (IsDocument(extension) || IsMedia(extension))
            {
                details.ReadShellProperties(path, IsDocument(extension));
                return details;
            }
            if (!IsImage(extension)) return details;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.None);
                int rotation = ImageOrientation.RotationFor(frame);
                details.PixelWidth = rotation == 90 || rotation == 270 ? frame.PixelHeight : frame.PixelWidth;
                details.PixelHeight = rotation == 90 || rotation == 270 ? frame.PixelWidth : frame.PixelHeight;
                details.BitsPerPixel = frame.Format.BitsPerPixel;
                details.HasAlpha = frame.Format == PixelFormats.Bgra32 || frame.Format == PixelFormats.Pbgra32
                    || frame.Format == PixelFormats.Rgba64 || frame.Format == PixelFormats.Prgba64
                    || frame.Format == PixelFormats.Rgba128Float || frame.Format == PixelFormats.Prgba128Float
                    || (frame.Palette?.Colors.Any(color => color.A < 255) ?? false);
                int a = details.PixelWidth, b = details.PixelHeight;
                while (b != 0) { int remainder = a % b; a = b; b = remainder; }
                if (a > 0) details.AspectRatio = (details.PixelWidth / a) + ":" + (details.PixelHeight / a);
            }
            catch { /* No readable image header is available. */ }
            return details;
        }

        private void ReadShellProperties(string path, bool document)
        {
            IPropertyStore? store = null;
            try
            {
                Guid iid = typeof(IPropertyStore).GUID;
                // FASTPROPERTIESONLY excludes file property handlers and disk/network reads.
                // Missing cached values remain absent rather than loading a document or media file.
                if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0x8 | 0x40, ref iid, out store) < 0
                    || store == null) return;
                if (PSGetPropertyKeyFromName(document ? "System.Document.PageCount" : "System.Media.Duration", out var key) < 0)
                    return;
                var value = new PropVariant();
                try
                {
                    if (store.GetValue(ref key, out value) < 0) return;
                    if (document && value.Type == 3 && value.Signed > 0) PageCount = value.Signed;
                    else if (!document && value.Type == 21 && value.Unsigned > 0 && value.Unsigned <= long.MaxValue)
                        Duration = TimeSpan.FromTicks((long)value.Unsigned);
                }
                finally { PropVariantClear(ref value); }
            }
            catch { /* Optional properties are unavailable on this file or provider. */ }
            finally { if (store != null) Marshal.ReleaseComObject(store); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid FormatId;
            public uint PropertyId;
        }

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public int Signed;
            [FieldOffset(8)] public ulong Unsigned;
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PropertyKey key);
            [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
            [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
            [PreserveSig] int Commit();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, uint flags,
            ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

        [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int PSGetPropertyKeyFromName(string name, out PropertyKey key);

        [DllImport("ole32.dll", PreserveSig = true)]
        private static extern int PropVariantClear(ref PropVariant value);
    }
}
