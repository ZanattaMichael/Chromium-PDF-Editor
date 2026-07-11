using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Xobject;

namespace PdfEditor.Core;

/// <summary>
/// Rewrites a page's content stream, dropping every piece of content that falls inside
/// one of the supplied regions. Text is removed at per-string granularity (dropped strings
/// are replaced by equivalent-width TJ displacements so surrounding text does not shift),
/// image XObjects are dropped or pixel-scrubbed, and form XObjects are recursively edited
/// on a cloned copy. This achieves true removal: the data is gone from the file, not
/// merely covered.
/// </summary>
internal sealed class ContentStreamEditor : PdfCanvasProcessor
{
    private const float Epsilon = 0.05f;

    private readonly IList<Rectangle> _regions;
    private readonly PdfDocument _document;
    private readonly List<string> _warnings;
    private readonly CollectingListener _collector;
    private readonly int _depth;

    private PdfCanvas _canvas = null!;
    private PdfResources _editResources = null!;

    public bool RemovedAnything { get; private set; }

    private ContentStreamEditor(CollectingListener collector, IList<Rectangle> regions,
        PdfDocument document, List<string> warnings, int depth) : base(collector)
    {
        _collector = collector;
        _regions = regions;
        _document = document;
        _warnings = warnings;
        _depth = depth;
    }

    public static ContentStreamEditor Create(IList<Rectangle> regions, PdfDocument document,
        List<string> warnings, int depth = 0)
        => new(new CollectingListener(), regions, document, warnings, depth);

    /// <summary>Rewrites the content of <paramref name="page"/> in place.</summary>
    public void EditPage(PdfPage page)
    {
        var resources = page.GetResources();
        byte[] content = page.GetContentBytes();
        var newStream = (PdfStream)new PdfStream().MakeIndirect(_document);
        _canvas = new PdfCanvas(newStream, resources, _document);
        _editResources = resources;
        ProcessContent(content, resources);
        page.GetPdfObject().Put(PdfName.Contents, newStream);
        page.GetPdfObject().SetModified();
    }

    /// <summary>Rewrites the raw content of a form XObject stream in place.</summary>
    public void EditFormStream(PdfStream formStream, PdfResources resources)
    {
        byte[] content = formStream.GetBytes();
        var scratch = new PdfStream();
        _canvas = new PdfCanvas(scratch, resources, _document);
        _editResources = resources;
        ProcessContent(content, resources);
        formStream.SetData(_canvas.GetContentStream().GetBytes(false));
    }

    // Wrap every default operator so we can decide, per operator, whether to copy it through.
    public override IContentOperator RegisterContentOperator(string operatorString, IContentOperator op)
    {
        var wrapper = new OperatorWrapper(op);
        var former = base.RegisterContentOperator(operatorString, wrapper);
        return former is OperatorWrapper w ? w.Original : former;
    }

    private sealed class OperatorWrapper : IContentOperator
    {
        public IContentOperator? Original { get; }
        public OperatorWrapper(IContentOperator? original) => Original = original;

