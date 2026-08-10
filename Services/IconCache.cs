using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using KillerShell.Shell;

namespace KillerShell.Services
{
    // Real Windows shell icons, cached per extension and per size, so a 100k-result search costs
    // a handful of shell calls rather than 100k. SHGFI_USEFILEATTRIBUTES resolves the icon from
    // the extension string alone with no disk access, except for the few types that carry a
    // per-file icon (.exe, .ico, .lnk).
    //
    // Two sources, because SHGetFileInfo can only ever hand back 16px or 32px:
    //   - 32px and below come straight from SHGetFileInfo, the cheap path the list view uses.
    //   - 48px and above go through the system image list (SHGetImageList), which is the only
    //     way to reach the 48px extra-large and 256px jumbo images the icon view needs.
    //
    // The 32px path stays the default so nothing about the list view changes: it draws at 18
    // logical px and downsampling 32 -> 18 is free, while at the 250% app zoom ceiling it is
    // still only 45px from a 32px source. Note SHGFI_LARGEICON is 0 - large is the default and
    // SMALLICON is the opt-in flag - so that OR is documentation rather than arithmetic.
    public static class IconCache
    {
        private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

        // Formats WIC can decode directly, so these get a real thumbnail of the image itself
        // rather than the generic per-extension icon every other file type shares.
        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp"
        };

        // ── Brand icon pack (brand\icons, normalised into Resources\icons) ───────────────
        //
        // Every directory in the app is drawn from here rather than from the shell, so the tree,
        // the bookmarks, the listing, the tab strip, recents and the overflow list all agree.
        // Loaded once and FROZEN: one bitmap is shared by every row, and a frozen ImageSource is
        // safe to hand to the background threads the listing decodes on.
        //
        // A missing or unloadable resource yields null, and the caller falls through to the real
        // shell icon rather than drawing a blank row.

        /// <summary>
        /// A named icon from the brand pack (Resources\icons), loaded once and frozen. Public so
        /// the tab strip can ask for a specific one by name - TabFolderIconConverter maps a tab's
        /// kind to its art. Null if the resource is missing, so callers fall back rather than
        /// drawing blank.
        /// </summary>
        private static readonly Dictionary<string, ImageSource?> ArtCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The icon pack the active theme draws from: the period Chicago95 art under
        /// Resources\icons\98 on a flat theme, the brand pack at Resources\icons everywhere else.
        /// Read off the palette through MainWindow.FlatChrome (Chrome.cs) rather than the theme
        /// name, the same way the rounded-corner suppression does, so any future flat theme gets
        /// it for free.
        ///
        /// The two packs carry the SAME 24 filenames at the same 160px canvas, so this is a
        /// prefix and nothing else - no per-name table to keep in sync, and a name that only the
        /// brand pack has still resolves, because a miss under 98\ falls back below.
        /// </summary>
        private static string Pack => MainWindow.FlatChrome ? "98/" : string.Empty;

        public static ImageSource? Art(string name)
        {
            string pack = Pack;

            // Cached by PACK AND name, not name alone. The tab strip asks per tab per repaint, and
            // without this every repaint would decode the PNG again; a null is cached too, so a
            // missing resource costs one failed load rather than one per frame. Keying the pack in
            // lets both sets sit in the cache at once, so switching theme and back does not redecode
            // and cannot hand back the other theme's art.
            string key = pack + name;

            lock (ArtCache)
            {
                if (ArtCache.TryGetValue(key, out var hit)) return hit;

                var img = Load(pack + name);

                // A 98 pack that is missing one of the names falls back to the brand art rather
                // than drawing blank. Nothing is missing today; this is so adding a 25th brand icon
                // cannot silently break the flat theme before its period version is drawn.
                if (img == null && pack.Length > 0) img = Load(name);

                ArtCache[key] = img;
                return img;
            }
        }

