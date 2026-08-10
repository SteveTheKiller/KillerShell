using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Decoding a photograph the way the person who took it expects to see it.
//
// WIC hands back the pixels as they are STORED, and a camera does not rotate them. A phone shot in
// portrait is written as a landscape frame plus an EXIF tag saying which way up it goes, and
// nothing in WPF reads that tag - not BitmapImage, not the Image control. So the app decoded every
// portrait phone photo on its side, in the listing thumbnail and in the details strip both, while
// Explorer showed the same file upright, because the shell thumbnail cache applies the tag before
// it stores anything.
//
// One place, three callers, deliberately: the same decode was written out three times
// (IconCache.cs, DetailsPane.cs, DemoImages.cs) and only ever fixed in the one being looked at.
// Anything in this app that turns a photograph into a BitmapSource goes through here now.
namespace KillerShell.Services
{
    internal static class ImageOrientation
    {
        /// <summary>
        /// A photograph decoded to <paramref name="px"/> on its longest side and turned the right
        /// way up, or null when it cannot be read. Frozen, so a background thread can hand it
        /// straight to the UI thread. No file handle survives the call.
        /// </summary>
        /// <param name="ignoreColorProfile">
        /// Skips color management. Worth it for a 32px tile in a long listing, not for a preview
        /// somebody is actually looking at.
        /// </param>
        internal static BitmapSource? Load(string file, int px, bool ignoreColorProfile)
        {
            if (string.IsNullOrEmpty(file) || px <= 0) return null;

            try
            {
                int rotate, srcW, srcH;

                // Header-only pass. DelayCreation means the pixels are never decoded here; all that
                // is wanted is the stored size and the orientation tag. Both are needed BEFORE the
                // real decode, because the size decides which axis gets capped and a quarter turn
                // swaps which axis that is.
                using (var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var frame = BitmapFrame.Create(probe, BitmapCreateOptions.DelayCreation,
                                                   BitmapCacheOption.None);
                    srcW = frame.PixelWidth;
                    srcH = frame.PixelHeight;
                    rotate = RotationFor(frame);
                }

                if (srcW <= 0 || srcH <= 0) return null;

                // The longest side is decided on what the viewer will SEE, then mapped back to the
                // axis the decoder understands, which is still the stored one. Capping the wrong
                // one leaves a portrait photo several times taller than the cell it has to sit in.
                bool swapped = rotate == 90 || rotate == 270;
                int shownW = swapped ? srcH : srcW;
                int shownH = swapped ? srcW : srcH;

                BitmapImage bmp;

                // A STREAM rather than a UriSource: BitmapImage keeps its own cache keyed on the
                // URI, which would hand back the pixels an edited file used to have. IconCache
                // keys its cache on the write time precisely so an edited image refreshes, and the
                // URI cache sitting underneath it would have quietly defeated that.
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;   // fully read before EndInit returns
                    if (ignoreColorProfile) bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

                    // WIC downsamples DURING the decode rather than after, so a thumbnail costs
                    // nowhere near what loading the full resolution and scaling it down would.
                    if ((shownW >= shownH) ^ swapped) bmp.DecodePixelWidth = px;
                    else bmp.DecodePixelHeight = px;

                    bmp.StreamSource = fs;
                    bmp.EndInit();
                }

                bmp.Freeze();

                if (rotate == 0) return bmp;

                var turned = new TransformedBitmap(bmp, new RotateTransform(rotate));
                turned.Freeze();
                return turned;
            }
            catch { return null; }   // corrupt, gone, or a format with no codec installed
        }

        /// <summary>
        /// The clockwise rotation an image's EXIF orientation tag asks for, in degrees, or 0 when
        /// there is no tag to read. The four MIRRORED orientations answer 0: they come from flatbed
        /// scanners and almost nothing else, and an unflipped photo is far less wrong on screen
        /// than a sideways one.
        /// </summary>
        internal static int RotationFor(BitmapFrame frame)
        {
            // A PNG has no app1 block at all and answers the query by throwing rather than by
            // saying no, which is why this is a catch and not a format test.
            try
            {
                const string Query = "/app1/ifd/{ushort=274}";

                if (frame.Metadata is not BitmapMetadata md) return 0;
                if (!md.ContainsQuery(Query)) return 0;

                object? raw = md.GetQuery(Query);
                if (raw == null) return 0;

                ushort tag = raw is ushort u ? u : Convert.ToUInt16(raw);
                return tag switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
            }
            catch { return 0; }
        }
    }
}