        public void Invoke(PdfCanvasProcessor processor, PdfLiteral oper, IList<PdfObject> operands)
        {
            var editor = (ContentStreamEditor)processor;
            switch (oper.ToString())
            {
                case "Tj":
                case "TJ":
                case "'":
                case "\"":
                    editor.HandleShowText(Original, oper, operands);
                    break;
                case "Do":
                    editor.HandleDo(Original, oper, operands);
                    break;
                case "EI":
                    editor.HandleInlineImage(Original, oper, operands);
                    break;
                default:
                    Original?.Invoke(processor, oper, operands);
                    editor.WriteOperands(operands);
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- text

    private void HandleShowText(IContentOperator? original, PdfLiteral oper, IList<PdfObject> operands)
    {
        _collector.Texts.Clear();
        original?.Invoke(this, oper, operands);
        var shown = _collector.Texts;

        bool anyHit = shown.Any(s => IntersectsAnyRegion(s.BBox));
        string opName = oper.ToString();

        if (!anyHit)
        {
            WriteOperands(operands);
            return;
        }
        RemovedAnything = true;

        // Re-emit the side effects of ' and " (line advance, word/char spacing), then
        // re-emit the show-text call in TJ form with hit strings replaced by
        // equivalent-width displacements.
        var os = _canvas.GetContentStream().GetOutputStream();
        switch (opName)
        {
            case "'":
                os.Write(new PdfLiteral("T*")).WriteNewLine();
                break;
            case "\"":
                os.Write(operands[0]).WriteSpace().Write(new PdfLiteral("Tw")).WriteNewLine();
                os.Write(operands[1]).WriteSpace().Write(new PdfLiteral("Tc")).WriteNewLine();
                os.Write(new PdfLiteral("T*")).WriteNewLine();
                break;
        }

        var sourceItems = new List<PdfObject>();
        if (opName == "TJ" && operands[0] is PdfArray arr)
            sourceItems.AddRange(arr);
        else if (opName == "\"")
            sourceItems.Add(operands[2]);
        else
            sourceItems.Add(operands[0]);

        int stringCount = sourceItems.Count(o => o is PdfString);
        var replacement = new PdfArray();
        if (stringCount == shown.Count)
        {
            int textIdx = 0;
            foreach (var item in sourceItems)
            {
                if (item is not PdfString)
                {
                    replacement.Add(item);
                    continue;
                }
                var info = shown[textIdx++];
                if (!IntersectsAnyRegion(info.BBox))
                {
                    replacement.Add(item);
                    continue;
                }
                // Per-glyph split: glyphs outside the region survive, glyphs inside are
                // replaced by an equivalent-width displacement so the line does not shift.
                foreach (var ch in info.Chars)
                {
                    if (IntersectsAnyRegion(ch.BBox))
                        replacement.Add(new PdfNumber(DisplacementFor(ch.UnscaledWidth, info)));
                    else
                        replacement.Add(ch.Str);
                }
            }
        }
        else
        {
            // Event/string count mismatch (unusual encodings). Fall back to dropping the
            // whole operator, preserving total advance so later text does not shift.
            double total = shown.Sum(s => (double)DisplacementFor(s.UnscaledWidth, s));
            replacement.Add(new PdfNumber(total));
        }

        os.Write(replacement).WriteSpace().Write(new PdfLiteral("TJ")).WriteNewLine();
    }

    private static float DisplacementFor(float unscaledWidth, ShownText info)
    {
        // A number n inside TJ translates the text position by -n/1000 * fontSize * hScale.
        float fs = info.FontSize;
        float th = info.HorizontalScaling;
        if (th > 5f) th /= 100f;          // normalise: stored as percent in some paths
        if (th <= 0f) th = 1f;
        if (fs == 0f) return 0f;
        return -unscaledWidth * 1000f / (fs * th);
    }

    // ------------------------------------------------------------- XObjects

    private void HandleDo(IContentOperator? original, PdfLiteral oper, IList<PdfObject> operands)
    {
        var name = (PdfName)operands[0];
        var xobjects = GetResources().GetResource(PdfName.XObject);
        var stream = xobjects?.GetAsStream(name);
        var subtype = stream?.GetAsName(PdfName.Subtype);

        if (stream == null)
        {
            WriteOperands(operands);
            return;
        }

        if (PdfName.Image.Equals(subtype))
        {
            _collector.Images.Clear();
            original?.Invoke(this, oper, operands);
            var bbox = _collector.Images.Count > 0 ? _collector.Images[0] : null;
            if (bbox != null && IntersectsAnyRegion(bbox))
            {
                RemovedAnything = true;
                if (ContainedInAnyRegion(bbox))
                    return; // fully covered: drop the draw call entirely
                if (ImageScrubber.TryScrubPixels(stream, bbox, _regions))
                {
                    WriteOperands(operands);
                    return;
                }
                _warnings.Add($"Image '{name.GetValue()}' partially overlaps a redaction " +
                              "region and could not be pixel-scrubbed; it was removed entirely.");
                return;
            }
            WriteOperands(operands);
            return;
        }

        if (PdfName.Form.Equals(subtype))
        {
            // Never let the base processor recurse into the form: our wrapped operators
            // would inline the form's content into the page stream. Handle it manually.
            var ctm = GetGraphicsState().GetCtm();
            var formMatrix = ReadMatrix(stream.GetAsArray(PdfName.Matrix));
            var full = formMatrix.Multiply(ctm);
            var formBBox = FormUserSpaceBBox(stream, full);
            if (formBBox != null && IntersectsAnyRegion(formBBox))
            {
                if (_depth >= 6)
                {
                    _warnings.Add("Form XObject nesting too deep; dropping the whole form inside the region.");
                    RemovedAnything = true;
                    return;
                }
                var cloned = (PdfStream)stream.Clone();
                cloned.MakeIndirect(_document);
                var formResources = new PdfResources(
                    cloned.GetAsDictionary(PdfName.Resources) ?? _editResources.GetPdfObject());
                var innerRegions = TransformRegionsInto(full);
                var inner = Create(innerRegions, _document, _warnings, _depth + 1);
                inner.EditFormStream(cloned, formResources);
                RemovedAnything |= inner.RemovedAnything;
                var newName = _editResources.AddForm(new PdfFormXObject(cloned));
                WriteOperands(new List<PdfObject> { newName, oper });
                return;
            }
            WriteOperands(operands);
            return;
        }

        WriteOperands(operands);
    }

    private void HandleInlineImage(IContentOperator? original, PdfLiteral oper, IList<PdfObject> operands)
    {
        _collector.Images.Clear();
        original?.Invoke(this, oper, operands);
        var bbox = _collector.Images.Count > 0 ? _collector.Images[0] : null;
        if (bbox != null && IntersectsAnyRegion(bbox))
        {
            RemovedAnything = true;
            return; // drop inline image touching a region (safe over-redaction)
        }
        if (operands[0] is PdfStream img)
            WriteInlineImage(img);
    }

    private void WriteInlineImage(PdfStream img)
    {
        var os = _canvas.GetContentStream().GetOutputStream();
        os.Write(new PdfLiteral("BI")).WriteNewLine();
        foreach (var key in img.KeySet())
        {
            if (PdfName.Length.Equals(key)) continue;
            os.Write(key).WriteSpace().Write(img.Get(key)).WriteNewLine();
        }
        os.Write(new PdfLiteral("ID")).WriteNewLine();
        os.WriteBytes(img.GetBytes(false));
        os.WriteNewLine().Write(new PdfLiteral("EI")).WriteNewLine();
    }

    // ------------------------------------------------------------- plumbing

    private void WriteOperands(IList<PdfObject> operands)
    {
        var os = _canvas.GetContentStream().GetOutputStream();
        for (int i = 0; i < operands.Count; i++)
        {
            os.Write(operands[i]);
            if (i < operands.Count - 1) os.WriteSpace();
            else os.WriteNewLine();
        }
    }

    private bool IntersectsAnyRegion(Rectangle r) => _regions.Any(reg => Overlaps(reg, r));

    private bool ContainedInAnyRegion(Rectangle r) => _regions.Any(reg =>
        reg.GetLeft() <= r.GetLeft() + Epsilon && reg.GetRight() >= r.GetRight() - Epsilon &&
        reg.GetBottom() <= r.GetBottom() + Epsilon && reg.GetTop() >= r.GetTop() - Epsilon);

    private static bool Overlaps(Rectangle a, Rectangle b) =>
        a.GetLeft() < b.GetRight() - Epsilon && b.GetLeft() < a.GetRight() - Epsilon &&
        a.GetBottom() < b.GetTop() - Epsilon && b.GetBottom() < a.GetTop() - Epsilon;

    private static Matrix ReadMatrix(PdfArray? arr)
    {
        if (arr == null || arr.Size() != 6) return new Matrix();
        return new Matrix(arr.GetAsNumber(0).FloatValue(), arr.GetAsNumber(1).FloatValue(),
            arr.GetAsNumber(2).FloatValue(), arr.GetAsNumber(3).FloatValue(),
            arr.GetAsNumber(4).FloatValue(), arr.GetAsNumber(5).FloatValue());
    }

    private static Rectangle? FormUserSpaceBBox(PdfStream form, Matrix fullMatrix)
    {
        var bbox = form.GetAsArray(PdfName.BBox);
        if (bbox == null || bbox.Size() != 4) return null;
        float llx = bbox.GetAsNumber(0).FloatValue(), lly = bbox.GetAsNumber(1).FloatValue();
        float urx = bbox.GetAsNumber(2).FloatValue(), ury = bbox.GetAsNumber(3).FloatValue();
        return TransformBBox(llx, lly, urx, ury, fullMatrix);
    }

    private static Rectangle TransformBBox(float llx, float lly, float urx, float ury, Matrix m)
    {
        var pts = new[]
        {
            TransformPoint(llx, lly, m), TransformPoint(urx, lly, m),
            TransformPoint(llx, ury, m), TransformPoint(urx, ury, m)
        };
        float minX = pts.Min(p => p.x), maxX = pts.Max(p => p.x);
        float minY = pts.Min(p => p.y), maxY = pts.Max(p => p.y);
        return new Rectangle(minX, minY, maxX - minX, maxY - minY);
    }

    private static (float x, float y) TransformPoint(float x, float y, Matrix m) =>
        (x * m.Get(Matrix.I11) + y * m.Get(Matrix.I21) + m.Get(Matrix.I31),
         x * m.Get(Matrix.I12) + y * m.Get(Matrix.I22) + m.Get(Matrix.I32));

    /// <summary>Maps the page-space regions into the coordinate space of a form XObject.</summary>
    private IList<Rectangle> TransformRegionsInto(Matrix formToUser)
    {
        float a = formToUser.Get(Matrix.I11), b = formToUser.Get(Matrix.I12);
        float c = formToUser.Get(Matrix.I21), d = formToUser.Get(Matrix.I22);
        float e = formToUser.Get(Matrix.I31), f = formToUser.Get(Matrix.I32);
        float det = a * d - b * c;
        if (Math.Abs(det) < 1e-9)
            return new List<Rectangle>();
        var inv = new Matrix(d / det, -b / det, -c / det, a / det,
            (c * f - d * e) / det, (b * e - a * f) / det);
        return _regions.Select(r =>
            TransformBBox(r.GetLeft(), r.GetBottom(), r.GetRight(), r.GetTop(), inv)).ToList();
    }

    // ------------------------------------------------------------- listener

    internal sealed record ShownChar(Rectangle BBox, float UnscaledWidth, PdfString Str);

    internal sealed record ShownText(Rectangle BBox, float UnscaledWidth, float FontSize,
        float HorizontalScaling, IReadOnlyList<ShownChar> Chars);

    private sealed class CollectingListener : IEventListener
    {
        public List<ShownText> Texts { get; } = new();
        public List<Rectangle> Images { get; } = new();

        public void EventOccurred(IEventData data, EventType type)
        {
            // Geometry must be captured immediately: render infos reference mutable
            // graphics state that changes as processing continues.
            if (data is TextRenderInfo t)
            {
                var chars = new List<ShownChar>();
                foreach (var c in t.GetCharacterRenderInfos())
                    chars.Add(new ShownChar(BBoxOf(c), c.GetUnscaledWidth(), c.GetPdfString()));
                Texts.Add(new ShownText(BBoxOf(t), t.GetUnscaledWidth(), t.GetFontSize(),
                    t.GetHorizontalScaling(), chars));
            }
            else if (data is ImageRenderInfo i)
            {
                var m = i.GetImageCtm();
                Images.Add(TransformBBox(0, 0, 1, 1, m));
            }
        }

        private static Rectangle BBoxOf(TextRenderInfo t)
        {
            var asc = t.GetAscentLine();
            var desc = t.GetDescentLine();
            float minX = Math.Min(Math.Min(asc.GetStartPoint().Get(0), asc.GetEndPoint().Get(0)),
                                  Math.Min(desc.GetStartPoint().Get(0), desc.GetEndPoint().Get(0)));
            float maxX = Math.Max(Math.Max(asc.GetStartPoint().Get(0), asc.GetEndPoint().Get(0)),
                                  Math.Max(desc.GetStartPoint().Get(0), desc.GetEndPoint().Get(0)));
            float minY = Math.Min(Math.Min(asc.GetStartPoint().Get(1), asc.GetEndPoint().Get(1)),
                                  Math.Min(desc.GetStartPoint().Get(1), desc.GetEndPoint().Get(1)));
            float maxY = Math.Max(Math.Max(asc.GetStartPoint().Get(1), asc.GetEndPoint().Get(1)),
                                  Math.Max(desc.GetStartPoint().Get(1), desc.GetEndPoint().Get(1)));
            return new Rectangle(minX, minY, maxX - minX, maxY - minY);
        }

        public ICollection<EventType>? GetSupportedEvents() => null;
    }
}
