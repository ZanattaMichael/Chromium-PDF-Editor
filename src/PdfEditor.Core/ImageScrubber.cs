using System.Runtime.InteropServices;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Xobject;
using SkiaSharp;

namespace PdfEditor.Core;

/// <summary>
/// Blacks out the pixels of an image XObject that fall inside redaction regions,
/// re-encoding the image so the original pixel data is truly gone.
/// </summary>
internal static class ImageScrubber
{
    /// <summary>
    /// Attempts to scrub the region overlap out of the image's pixel data.
    /// <paramref name="drawnBBox"/> is the user-space rectangle the image is drawn into.
    /// Returns false when the image format could not be decoded, in which case the
    /// caller must fall back to dropping the image.
    /// </summary>
    public static bool TryScrubPixels(PdfStream imageStream, Rectangle drawnBBox, IList<Rectangle> regions)
    {
        try
        {
            var xobject = new PdfImageXObject(imageStream);
            byte[] bytes = xobject.GetImageBytes(true);
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap == null) return false;

            using var canvas = new SKCanvas(bitmap);
            using var black = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
            float sx = bitmap.Width / drawnBBox.GetWidth();
            float sy = bitmap.Height / drawnBBox.GetHeight();
            bool painted = false;
            foreach (var region in regions)
            {
                float left = Math.Max(region.GetLeft(), drawnBBox.GetLeft());
                float right = Math.Min(region.GetRight(), drawnBBox.GetRight());
                float bottom = Math.Max(region.GetBottom(), drawnBBox.GetBottom());
                float top = Math.Min(region.GetTop(), drawnBBox.GetTop());
                if (left >= right || bottom >= top) continue;
                // Image rows run top-down while PDF user space runs bottom-up.
                float px = (left - drawnBBox.GetLeft()) * sx;
                float pyTop = (drawnBBox.GetTop() - top) * sy;
                canvas.DrawRect(px, pyTop, (right - left) * sx, (top - bottom) * sy, black);
                painted = true;
            }
            if (!painted) return true;
            canvas.Flush();

            byte[]? rgb = ReadRgb(bitmap);
            // No readable pixels means we cannot prove the scrubbed image is what gets written, so
            // report failure and let the caller drop the image outright. Failing closed is the only
            // safe direction here: this is redaction.
            if (rgb == null) return false;
            ReplaceWithRgb(imageStream, bitmap.Width, bitmap.Height, rgb);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the scrubbed bitmap out as packed 8-bit RGB, the form the PDF stream wants.
    /// <para>
    /// A row-at-a-time <see cref="SKPixmap.ReadPixels(SKImageInfo, IntPtr, int, int, int)"/> into a
    /// pinned buffer, dropping the alpha byte as it goes. This used to encode the bitmap to PNG, decode that
    /// PNG straight back into a second bitmap, and read the copy a pixel at a time through
    /// <c>GetPixel</c> — one managed-to-native call per pixel, 16.7 million of them on a 4096-square
    /// image, on top of a PNG compress/decompress round trip whose output was never otherwise used
    /// (the already-scrubbed bitmap was passed in and ignored). Redacting over a 4096-square image
    /// took 8.5s because of it, against a 20s fuzz budget it intermittently blew on CI.
    /// </para>
    /// <para>
    /// <see cref="SKAlphaType.Unpremul"/> is requested deliberately: <c>GetPixel</c> returned
    /// unpremultiplied components, and since the stream written below has no alpha channel, taking
    /// premultiplied bytes instead would darken every partially transparent pixel toward black.
    /// </para>
    /// </summary>
    /// <returns>Packed RGB, three bytes per pixel; null if the pixels could not be read.</returns>
    private static byte[]? ReadRgb(SKBitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height;
        // A row at a time, into one reused buffer. Reading the whole surface in a single call would
        // need a second full-size copy on the managed heap (4 bytes per pixel, 64 MiB on a
        // 4096-square image) purely to strip its alpha byte; a row costs kilobytes, the extra
        // ReadPixels calls are one per row rather than one per pixel, and it measured faster.
        var rowInfo = new SKImageInfo(width, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        int rowBytes = rowInfo.RowBytes;
        var row = new byte[rowBytes];
        var rgb = new byte[checked(width * height * 3)];

        using var pixels = bitmap.PeekPixels();
        if (pixels == null) return null;

        var pin = GCHandle.Alloc(row, GCHandleType.Pinned);
        try
        {
            IntPtr buffer = pin.AddrOfPinnedObject();
            int dst = 0;
            for (int y = 0; y < height; y++)
            {
                if (!pixels.ReadPixels(rowInfo, buffer, rowBytes, 0, y)) return null;
                for (int src = 0; src < rowBytes; src += 4)
                {
                    rgb[dst++] = row[src];
                    rgb[dst++] = row[src + 1];
                    rgb[dst++] = row[src + 2];
                }
            }
        }
        finally { pin.Free(); }
        return rgb;
    }

    private static void ReplaceWithRgb(PdfStream imageStream, int width, int height, byte[] rgb)
    {
        // Store as FlateDecoded raw RGB — universally supported and avoids
        // format-specific entries left over from the original image.
        foreach (var key in imageStream.KeySet().ToArray())
        {
            if (!PdfName.Subtype.Equals(key) && !PdfName.Type.Equals(key))
                imageStream.Remove(key);
        }
        imageStream.SetData(rgb);
        imageStream.Put(PdfName.Width, new PdfNumber(width));
        imageStream.Put(PdfName.Height, new PdfNumber(height));
        imageStream.Put(PdfName.ColorSpace, PdfName.DeviceRGB);
        imageStream.Put(PdfName.BitsPerComponent, new PdfNumber(8));
        imageStream.SetModified();
    }
}
