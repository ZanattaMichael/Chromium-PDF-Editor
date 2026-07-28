using iText.Kernel.Pdf;

namespace PdfEditor.Core;

/// <summary>
/// Structural pre-checks that must run before a page's content is handed to iText's content
/// processor, for the failures iText itself does not defend against.
/// </summary>
internal static class PdfStructureGuard
{
    /// <summary>Deepest legitimate form-XObject nesting. Real documents use a handful of levels.</summary>
    private const int MaxFormNesting = 32;

    /// <summary>Upper bound on nodes inspected, so a wide (rather than deep) graph cannot stall the walk.</summary>
    private const int MaxFormsInspected = 4096;

    /// <summary>
    /// Rejects a page whose form-XObject graph does not terminate.
    /// <para>
    /// <c>PdfCanvasProcessor</c> follows every <c>/Do</c> into the referenced form XObject and
    /// processes its content recursively, with no cycle check and no depth limit. A document
    /// containing a form XObject that lists itself in its own <c>/Resources /XObject</c> therefore
    /// drives it into infinite recursion — and a <see cref="StackOverflowException"/> cannot be
    /// caught on .NET, so the whole host process dies. That is a one-file denial of service against
    /// anyone who opens a hostile PDF, and it has to be prevented rather than handled.
    /// </para>
    /// </summary>
    public static void EnsureFormXObjectsTerminate(PdfPage page)
    {
        var onPath = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        int budget = MaxFormsInspected;
        Walk(page.GetResources()?.GetResource(PdfName.XObject), onPath, 0, ref budget);
    }

    private static void Walk(PdfDictionary? xobjects, HashSet<PdfObject> onPath, int depth, ref int budget)
    {
        if (xobjects == null || budget <= 0) return;
        if (depth > MaxFormNesting)
            throw new InvalidDataException(
                $"This PDF could not be read: its form XObjects nest more than {MaxFormNesting} " +
                "levels deep, which no legitimate document does.");

        foreach (var key in xobjects.KeySet().ToList())
        {
            if (--budget <= 0) return;
            var form = xobjects.GetAsStream(key);
            if (form == null || !PdfName.Form.Equals(form.GetAsName(PdfName.Subtype))) continue;

            if (!onPath.Add(form))
                throw new InvalidDataException(
                    "This PDF could not be read: a form XObject draws itself, so its content never " +
                    "terminates. The document is malformed or deliberately hostile.");
            Walk(form.GetAsDictionary(PdfName.Resources)?.GetAsDictionary(PdfName.XObject),
                onPath, depth + 1, ref budget);
            onPath.Remove(form);
        }
    }
}
