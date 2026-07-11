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

            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            ReplaceWithPng(imageStream, bitmap, encoded.ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ReplaceWithPng(PdfStream imageStream, SKBitmap bitmap, byte[] png)
    {
        // Store as FlateDecoded raw RGB — universally supported and avoids
        // format-specific entries left over from the original image.
        using var decoded = SKBitmap.Decode(png);
        var rgb = new byte[decoded.Width * decoded.Height * 3];
        int p = 0;
        for (int y = 0; y < decoded.Height; y++)
        {
            for (int x = 0; x < decoded.Width; x++)
            {
                var c = decoded.GetPixel(x, y);
                rgb[p++] = c.Red;
                rgb[p++] = c.Green;
                rgb[p++] = c.Blue;
            }
        }

        foreach (var key in imageStream.KeySet().ToArray())
        {
            if (!PdfName.Subtype.Equals(key) && !PdfName.Type.Equals(key))
                imageStream.Remove(key);
        }
        imageStream.SetData(rgb);
        imageStream.Put(PdfName.Width, new PdfNumber(decoded.Width));
        imageStream.Put(PdfName.Height, new PdfNumber(decoded.Height));
        imageStream.Put(PdfName.ColorSpace, PdfName.DeviceRGB);
        imageStream.Put(PdfName.BitsPerComponent, new PdfNumber(8));
        imageStream.SetModified();
    }
}
