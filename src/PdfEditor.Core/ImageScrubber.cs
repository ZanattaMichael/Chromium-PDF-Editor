using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Xobject;
using SkiaSharp;

namespace PdfEditor.Core;

/// <summary>What colour a scrubbed area of an image is painted.</summary>
internal enum ScrubFill
{
    /// <summary>Opaque black — redaction, where the point is that something was removed.</summary>
    Black,

    /// <summary>
    /// The dominant colour immediately around the scrubbed area, so erasing words from a scanned
    /// page leaves paper rather than a black bar. Falls back to white when there is nothing to
    /// sample.
    /// </summary>
    SurroundingPaper,
}

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
    public static bool TryScrubPixels(PdfStream imageStream, Rectangle drawnBBox,
        IList<Rectangle> regions, ScrubFill fill = ScrubFill.Black)
    {
        try
        {
            var xobject = new PdfImageXObject(imageStream);
            byte[] bytes = xobject.GetImageBytes(true);
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap == null) return false;

            float sx = bitmap.Width / drawnBBox.GetWidth();
            float sy = bitmap.Height / drawnBBox.GetHeight();
            var targets = new List<SKRect>();
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
                targets.Add(SKRect.Create(px, pyTop, (right - left) * sx, (top - bottom) * sy));
            }
            if (targets.Count == 0) return true;

            using var canvas = new SKCanvas(bitmap);
            using var paint = new SKPaint { Style = SKPaintStyle.Fill };
            foreach (var target in targets)
            {
                // Sampled per rect, before anything is painted, so one rect's fill can never be
                // read back as another's surroundings.
                paint.Color = fill == ScrubFill.SurroundingPaper ? PaperAround(bitmap, target) : SKColors.Black;
                canvas.DrawRect(target, paint);
            }
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

    /// <summary>
    /// The dominant colour in a band just outside <paramref name="target"/> — the paper the words
    /// being erased were printed on. The mode rather than the mean, because a band that clips a
    /// neighbouring glyph or a rule would drag an average toward grey, while the most common colour
    /// is still the background. Colours are bucketed to 16 levels per channel first so a scan's
    /// sensor noise doesn't split one paper colour across thousands of near-identical values.
    /// </summary>
    private static SKColor PaperAround(SKBitmap bitmap, SKRect target)
    {
        const int band = 4;
        var outer = SKRect.Create(target.Left - band, target.Top - band,
            target.Width + 2 * band, target.Height + 2 * band);
        var counts = new Dictionary<int, (int Count, SKColor Colour)>();
        int x0 = Math.Max(0, (int)outer.Left), x1 = Math.Min(bitmap.Width - 1, (int)outer.Right);
        int y0 = Math.Max(0, (int)outer.Top), y1 = Math.Min(bitmap.Height - 1, (int)outer.Bottom);

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (target.Contains(x, y)) continue; // inside the area being erased: not paper
                var c = bitmap.GetPixel(x, y);
                int key = (c.Red >> 4) << 8 | (c.Green >> 4) << 4 | c.Blue >> 4;
                var seen = counts.TryGetValue(key, out var v) ? v : (0, c);
                counts[key] = (seen.Item1 + 1, seen.Item2);
            }
        }
        if (counts.Count == 0) return SKColors.White; // the rect covers the whole image
        return counts.Values.OrderByDescending(v => v.Count).First().Colour;
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
