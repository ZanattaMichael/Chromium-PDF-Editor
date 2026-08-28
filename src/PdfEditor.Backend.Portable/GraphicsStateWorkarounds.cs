using PdfSharp.Pdf;

namespace PdfEditor.Backend.Portable;

/// <summary>
/// The compositing primitives OfficeIMO does not expose.
///
/// Highlighting needs <c>/BM /Multiply</c> so the highlight darkens the text beneath it rather than
/// covering it; watermarking needs constant alpha. Neither is reachable through OfficeIMO's public
/// API — it has no blend-mode surface at all, and <c>PdfTextStampOptions</c> has no opacity — so
/// both are written directly into the page's <c>/ExtGState</c> resources here, and referenced from
/// a content stream with the returned name.
/// </summary>
public static class GraphicsStateWorkarounds
{
    /// <summary>PDF blend modes this project uses. The spec's full set is larger.</summary>
    public const string Multiply = "Multiply";
    public const string Normal = "Normal";

    /// <summary>
    /// Registers a graphics state on <paramref name="page"/> and returns the resource name to emit
    /// before the drawing operators, e.g. <c>/PdfEditGS1 gs</c>.
    /// </summary>
    /// <param name="blendMode">A PDF blend mode name without the leading slash, or null to leave it unset.</param>
    /// <param name="alpha">Fill and stroke alpha in 0..1.</param>
    public static string Register(PdfDocument document, PdfPage page, string? blendMode = null, double alpha = 1.0)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(page);
        if (alpha is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "Alpha must be within 0..1.");

        var state = new PdfDictionary(document);
        state.Elements["/Type"] = new PdfName("/ExtGState");
        if (blendMode is not null) state.Elements["/BM"] = new PdfName("/" + blendMode);
        state.Elements["/ca"] = new PdfReal(alpha);
        state.Elements["/CA"] = new PdfReal(alpha);
        document.Internals.AddObject(state);

        PdfDictionary resources = page.Elements.GetDictionary("/Resources")
            ?? throw new InvalidOperationException("The page has no /Resources dictionary.");

        PdfDictionary? table = resources.Elements.GetDictionary("/ExtGState");
        if (table is null)
        {
            table = new PdfDictionary(document);
            document.Internals.AddObject(table);
            resources.Elements["/ExtGState"] = table.Reference;
        }

        // Names must not collide with anything the producer already registered.
        string name;
        int index = table.Elements.Count + 1;
        do { name = "/PdfEditGS" + index++; } while (table.Elements.ContainsKey(name));

        table.Elements[name] = state.Reference;
        return name;
    }

    /// <summary>
    /// A ready-made Multiply highlight over <paramref name="rect"/> in default user space, matching
    /// HighlightTool's current appearance. The page is normalized first, without which the box
    /// lands in whatever space the producer leaked.
    /// </summary>
    public static string BuildHighlight(PdfDocument document, PdfPage page, PdfRectangle rect, double r, double g, double b)
    {
        ContentStreamGuard.Normalize(page);
        string gs = Register(document, page, Multiply);
        return $"q\n{gs} gs\n{F(r)} {F(g)} {F(b)} rg\n" +
               $"{F(rect.X1)} {F(rect.Y1)} {F(rect.X2 - rect.X1)} {F(rect.Y2 - rect.Y1)} re f\nQ\n";
    }

    static string F(double value) => value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}
