using iText.Kernel.Pdf;

namespace PdfEditor.Core;

/// <summary>
/// Detects and removes "active content" in a PDF: embedded JavaScript and actions that reach
/// outside the document (open a URL, launch a file, submit a form, jump to a remote file).
/// The viewer renders pages to images so nothing here ever executes — but the moment a document
/// is saved back out and opened in Acrobat/Chrome, these can run. They are surfaced and, until
/// the user explicitly keeps them, stripped on save.
/// </summary>
public static class PdfSafety
{
    // Action subtypes (/S) that reach outside the document.
    private static readonly PdfName[] UrlActions =
        { PdfName.URI, PdfName.Launch, PdfName.SubmitForm, PdfName.GoToR, PdfName.ImportData };

    public static SafetyReport Scan(byte[] pdf, string? password = null)
    {
        using var doc = PdfIo.OpenReadOnly(pdf, password);
        int js = 0, url = 0;
        var samples = new List<string>();
        var catalog = doc.GetCatalog().GetPdfObject();

        // Document-level JavaScript name tree (/Names /JavaScript).
        js += CountNameTreeJs(catalog.GetAsDictionary(PdfName.Names)?.GetAsDictionary(PdfName.JavaScript), samples);

        // Document open action + additional actions.
        Classify(catalog.Get(PdfName.OpenAction), ref js, ref url, samples);
        ClassifyAA(catalog.GetAsDictionary(PdfName.AA), ref js, ref url, samples);

        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
        {
            var page = doc.GetPage(i);
            ClassifyAA(page.GetPdfObject().GetAsDictionary(PdfName.AA), ref js, ref url, samples);
            foreach (var annot in page.GetAnnotations())
            {
                var ad = annot.GetPdfObject();
                Classify(ad.Get(PdfName.A), ref js, ref url, samples);
                ClassifyAA(ad.GetAsDictionary(PdfName.AA), ref js, ref url, samples);
            }
        }
        return new SafetyReport(js, url, samples.Take(12).ToList());
    }

    /// <summary>Removes all embedded JavaScript and outward-reaching actions (internal links kept).</summary>
    public static EditResult StripActive(byte[] pdf, string? password = null)
    {
        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            var catalog = doc.GetCatalog().GetPdfObject();
            catalog.GetAsDictionary(PdfName.Names)?.Remove(PdfName.JavaScript);
            RemoveIfActive(catalog, PdfName.OpenAction);
            catalog.Remove(PdfName.AA);

            for (int i = 1; i <= doc.GetNumberOfPages(); i++)
            {
                var page = doc.GetPage(i);
                page.GetPdfObject().Remove(PdfName.AA);
                foreach (var annot in page.GetAnnotations())
                {
                    var ad = annot.GetPdfObject();
                    RemoveIfActive(ad, PdfName.A);
                    ad.Remove(PdfName.AA);
                }
            }
        }
        return EditResult.Of(output.ToArray());
    }

    private static bool IsActive(PdfDictionary action)
    {
        var s = action.GetAsName(PdfName.S);
        if (s == null) return false;
        return s.Equals(PdfName.JavaScript) || UrlActions.Any(s.Equals);
    }

    private static void RemoveIfActive(PdfDictionary owner, PdfName key)
    {
        if (owner.Get(key) is PdfDictionary a && IsActive(a)) owner.Remove(key);
    }

    private static void Classify(PdfObject? obj, ref int js, ref int url, List<string> samples)
    {
        if (obj is PdfArray arr)
        {
            foreach (var e in arr) Classify(e, ref js, ref url, samples);
            return;
        }
        if (obj is not PdfDictionary a) return;

        var s = a.GetAsName(PdfName.S);
        if (s != null)
        {
            if (s.Equals(PdfName.JavaScript)) { js++; AddSample(samples, JsSample(a)); }
            else if (s.Equals(PdfName.URI))
            {
                url++;
                AddSample(samples, a.GetAsString(PdfName.URI)?.ToUnicodeString() ?? "URI");
            }
            else if (UrlActions.Any(s.Equals)) { url++; AddSample(samples, s.GetValue()); }
        }
        Classify(a.Get(PdfName.Next), ref js, ref url, samples); // chained actions
    }

    private static void ClassifyAA(PdfDictionary? aa, ref int js, ref int url, List<string> samples)
    {
        if (aa == null) return;
        foreach (var key in aa.KeySet().ToList()) Classify(aa.Get(key), ref js, ref url, samples);
    }

    private static int CountNameTreeJs(PdfDictionary? tree, List<string> samples)
    {
        if (tree == null) return 0;
        int count = 0;
        var names = tree.GetAsArray(PdfName.Names);
        if (names != null)
            for (int i = 1; i < names.Size(); i += 2)
            {
                count++;
                if (names.Get(i) is PdfDictionary d) AddSample(samples, JsSample(d));
            }
        var kids = tree.GetAsArray(PdfName.Kids);
        if (kids != null)
            foreach (var kid in kids)
                if (kid is PdfDictionary kd) count += CountNameTreeJs(kd, samples);
        return count;
    }

    private static string JsSample(PdfDictionary action)
    {
        var js = action.Get(PdfName.JS);
        string text = js switch
        {
            PdfString s => s.ToUnicodeString(),
            PdfStream st => System.Text.Encoding.UTF8.GetString(st.GetBytes()),
            _ => "JavaScript"
        };
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return "JS: " + (text.Length > 80 ? text[..80] + "…" : text);
    }

    private static void AddSample(List<string> samples, string sample)
    {
        if (samples.Count < 12 && !samples.Contains(sample)) samples.Add(sample);
    }
}
