using iText.IO.Image;
using iText.Kernel.Geom;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace PdfEditor.Core;

/// <summary>
/// Repositions raster images on a page. Because a placed image is baked into the page content
/// stream, moving it means removing the original draw (like the text-move tool does) and re-drawing
/// the same image bytes at the shifted rectangle — in the page's default user space so it lands
/// correctly even on Chrome/Google-Docs PDFs that leave a transform active.
/// </summary>
public static class ImageTools
{
    /// <summary>
    /// Moves every image overlapping <paramref name="source"/> by (<paramref name="dx"/>,
    /// <paramref name="dy"/>) in PDF user space. A no-op when the region holds no image.
    /// </summary>
    public static EditResult MoveImage(byte[] pdf, int page, RectRegion source, float dx, float dy,
        string? password = null)
    {
        var images = FindImages(pdf, page, source, password);
        if (images.Count == 0) return EditResult.Of(pdf);

        // Remove the original image content over each image's own bounds, then redraw shifted.
        var removeRegions = images
            .Select(i => new RectRegion(page, i.Rect.GetX(), i.Rect.GetY(), i.Rect.GetWidth(), i.Rect.GetHeight()))
            .ToList();
        var removed = Redactor.RemoveContent(pdf, removeRegions, password);

        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(removed.Pdf, output, password))
        {
            var canvas = PdfContentGuard.InDefaultUserSpace(doc.GetPage(page), doc);
            foreach (var img in images)
            {
                var data = ImageDataFactory.Create(img.Bytes);
                var dest = new Rectangle(img.Rect.GetX() + dx, img.Rect.GetY() + dy,
                    img.Rect.GetWidth(), img.Rect.GetHeight());
                canvas.AddImageFittedIntoRectangle(data, dest, false);
            }
        }
        return new EditResult(output.ToArray(), removed.Warnings);
    }

    private static List<(byte[] Bytes, Rectangle Rect)> FindImages(byte[] pdf, int page,
        RectRegion region, string? password)
    {
        using var doc = PdfIo.OpenReadOnly(pdf, password);
        if (page < 1 || page > doc.GetNumberOfPages())
            throw new ArgumentOutOfRangeException(nameof(page), $"Page {page} does not exist.");
        var finder = new ImageFinder(new Rectangle(region.X, region.Y, region.Width, region.Height));
        new PdfCanvasProcessor(finder).ProcessPageContent(doc.GetPage(page));
        return finder.Found;
    }

    private sealed class ImageFinder : IEventListener
    {
        private readonly Rectangle _region;
        public List<(byte[] Bytes, Rectangle Rect)> Found { get; } = new();

        public ImageFinder(Rectangle region) => _region = region;

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_IMAGE || data is not ImageRenderInfo info) return;
            var m = info.GetImageCtm();
            // An image draws in the unit square transformed by the CTM; for an axis-aligned
            // placement the on-page rectangle is (I31, I32) with size (I11, I22).
            float w = m.Get(Matrix.I11), h = m.Get(Matrix.I22);
            float x = m.Get(Matrix.I31), y = m.Get(Matrix.I32);
            var rect = new Rectangle(Math.Min(x, x + w), Math.Min(y, y + h), Math.Abs(w), Math.Abs(h));
            if (!Intersects(rect, _region)) return;
            try
            {
                var bytes = info.GetImage()?.GetImageBytes();
                if (bytes is { Length: > 0 }) Found.Add((bytes, rect));
            }
            catch
            {
                // Unsupported/undecodable image encoding — skip it rather than fail the whole move.
            }
        }

        public ICollection<EventType> GetSupportedEvents() =>
            new HashSet<EventType> { EventType.RENDER_IMAGE };

        private static bool Intersects(Rectangle a, Rectangle b) =>
            a.GetLeft() < b.GetRight() && b.GetLeft() < a.GetRight() &&
            a.GetBottom() < b.GetTop() && b.GetBottom() < a.GetTop();
    }
}
