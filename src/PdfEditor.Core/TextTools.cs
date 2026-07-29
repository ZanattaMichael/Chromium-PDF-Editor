using System.Text;
using iText.IO.Font.Constants;
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
    /// <summary>Returns the text inside a region plus its dominant font size and style.</summary>
    public static RegionText GetTextInRegion(byte[] pdf, RectRegion region, string? password = null)
    {
        using var doc = PdfIo.OpenReadOnly(pdf, password);
        var rect = new Rectangle(region.X, region.Y, region.Width, region.Height);
        var chunks = CollectChunks(doc, region.Page).Where(c => ContainsCenter(rect, c.BBox)).ToList();
        string dominantFont = chunks
            .Where(c => !string.IsNullOrEmpty(c.FontName))
            .GroupBy(c => c.FontName, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key ?? "";
        // Take the size from the run the region is mostly made of, so a single stray large glyph
        // cannot decide the size for a paragraph — and so size and family describe the same run.
        var sizing = chunks.Where(c => string.Equals(c.FontName, dominantFont, StringComparison.Ordinal)).ToList();
        if (sizing.Count == 0) sizing = chunks;
        float size = sizing.Count == 0 ? 12f : MathF.Round(sizing.Max(c => c.FontSize), 2);
        var (family, bold, italic) = DetectFont(dominantFont);
        return new RegionText(AssembleText(chunks), size, family, bold, italic, dominantFont);
    }

    /// <summary>
    /// Replaces the text inside a region: original text operators are removed from the
    /// file and the new text is laid out inside the same rectangle, in the requested font,
    /// size, style, and colour (all optional — omitted values fall back to what was there).
    /// </summary>
    public static EditResult ReplaceTextInRegion(byte[] pdf, RectRegion region, string newText,
        float? fontSize = null, string? fontFamily = null, bool? bold = null, bool? italic = null,
        string? colorHex = null, string? password = null)
    {
        var found = GetTextInRegion(pdf, region, password);
        float size = fontSize ?? found.FontSize;
        // Family and style fall back to the run being replaced, as the summary above promises. They
        // used to default to plain Helvetica instead, so any caller that named a size but no face
        // silently reset bold Times body copy to regular Helvetica (#29).
        string stampFont = ResolveFont(fontFamily ?? found.FontFamily,
            bold ?? found.Bold, italic ?? found.Italic);

        var removed = Redactor.RemoveContent(pdf, new[] { region }, password);
        var stamped = StampText(removed.Pdf, region, newText, size, password,
            fontName: stampFont, color: ParseColor(colorHex));

        var warnings = new List<string>(removed.Warnings);
        if (DescribeSubstitution(found.SourceFont, stampFont) is { } note) warnings.Add(note);
        return new EditResult(stamped, warnings);
    }

    /// <summary>
    /// Reports, in words, when replacement text will not be set in the font the original was set
    /// in. Only the standard-14 faces can be stamped today, so editing a run in any other font is a
    /// silent change to how the document looks — exactly the kind of quiet substitution a user needs
    /// told about rather than left to notice. Returns null when the original face is reproduced.
    /// </summary>
    /// <remarks>
    /// Reusing the embedded program itself is issue #34; widening the stampable set is #28. Until
    /// then the honest thing is to say what was swapped for what.
    /// </remarks>
    internal static string? DescribeSubstitution(string? sourceFont, string stampFont)
    {
        string original = StripSubsetPrefix(sourceFont);
        if (original.Length == 0) return null;                                   // nothing detected
        if (string.Equals(original, stampFont, StringComparison.OrdinalIgnoreCase)) return null;
        return $"The original font '{original}' cannot be embedded by the editor, so the replacement "
            + $"text was substituted with '{stampFont}'. Spacing and letterforms will differ.";
    }

    /// <summary>Drops the six-letter subset tag PDF writers prepend (e.g. <c>ABCDEF+Calibri</c>).</summary>
    internal static string StripSubsetPrefix(string? fontName)
    {
        string name = (fontName ?? "").Trim();
        return name.Length > 7 && name[6] == '+' ? name[7..] : name;
    }

    /// <summary>
    /// Moves the text found in <paramref name="source"/> by (<paramref name="dx"/>,
    /// <paramref name="dy"/>) in PDF user space: the original text is removed and re-stamped at the
    /// shifted position, preserving its detected font, size, and style. A no-op if the region holds
    /// no text.
    /// </summary>
    public static EditResult MoveText(byte[] pdf, RectRegion source, float dx, float dy, string? password = null)
    {
        var found = GetTextInRegion(pdf, source, password);
        if (string.IsNullOrWhiteSpace(found.Text)) return EditResult.Of(pdf);

        var removed = Redactor.RemoveContent(pdf, new[] { source }, password);
        var dest = new RectRegion(source.Page, source.X + dx, source.Y + dy, source.Width, source.Height);
        var stamped = StampText(removed.Pdf, dest, found.Text, found.FontSize, password,
            fontName: ResolveFont(found.FontFamily, found.Bold, found.Italic), wrap: false);
        return new EditResult(stamped, removed.Warnings);
    }

    /// <summary>
    /// Adds new text on top of the page inside <paramref name="region"/> (wrapped to its width),
    /// without touching any existing content. Used by the "add text anywhere" tool.
    /// </summary>
    public static EditResult AddText(byte[] pdf, RectRegion region, string text, float fontSize,
        string? fontFamily = null, bool bold = false, bool italic = false,
        string? colorHex = null, string? password = null)
    {
        var stamped = StampText(pdf, region, text, fontSize, password,
            fontName: ResolveFont(fontFamily, bold, italic), color: ParseColor(colorHex));
        return EditResult.Of(stamped);
    }

    /// <summary>Maps a family name (helvetica/times/courier) + style to a standard-14 PDF font.</summary>
    internal static string ResolveFont(string? family, bool bold, bool italic)
    {
        switch ((family ?? "helvetica").Trim().ToLowerInvariant())
        {
            case "times":
            case "serif":
                return bold && italic ? StandardFonts.TIMES_BOLDITALIC
                    : bold ? StandardFonts.TIMES_BOLD
                    : italic ? StandardFonts.TIMES_ITALIC
                    : StandardFonts.TIMES_ROMAN;
            case "courier":
            case "mono":
            case "monospace":
                return bold && italic ? StandardFonts.COURIER_BOLDOBLIQUE
                    : bold ? StandardFonts.COURIER_BOLD
                    : italic ? StandardFonts.COURIER_OBLIQUE
                    : StandardFonts.COURIER;
            default: // helvetica / sans-serif
                return bold && italic ? StandardFonts.HELVETICA_BOLDOBLIQUE
                    : bold ? StandardFonts.HELVETICA_BOLD
                    : italic ? StandardFonts.HELVETICA_OBLIQUE
                    : StandardFonts.HELVETICA;
        }
    }

    /// <summary>Best-effort read of a font's PostScript name into family + bold/italic flags.</summary>
    internal static (string Family, bool Bold, bool Italic) DetectFont(string? postScriptName)
    {
        string n = (postScriptName ?? "").ToLowerInvariant();
        string family =
            n.Contains("times") || n.Contains("serif") || n.Contains("georgia") || n.Contains("roman") || n.Contains("minion") ? "times"
            : n.Contains("courier") || n.Contains("mono") || n.Contains("consol") ? "courier"
            : "helvetica";
        bool bold = n.Contains("bold") || n.Contains("black") || n.Contains("heavy") || n.Contains("semibold");
        bool italic = n.Contains("italic") || n.Contains("oblique");
        return (family, bold, italic);
    }

    internal static iText.Kernel.Colors.Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        string h = hex.Trim().TrimStart('#');
        if (h.Length != 6 ||
            !int.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out int rgb))
            return null;
        return new iText.Kernel.Colors.DeviceRgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
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
            int page = p;
            PdfIo.Guarded($"searching page {page}", () =>
            {
                PdfStructureGuard.EnsureFormXObjectsTerminate(doc.GetPage(page));
                new PdfCanvasProcessor(strategy).ProcessPageContent(doc.GetPage(page));
            });
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
        string? password, bool wrap = true, string? fontName = null,
        iText.Kernel.Colors.Color? color = null)
    {
        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            var page = doc.GetPage(region.Page);
            var font = PdfFontFactory.CreateFont(fontName ?? StandardFonts.HELVETICA);
            // Draw in the page's default user space so the stamped text isn't thrown off by a
            // leftover transform the page content leaves active (e.g. Chrome / Google Docs exports).
            var pdfCanvas = PdfContentGuard.InDefaultUserSpace(page, doc);
            if (wrap)
            {
                var box = new Rectangle(region.X, region.Y, region.Width, region.Height);
                using var canvas = new Canvas(pdfCanvas, box);
                var paragraph = new Paragraph(text).SetFont(font).SetFontSize(fontSize)
                    .SetMargin(0).SetMultipliedLeading(1.05f)
                    .SetVerticalAlignment(VerticalAlignment.TOP);
                if (color != null) paragraph.SetFontColor(color);
                canvas.Add(paragraph);
            }
            else
            {
                // Single-line stamp on the original baseline (used by find & replace).
                float baseline = region.Y + fontSize * 0.21f; // approximate descender share
                pdfCanvas.BeginText().SetFontAndSize(font, fontSize);
                if (color != null) pdfCanvas.SetFillColor(color);
                pdfCanvas.MoveText(region.X, baseline).ShowText(text).EndText();
            }
        }
        return output.ToArray();
    }

    /// <summary>
    /// Returns each run of text on a page with its bounding box in PDF user space. Used to build
    /// the viewer's selectable text layer. Runs (not individual glyphs) keep the layer light.
    /// </summary>
    public static IReadOnlyList<TextSpan> GetTextSpans(byte[] pdf, int page, string? password = null)
    {
        using var doc = PdfIo.OpenReadOnly(pdf, password);
        if (page < 1 || page > doc.GetNumberOfPages()) return Array.Empty<TextSpan>();
        var spans = new List<TextSpan>();
        PdfIo.Guarded($"reading the text layout of page {page}", () =>
        {
            PdfStructureGuard.EnsureFormXObjectsTerminate(doc.GetPage(page));
            new PdfCanvasProcessor(new SpanListener(spans)).ProcessPageContent(doc.GetPage(page));
        });
        return spans;
    }

    private sealed class SpanListener : IEventListener
    {
        private readonly List<TextSpan> _spans;
        public SpanListener(List<TextSpan> spans) => _spans = spans;

        public void EventOccurred(IEventData data, EventType type)
        {
            if (data is not TextRenderInfo t) return;
            string text = t.GetText();
            if (string.IsNullOrWhiteSpace(text)) return;
            var asc = t.GetAscentLine();
            var desc = t.GetDescentLine();
            float x0 = desc.GetStartPoint().Get(0), x1 = desc.GetEndPoint().Get(0);
            float yBottom = desc.GetStartPoint().Get(1), yTop = asc.GetStartPoint().Get(1);
            float minX = Math.Min(x0, x1), maxX = Math.Max(x0, x1);
            if (maxX <= minX || yTop <= yBottom) return; // skip zero-area / vertical runs
            _spans.Add(new TextSpan(text, minX, yBottom, maxX - minX, yTop - yBottom));
        }

        public ICollection<EventType>? GetSupportedEvents() => null;
    }

    // ------------------------------------------------------------ extraction

    private sealed record Chunk(string Text, Rectangle BBox, float FontHeight, float FontSize, string FontName);

    /// <summary>
    /// Recovers the type size a run was set in from the height of its transformed
    /// ascender-to-descender box.
    /// <para>
    /// The box is <em>not</em> the em: it spans only <c>(ascender - descender) / 1000</c> of it —
    /// 0.925 for Helvetica, 0.900 for Times, 0.786 for Courier. Treating the box height as the font
    /// size (which this code did until #29) re-stamped every edited run 7–21% smaller than the
    /// original, and because each edit re-measured its own undersized output the error compounded:
    /// three passes over 24pt Courier left it under 12pt.
    /// </para>
    /// <para>
    /// Working back from the box rather than from the <c>Tf</c> operand is deliberate — the box has
    /// the text matrix and CTM already applied, so a run scaled by a <c>Tm</c>/<c>cm</c> yields the
    /// size it is drawn at, which is the size the replacement has to be stamped at.
    /// </para>
    /// </summary>
    /// <param name="boxHeight">Transformed ascent-line to descent-line distance.</param>
    /// <param name="ascender">Font ascender, normalised to a 1000-unit em.</param>
    /// <param name="descender">Font descender (negative), normalised to a 1000-unit em.</param>
    internal static float EmSizeFromBoxHeight(float boxHeight, float ascender, float descender)
    {
        float span = (ascender - descender) / 1000f;
        // Fonts that report no usable vertical metrics — Type 3 faces, undecodable embedded
        // programs — leave the box height as the only estimate there is. Guarding on a plausible
        // range also keeps a zero or absurd span from producing an infinity or a NaN.
        if (!float.IsFinite(span) || span < 0.4f || span > 2f) return boxHeight;
        return boxHeight / span;
    }

    /// <summary>Vertical metrics of a run's font, normalised to a 1000-unit em; (0,0) if unusable.</summary>
    private static (float Ascender, float Descender) VerticalMetrics(PdfFont? font)
    {
        try
        {
            var metrics = font?.GetFontProgram()?.GetFontMetrics();
            return metrics == null ? (0f, 0f) : (metrics.GetTypoAscender(), metrics.GetTypoDescender());
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException
                                       or iText.Kernel.Exceptions.PdfException)
        {
            return (0f, 0f); // same fallback as a font with no metrics at all
        }
    }

    private static List<Chunk> CollectChunks(PdfDocument doc, int pageNumber)
    {
        var chunks = new List<Chunk>();
        var listener = new ChunkListener(chunks);
        PdfIo.Guarded($"extracting text from page {pageNumber}", () =>
        {
            PdfStructureGuard.EnsureFormXObjectsTerminate(doc.GetPage(pageNumber));
            new PdfCanvasProcessor(listener).ProcessPageContent(doc.GetPage(pageNumber));
        });
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
                string fontName = "";
                PdfFont? font = null;
                try
                {
                    font = single.GetFont();
                    fontName = font?.GetFontProgram()?.GetFontNames()?.GetFontName() ?? "";
                }
                catch { /* some embedded fonts expose no usable name; family detection just falls back */ }
                float boxHeight = maxY - minY;
                var (ascender, descender) = VerticalMetrics(font);
                _chunks.Add(new Chunk(single.GetText(),
                    new Rectangle(minX, minY, maxX - minX, boxHeight), boxHeight,
                    EmSizeFromBoxHeight(boxHeight, ascender, descender), fontName));
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
