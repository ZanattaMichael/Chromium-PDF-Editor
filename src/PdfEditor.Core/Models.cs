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
/// Basic geometry of a single page. <paramref name="X"/>/<paramref name="Y"/> are the
/// lower-left corner of the page box in PDF user space — non-zero when the page's
/// MediaBox/CropBox does not start at the origin, which the viewer must account for when
/// mapping screen coordinates back to the document (otherwise redactions land offset).
/// </summary>
public sealed record PageInfo(int Number, float X, float Y, float Width, float Height);

/// <summary>Summary of a loaded document.</summary>
public sealed record DocumentInfo(int PageCount, IReadOnlyList<PageInfo> Pages, bool IsEncrypted);

/// <summary>Text found inside a region, along with the dominant font size (user-space points).</summary>
public sealed record RegionText(string Text, float FontSize);

/// <summary>Result of an operation that rewrites the document.</summary>
public sealed record EditResult(byte[] Pdf, IReadOnlyList<string> Warnings)
{
    public static EditResult Of(byte[] pdf) => new(pdf, Array.Empty<string>());
}

/// <summary>State of a digital signature found in a document.</summary>
public sealed record SignatureInfo(string Name, string? SignerName, bool IntegrityValid, bool CoversWholeDocument);
