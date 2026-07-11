using System.Text;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;

namespace PdfEditor.Core;

/// <summary>
/// Text discovery and in-place text editing. Editing works by truly removing the
/// original text operators from the content stream (via <see cref="ContentStreamEditor"/>)
/// and stamping replacement text into the same region.
/// </summary>
public static class TextTools
{
    /// <summary>Returns the text inside a region plus its dominant font size.</summary>
    public static RegionText GetTextInRegion(byte[] pdf, RectRegion region, string? password = null)
    {
        using var doc = PdfIo.OpenReadOnly(pdf, password);
        var rect = new Rectangle(region.X, region.Y, region.Width, region.Height);
        var chunks = CollectChunks(doc, region.Page).Where(c => ContainsCenter(rect, c.BBox)).ToList();
        return new RegionText(AssembleText(chunks), chunks.Count == 0 ? 12f : chunks.Max(c => c.FontHeight));
    }

    /// <summary>
    /// Replaces the text inside a region: original text operators are removed from the
    /// file and the new text is laid out inside the same rectangle.
    /// </summary>
    public static EditResult ReplaceTextInRegion(byte[] pdf, RectRegion region, string newText,
        float? fontSize = null, string? password = null)
    {
        float size = fontSize ?? GetTextInRegion(pdf, region, password).FontSize;
        var removed = Redactor.RemoveContent(pdf, new[] { region }, password);
        var stamped = StampText(removed.Pdf, region, newText, size, password);
        return new EditResult(stamped, removed.Warnings);
    }

    /// <summary>Finds every occurrence of a phrase across the document.</summary>
    public static IReadOnlyList<TextMatch> FindText(byte[] pdf, string phrase, string? password = null)
    {
        if (string.IsNullOrEmpty(phrase)) return Array.Empty<TextMatch>();
        using var doc = PdfIo.OpenReadOnly(pdf, password);
        var matches = new List<TextMatch>();
        for (int p = 1; p <= doc.GetNumberOfPages(); p++)
        {
            var strategy = new RegexBasedLocationExtractionStrategy(
                System.Text.RegularExpressions.Regex.Escape(phrase));
            new PdfCanvasProcessor(strategy).ProcessPageContent(doc.GetPage(p));
            foreach (var location in strategy.GetResultantLocations())
            {
                var r = location.GetRectangle();
                matches.Add(new TextMatch(p, location.GetText(), r.GetX(), r.GetY(), r.GetWidth(), r.GetHeight()));
            }
        }
        return matches;
    }

    /// <summary>Replaces every occurrence of a phrase document-wide. Returns the count replaced.</summary>
    public static (EditResult Result, int Count) ReplaceAll(byte[] pdf, string phrase, string replacement,
        string? password = null)
    {
        var matches = FindText(pdf, phrase, password);
        if (matches.Count == 0) return (EditResult.Of(pdf), 0);

        var warnings = new List<string>();
        byte[] current = pdf;
        // Inset each match rect slightly so glyphs of adjacent words that merely touch
        // the boundary are not removed with it.
        var regions = matches.Select(m => new RectRegion(m.Page,
            m.X + 0.2f, m.Y + 0.2f, Math.Max(0.1f, m.Width - 0.4f), Math.Max(0.1f, m.Height - 0.4f))).ToList();
        var removed = Redactor.RemoveContent(current, regions, password);
        warnings.AddRange(removed.Warnings);
        current = removed.Pdf;

        foreach (var m in matches)
        {
            var region = new RectRegion(m.Page, m.X, m.Y, m.Width, m.Height);
            current = StampText(current, region, replacement, m.Height, password, wrap: false);
        }
        return (new EditResult(current, warnings), matches.Count);
    }

    private static byte[] StampText(byte[] pdf, RectRegion region, string text, float fontSize,
        string? password, bool wrap = true)
    {
        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            var page = doc.GetPage(region.Page);
            var font = PdfFontFactory.CreateFont(iText.IO.Font.Constants.StandardFonts.HELVETICA);
            var pdfCanvas = new PdfCanvas(page);
            if (wrap)
            {
                var box = new Rectangle(region.X, region.Y, region.Width, region.Height);
                using var canvas = new Canvas(pdfCanvas, box);
                canvas.Add(new Paragraph(text).SetFont(font).SetFontSize(fontSize)
                    .SetMargin(0).SetMultipliedLeading(1.05f)
                    .SetVerticalAlignment(VerticalAlignment.TOP));
            }
            else
            {
                // Single-line stamp on the original baseline (used by find & replace).
                float baseline = region.Y + fontSize * 0.21f; // approximate descender share
                pdfCanvas.BeginText().SetFontAndSize(font, fontSize)
                    .MoveText(region.X, baseline).ShowText(text).EndText();
            }
        }
        return output.ToArray();
    }

    // ------------------------------------------------------------ extraction

    private sealed record Chunk(string Text, Rectangle BBox, float FontHeight);

    private static List<Chunk> CollectChunks(PdfDocument doc, int pageNumber)
    {
        var chunks = new List<Chunk>();
        var listener = new ChunkListener(chunks);
        new PdfCanvasProcessor(listener).ProcessPageContent(doc.GetPage(pageNumber));
        return chunks;
    }

    private sealed class ChunkListener : IEventListener
    {
        private readonly List<Chunk> _chunks;
        public ChunkListener(List<Chunk> chunks) => _chunks = chunks;

        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is not TextRenderInfo t) return;
            foreach (var single in t.GetCharacterRenderInfos())
            {
                var asc = single.GetAscentLine();
                var desc = single.GetDescentLine();
                float minX = Math.Min(asc.GetStartPoint().Get(0), desc.GetStartPoint().Get(0));
                float maxX = Math.Max(asc.GetEndPoint().Get(0), desc.GetEndPoint().Get(0));
                float minY = desc.GetStartPoint().Get(1);
                float maxY = asc.GetStartPoint().Get(1);
                if (maxX <= minX) continue;
                _chunks.Add(new Chunk(single.GetText(),
                    new Rectangle(minX, minY, maxX - minX, maxY - minY), maxY - minY));
            }
        }

        public ICollection<EventType>? GetSupportedEvents() => null;
    }

    private static bool ContainsCenter(Rectangle region, Rectangle glyph)
    {
        float cx = glyph.GetLeft() + glyph.GetWidth() / 2;
        float cy = glyph.GetBottom() + glyph.GetHeight() / 2;
        return cx >= region.GetLeft() && cx <= region.GetRight() &&
               cy >= region.GetBottom() && cy <= region.GetTop();
    }

    private static string AssembleText(List<Chunk> chunks)
    {
        if (chunks.Count == 0) return string.Empty;
        // Group into lines by baseline proximity, then order left-to-right.
        var lines = new List<List<Chunk>>();
        foreach (var chunk in chunks.OrderByDescending(c => c.BBox.GetBottom()))
        {
            var line = lines.FirstOrDefault(l =>
                Math.Abs(l[0].BBox.GetBottom() - chunk.BBox.GetBottom()) < l[0].FontHeight * 0.6f);
            if (line == null) lines.Add(line = new List<Chunk>());
            line.Add(chunk);
        }
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length > 0) sb.Append('\n');
            Chunk? prev = null;
            foreach (var c in line.OrderBy(c => c.BBox.GetLeft()))
            {
                if (prev != null &&
                    c.BBox.GetLeft() - prev.BBox.GetRight() > prev.FontHeight * 0.25f)
                    sb.Append(' ');
                sb.Append(c.Text);
                prev = c;
            }
        }
        return sb.ToString();
    }
}
