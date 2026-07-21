namespace PdfEditor.Core;

/// <summary>
/// A rectangular region on a page, expressed in PDF user space
/// (points, origin at the bottom-left corner of the page).
/// </summary>
public sealed record RectRegion(int Page, float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Top => Y + Height;
}

/// <summary>A located occurrence of a text phrase.</summary>
public sealed record TextMatch(int Page, string Text, float X, float Y, float Width, float Height);

/// <summary>
/// A run of text on a page with its bounding box in PDF user space — used to build the viewer's
/// invisible, selectable text layer so users can select and copy real text off the rendered image.
/// </summary>
public sealed record TextSpan(string Text, float X, float Y, float Width, float Height);

/// <summary>
/// Geometry of a single page, describing exactly the box that gets rendered so the viewer
/// can map screen coordinates back to the document. <paramref name="X"/>/<paramref name="Y"/>
/// are the lower-left corner of the (crop) box in PDF user space — non-zero when it does not
/// start at the origin. <paramref name="Width"/>/<paramref name="Height"/> are the box size
/// in that space (before any rotation). <paramref name="Rotation"/> is the page's clockwise
/// display rotation (0/90/180/270); at 90/270 the rendered image is width/height-swapped.
/// Ignoring the crop box or the rotation makes redactions land in the wrong place.
/// </summary>
public sealed record PageInfo(int Number, float X, float Y, float Width, float Height, int Rotation);

/// <summary>Summary of a loaded document.</summary>
public sealed record DocumentInfo(int PageCount, IReadOnlyList<PageInfo> Pages, bool IsEncrypted);

/// <summary>
/// Text found inside a region, with the dominant font size (user-space points) and a
/// best-effort read of the dominant font: family (<c>helvetica</c>/<c>times</c>/<c>courier</c>)
/// and whether it is bold/italic — used to pre-fill the edit controls.
/// </summary>
public sealed record RegionText(string Text, float FontSize, string FontFamily, bool Bold, bool Italic);

/// <summary>Result of an operation that rewrites the document.</summary>
public sealed record EditResult(byte[] Pdf, IReadOnlyList<string> Warnings)
{
    public static EditResult Of(byte[] pdf) => new(pdf, Array.Empty<string>());
}

/// <summary>State of a digital signature found in a document.</summary>
public sealed record SignatureInfo(string Name, string? SignerName, bool IntegrityValid, bool CoversWholeDocument);

/// <summary>
/// A fillable AcroForm field. <paramref name="Type"/> is one of text/checkbox/radio/choice/
/// signature/button. <paramref name="Options"/> lists the allowed values for checkbox/radio/choice
/// fields (empty otherwise).
/// </summary>
public sealed record FormField(string Name, string Type, string Value,
    IReadOnlyList<string> Options, bool ReadOnly);

/// <summary>
/// Result of scanning a document for active content. <paramref name="Samples"/> holds a few
/// human-readable examples (script snippets / URLs) for display — never executed.
/// </summary>
public sealed record SafetyReport(int JavaScriptCount, int UrlCount, IReadOnlyList<string> Samples)
{
    public bool HasJavaScript => JavaScriptCount > 0;
    public bool HasUrlActions => UrlCount > 0;
    public bool HasActiveContent => HasJavaScript || HasUrlActions;
}

/// <summary>A link URL found in the document, with the page it appears on.</summary>
public sealed record PdfLink(int Page, string Url);

/// <summary>
/// A safety assessment for one URL. <paramref name="Level"/> is the traffic-light rating
/// (<c>green</c>/<c>yellow</c>/<c>red</c>/<c>unknown</c>), <paramref name="Category"/> a short
/// reason (e.g. <c>known-site</c>, <c>code-hosting</c>, <c>file-hosting</c>, <c>malicious</c>),
/// and <paramref name="Source"/> whether the verdict came from the local <c>heuristic</c> or
/// the <c>cloudflare</c> URL Scanner.
/// </summary>
public sealed record UrlVerdict(int Page, string Url, string Level, string Category,
    string Source, string? Detail = null);
