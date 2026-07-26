using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;

namespace PdfEditor.Core;

/// <summary>
/// Authors document-level JavaScript (the /Names /JavaScript tree an Acrobat-style PDF runs when
/// it is opened). This is the deliberate, user-initiated counterpart to <see cref="PdfSafety"/>,
/// which detects and (by default) strips such scripts: a form author adds calculation/validation
/// logic here on purpose. The viewer only ever rasterises pages, so nothing added here executes
/// inside the editor — it runs in Acrobat/Chrome once the saved file is opened there.
/// </summary>
public static class JavaScriptTool
{
    /// <summary>
    /// Adds (or replaces, by name) a named document-level JavaScript. The name identifies the
    /// script in the document's JavaScript name tree; re-using a name overwrites that entry.
    /// </summary>
    public static EditResult AddDocumentScript(byte[] pdf, string name, string script,
        string? password = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A script name is required.", nameof(name));
        if (string.IsNullOrEmpty(script))
            throw new ArgumentException("The script is empty.", nameof(script));

        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            var action = PdfAction.CreateJavaScript(script);
            var tree = doc.GetCatalog().GetNameTree(PdfName.JavaScript);
            tree.AddEntry(name.Trim(), action.GetPdfObject());
            doc.GetCatalog().SetModified();
        }
        return EditResult.Of(output.ToArray());
    }

    /// <summary>Lists every named document-level JavaScript with its source text.</summary>
    public static IReadOnlyList<PdfScript> ListScripts(byte[] pdf, string? password = null)
    {
        using var doc = PdfIo.OpenReadOnly(pdf, password);
        var scripts = new List<PdfScript>();
        foreach (var (key, value) in doc.GetCatalog().GetNameTree(PdfName.JavaScript).GetNames())
        {
            string name = key.ToUnicodeString();
            scripts.Add(new PdfScript(name, ExtractSource(value)));
        }
        return scripts;
    }

    /// <summary>
    /// Removes the named document-level JavaScript, keeping any others. Removing an unknown name is
    /// a no-op (the returned document is unchanged apart from a normal rewrite).
    /// </summary>
    public static EditResult RemoveScript(byte[] pdf, string name, string? password = null)
    {
        var survivors = ListScripts(pdf, password).Where(s => s.Name != name).ToList();

        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            // Drop the whole JavaScript tree, then re-add the scripts we are keeping.
            doc.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Names)?.Remove(PdfName.JavaScript);
            if (survivors.Count > 0)
            {
                var tree = doc.GetCatalog().GetNameTree(PdfName.JavaScript);
                foreach (var s in survivors)
                    tree.AddEntry(s.Name, PdfAction.CreateJavaScript(s.Script).GetPdfObject());
            }
            doc.GetCatalog().SetModified();
        }
        return EditResult.Of(output.ToArray());
    }

    /// <summary>Reads the script source out of a JavaScript action (the /JS string or stream).</summary>
    private static string ExtractSource(PdfObject value) => value switch
    {
        PdfDictionary action => action.Get(PdfName.JS) switch
        {
            PdfString s => s.ToUnicodeString(),
            PdfStream st => System.Text.Encoding.UTF8.GetString(st.GetBytes()),
            _ => ""
        },
        _ => ""
    };
}