        private static ImageSource? Load(string relative)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri($"pack://application:,,,/Resources/icons/{relative}.png");
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();          // frozen, so the listing's background threads can use it
                return bmp;
            }
            catch { return null; }     // missing or unreadable - the caller caches the null
        }

        /// <summary>The recents button's art, bound straight from FilePane.xaml.</summary>
        public static ImageSource? RecentsArt => Art("recents_icon");

        // Properties, NOT static readonly fields. A field would resolve the pack once at type
        // initialisation and pin every folder in the app to whichever theme happened to be active
        // at startup; Art() is cached, so asking per call costs a dictionary hit.
        private static ImageSource? GenericFolder => Art("folder_icon");
        private static ImageSource? DriveArt      => Art("drive_icon");
        private static ImageSource? ThisPcArt     => Art("my_pc_icon");

        /// <summary>
        /// Special folders mapped to their art's NAME, by real path. Built once - each
        /// GetFolderPath is a registry read and this is asked per row.
        ///
        /// It holds names rather than resolved ImageSources on purpose: a map of bitmaps is built
        /// at type initialisation and would pin every special folder to the icon pack that was
        /// active at startup, so switching to or from the flat theme would leave Documents,
        /// Downloads and the rest drawing the other theme's art. The name is resolved through the
        /// cached Art() at draw time instead.
        /// </summary>
        private static readonly Dictionary<string, string> FolderArt = BuildFolderArt();

        private static Dictionary<string, string> BuildFolderArt()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(Environment.SpecialFolder f, string art)
            {
                try
                {
                    string p = Environment.GetFolderPath(f);
                    if (!string.IsNullOrEmpty(p)) map[p.TrimEnd('\\')] = art;
                }
                catch { }
            }
            Add(Environment.SpecialFolder.UserProfile,  "home_folder_icon");
            Add(Environment.SpecialFolder.Desktop,      "desktop_folder_icon");
            Add(Environment.SpecialFolder.MyDocuments,  "documents_folder_icon");
            Add(Environment.SpecialFolder.MyPictures,   "pictures_folder_icon");
            Add(Environment.SpecialFolder.MyMusic,      "music_folder_icon");
            Add(Environment.SpecialFolder.MyVideos,     "videos_folder_icon");
            Add(Environment.SpecialFolder.Favorites,    "favorites_folder_icon");
            Add(Environment.SpecialFolder.ProgramFiles, "program_files_icon");
            Add(Environment.SpecialFolder.Windows,      "windows_folder_icon");

            // Downloads has no SpecialFolder member on net48 - it is a KNOWNFOLDERID only - and
            // it is the folder people pick out by its icon more than any other.
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                    map[Path.Combine(home, "Downloads").TrimEnd('\\')] = "downloads_folder_icon";
            }
            catch { }

            // NOTE: SpecialFolder.Recent has no art yet, so it is deliberately absent and falls
            // through to the generic folder. Add "recent_folder_icon" here when it exists.
            return map;
        }

        /// <summary>
        /// The brand art for a directory: its own if it is a special folder, the drive or This PC
        /// art for those, and the generic folder for everything else. Null only when the resource
        /// failed to load, which sends the caller back to the shell icon.
        /// </summary>
        private static ImageSource? FolderArtFor(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (MainWindow.IsThisPc(path)) return ThisPcArt;      // Browse.cs sentinel
            if (path.Length <= 3) return DriveArt;                 // "C:\" and shorter is a root

            // --demo answers from the fabricated machine's own table instead (DemoFileSystem.cs
            // ArtFor). FolderArt below was built from Environment.GetFolderPath, which describes
            // the machine the capture is being taken ON - so on any profile not named the same as
            // the fabricated one, every fabricated special folder would miss and draw the plain
            // folder glyph. A demo path that is not one of the fabricated special folders falls
            // through to the generic folder below, exactly as an ordinary folder does.
            if (MainWindow.DemoMode)
            {
                string? demoName = DemoFs.ArtFor(path);
                return demoName != null ? Art(demoName) ?? GenericFolder : GenericFolder;
            }

            return FolderArt.TryGetValue(path.TrimEnd('\\'), out var name)
                 ? Art(name) ?? GenericFolder
                 : GenericFolder;
        }

        /// <summary>The 32px icon. Kept as the default so existing callers are unaffected.</summary>
        public static ImageSource? For(string filePath) => For(filePath, 32);

        /// <summary>The icon at the smallest shell size that covers <paramref name="px"/>.</summary>
        /// <param name="isDirectory">
        /// Folders have no extension to key on, and the extension-only fast path would answer a
        /// folder with the generic unknown-file icon. Setting this resolves by real path instead,
        /// which costs a disk touch per folder but is also what picks up a custom folder icon.
        /// </param>
        public static ImageSource? For(string filePath, int px, bool isDirectory = false)
        {
            // Directories are drawn from the brand pack, never the shell: its own art for a
            // special folder, drive or This PC art for those, the generic folder for the rest.
            if (isDirectory)
            {
                var art = FolderArtFor(filePath);
                if (art != null) return art;
            }

            string ext;
            try { ext = Path.GetExtension(filePath); } catch { ext = string.Empty; }
            if (string.IsNullOrEmpty(ext)) ext = ".";

            // Per-file: types whose icon is baked into the file itself rather than shared by
            // every file of that type. Folders can carry a custom icon too (desktop.ini), so a
            // browsed directory is resolved by path rather than as one shared folder glyph.
            bool perFile = isDirectory
                        || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".ico", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase);

            int shil = ShilFor(px);

            // Real thumbnail rather than a shared icon, for the formats WIC can decode. Keyed on
            // the write time so an edited image's thumbnail refreshes instead of showing a stale
            // cached decode.
            //
            // A fabricated demo path has no pixel data behind it, so there is nothing at the path
            // to decode: the tile is DRAWN from the path instead (Services\DemoImages.cs), which
            // is what lets the icon view show a grid of actual pictures in a capture without
            // reading, or shipping, a single real photograph. A path that is not one of the
            // fabricated pictures answers null and falls through to the generic icon below.
            if (!isDirectory && ImageExtensions.Contains(ext))
            {
                if (MainWindow.DemoMode)
                {
                    var drawn = DemoImages.Render(filePath, px);
                    if (drawn != null) return drawn;
                }
                else
                {
                    var thumb = ImageThumbnail(filePath, px, shil);
                    if (thumb != null) return thumb;
                }
                // Falls through to the generic icon below on any failure (corrupt file, unsupported
                // variant of the format, file gone by the time it is read, etc.) rather than a
                // blank tile.
            }

            string key = (perFile ? filePath : ext) + "|" + shil + (isDirectory ? "|d" : string.Empty);

            lock (Cache)
                if (Cache.TryGetValue(key, out var hit)) return hit;

            string target = perFile ? filePath : "x" + ext;
            bool real = perFile;

            // The per-file query asks the shell about a REAL path, and a demo path is not on disk
            // (DemoFileSystem.cs). The shell answers a question about a path that does not exist
            // with nothing at all, so every fabricated folder in the tree and every fabricated
            // folder, .exe, .ico and .lnk row would draw blank - and the blank would be cached.
            // Substituting something that resolves to the same GENERIC icon fixes the row without
            // pretending the file is there: a directory that certainly exists for a folder, and
            // the extension-only synthetic name for the rest. The cache key is still the fake
            // path, so this costs one shell call per fake path rather than one per row.
            if (perFile && MainWindow.DemoMode && !OnDisk(filePath, isDirectory))
            {
                if (isDirectory) target = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                else { target = "x" + ext; real = false; }
            }

            var img = shil == SHIL_LARGE ? LoadSmallPath(target, real)
                                         : LoadFromImageList(target, real, shil);

            // The image list can legitimately come up empty on a locked-down or unusual shell.
            // Falling back to the 32px path is better than a blank tile.
            if (img == null && shil != SHIL_LARGE) img = LoadSmallPath(target, real);

            lock (Cache) Cache[key] = img;
            return img;
        }

        // Only ever asked in demo mode, so the ordinary path pays nothing for it.
        private static bool OnDisk(string filePath, bool isDirectory)
        {
            try { return isDirectory ? Directory.Exists(filePath) : File.Exists(filePath); }
            catch { return false; }
        }

        private static ImageSource? ImageThumbnail(string filePath, int px, int shil)
        {
            string key;
            try
            {
                if (!File.Exists(filePath)) return null;
                key = filePath + "|" + shil + "|thumb|" + File.GetLastWriteTimeUtc(filePath).Ticks;
            }
            catch { return null; }

            lock (Cache)
                if (Cache.TryGetValue(key, out var hit)) return hit;

            ImageSource? img;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // decode now and release the file handle
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                // WIC downsamples DURING decode rather than after, so this costs nowhere near what
                // loading the full-resolution image would - the same reason DecodePixelWidth is
                // used instead of loading full size and scaling a RenderTargetBitmap down.
                bmp.DecodePixelWidth = px;
                bmp.UriSource = new Uri(filePath);
                bmp.EndInit();
                bmp.Freeze();
                img = bmp;
            }
            catch { img = null; }   // corrupt file, an unsupported variant of the format, etc.

            lock (Cache) Cache[key] = img;
            return img;
        }

        private static int ShilFor(int px)
            => px <= 32 ? SHIL_LARGE
             : px <= 48 ? SHIL_EXTRALARGE
                        : SHIL_JUMBO;

        // ── 32px path (SHGetFileInfo) ────────────────────────────
        /// <summary>
        /// The "This PC" icon: the brand pack's my_pc art, falling back to imageres.dll (where
        /// Explorer's own copy lives, index 104) only if that resource failed to load. This PC
        /// has no path for the shell to resolve, which is why it needs its own entry point at
        /// all - Bookmarks.cs calls straight into here rather than through For().
        /// </summary>
        public static ImageSource? ForComputer(int px)
        {
            if (ThisPcArt != null) return ThisPcArt;

            string key = ":computer:|" + px;
            lock (Cache)
                if (Cache.TryGetValue(key, out var hit)) return hit;

            ImageSource? img = null;
            IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
            try
            {
                string res = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "imageres.dll");

                if (ExtractIconEx(res, 104, out large, out small, 1) > 0)
                {
                    IntPtr use = px <= 16 && small != IntPtr.Zero ? small : large;
                    if (use != IntPtr.Zero) img = FromHIcon(use, crop: false);
                }
            }
            catch { /* an unusual or locked-down shell: no icon rather than a wrong one */ }
            finally
            {
                if (large != IntPtr.Zero) DestroyIcon(large);
                if (small != IntPtr.Zero) DestroyIcon(small);
            }

            lock (Cache) Cache[key] = img;
            return img;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int ExtractIconEx(string file, int index,
                                                out IntPtr large, out IntPtr small, int count);

        private static ImageSource? LoadSmallPath(string pathOrName, bool real)
        {
            var info = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_LARGEICON;
            if (!real) flags |= SHGFI_USEFILEATTRIBUTES;

            IntPtr r = SHGetFileInfo(pathOrName, FILE_ATTRIBUTE_NORMAL, ref info,
                                     (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            try { return FromHIcon(info.hIcon, crop: false); }
            finally { DestroyIcon(info.hIcon); }
        }

        // ── 48px / 256px path (system image list) ────────────────
        // SHGFI_SYSICONINDEX gives the file type's index into the system image list; the image
        // list for the requested size then hands back that entry as an icon we own.
        private static ImageSource? LoadFromImageList(string pathOrName, bool real, int shil)
        {
            var info = new SHFILEINFO();
            uint flags = SHGFI_SYSICONINDEX;
            if (!real) flags |= SHGFI_USEFILEATTRIBUTES;

            IntPtr r = SHGetFileInfo(pathOrName, FILE_ATTRIBUTE_NORMAL, ref info,
                                     (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (r == IntPtr.Zero) return null;

            IImageList? list = null;
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                var iid = IID_IImageList;
                if (SHGetImageList(shil, ref iid, out list) != 0 || list == null) return null;
                if (list.GetIcon(info.iIcon, ILD_TRANSPARENT, ref hIcon) != 0 || hIcon == IntPtr.Zero)
                    return null;

                // Jumbo only: see TrimCanvas for why.
                return FromHIcon(hIcon, crop: shil == SHIL_JUMBO);
            }
            catch { return null; }
            finally
            {
                if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
                if (list != null) Marshal.ReleaseComObject(list);
            }
        }

        private static ImageSource? FromHIcon(IntPtr hIcon, bool crop)
        {
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                if (crop) src = TrimCanvas(src);
                src.Freeze();
                return src;
            }
            catch { return null; }
        }

        // Windows does not have a 256px icon for most file types. Ask the jumbo list for one and
        // it hands back the 32px or 48px image sitting in the middle of a 256px transparent
        // canvas, which drawn into a tile becomes a postage stamp floating in space. So measure
        // the opaque bounds and crop to them.
        //
        // Only when the content is genuinely small: an icon that fills most of the canvas is a
        // real 256px asset, and its transparent margin is deliberate design that should be kept.
        // Below 60% is well clear of that and unambiguously the centered-small-icon case.
        private static BitmapSource TrimCanvas(BitmapSource src)
        {
            int w = src.PixelWidth, h = src.PixelHeight;
            if (w <= 64 || h <= 64) return src;

            // CopyPixels below reads 4 bytes per pixel with alpha last, so make sure that is what
            // the bitmap actually is before trusting the arithmetic.
            var bgra = src;
            if (bgra.Format != PixelFormats.Bgra32)
            {
                try { bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0); }
                catch { return src; }
            }

            int stride = w * 4;
            var px = new byte[stride * h];
            try { bgra.CopyPixels(px, stride, 0); }
            catch { return src; }

            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    if (px[row + (x * 4) + 3] <= 8) continue;   // effectively transparent
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0 || maxY < 0) return src;   // fully transparent, nothing to trim

            int cw = maxX - minX + 1, ch = maxY - minY + 1;
            if (cw > w * 0.6 || ch > h * 0.6) return src;   // a real large icon; leave it alone

            try { return new CroppedBitmap(src, new Int32Rect(minX, minY, cw, ch)); }
            catch { return src; }
        }

        // ── Interop ──────────────────────────────────────────────
        private const uint SHGFI_ICON              = 0x100;
        private const uint SHGFI_LARGEICON         = 0x0;    // the default; see the class note
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint SHGFI_SYSICONINDEX      = 0x4000;
        private const uint FILE_ATTRIBUTE_NORMAL   = 0x80;

        private const int SHIL_LARGE      = 0;   // 32px
        private const int SHIL_EXTRALARGE = 2;   // 48px
        private const int SHIL_JUMBO      = 4;   // 256px

        private const int ILD_TRANSPARENT = 1;

        private static Guid IID_IImageList = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int    iIcon;
            public uint   dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]  public string szTypeName;
        }

        // Only GetIcon is ever called, but every method above it in the vtable has to be declared
        // for the slots to line up. Draw's parameter is a struct we never build, so it is left as
        // an IntPtr rather than dragging IMAGELISTDRAWPARAMS in.
        [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IImageList
        {
            [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
            [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
            [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
            [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
            [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
            [PreserveSig] int Draw(IntPtr pimldp);
            [PreserveSig] int Remove(int i);
            [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll")]
        private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList? ppv);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
