using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PdfEditor.Backend.Portable;

/// <summary>
/// Rejects a page whose form-XObject graph does not terminate — the portable-backend counterpart of
/// PdfEditor.Core's PdfStructureGuard.
///
/// A content processor follows every <c>/Do</c> into the referenced form XObject and processes it
/// recursively. A form XObject that lists itself in its own <c>/Resources /XObject</c> therefore
/// drives that walk into infinite recursion, and a <see cref="StackOverflowException"/> cannot be
/// caught on .NET — the host process simply dies. That is a one-file denial of service against
/// anyone who opens a hostile PDF, so it has to be prevented rather than handled.
///
/// The check needs to enumerate raw dictionaries, which is precisely the access OfficeIMO keeps
/// internal; PdfSharp exposes it.
/// </summary>
public static class FormXObjectGuard
{
    /// <summary>Deepest legitimate nesting. Real documents use a handful of levels.</summary>
    public const int MaxFormNesting = 32;

    /// <summary>Upper bound on nodes inspected, so a wide rather than deep graph cannot stall the walk.</summary>
    public const int MaxFormsInspected = 4096;

    public static void EnsureFormXObjectsTerminate(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var onPath = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        int budget = MaxFormsInspected;
        Walk(page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject"), onPath, 0, ref budget);
    }

    public static void EnsureAllPagesTerminate(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        for (int i = 0; i < document.PageCount; i++)
            EnsureFormXObjectsTerminate(document.Pages[i]);
    }

    static void Walk(PdfDictionary? xobjects, HashSet<PdfObject> onPath, int depth, ref int budget)
    {
        if (xobjects is null || budget <= 0) return;
        if (depth > MaxFormNesting)
            throw new InvalidDataException(
                $"This PDF could not be read: its form XObjects nest more than {MaxFormNesting} " +
                "levels deep, which no legitimate document does.");

        foreach (string key in xobjects.Elements.Keys.ToList())
        {
            if (--budget <= 0) return;

            PdfDictionary? form = Resolve(xobjects.Elements[key]);
            if (form is null) continue;
            if (form.Elements.GetName("/Subtype") != "/Form") continue;

            if (!onPath.Add(form))
                throw new InvalidDataException(
                    "This PDF could not be read: a form XObject draws itself, so its content never " +
                    "terminates. The document is malformed or deliberately hostile.");

            Walk(form.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject"),
                onPath, depth + 1, ref budget);
            onPath.Remove(form);
        }
    }

    // A cycle is only detectable on the shared underlying object, so references must be followed to
    // the object they name rather than compared as references.
    static PdfDictionary? Resolve(PdfItem? item) => item switch
    {
        PdfReference reference => reference.Value as PdfDictionary,
        PdfDictionary dictionary => dictionary,
        _ => null,
    };
}
