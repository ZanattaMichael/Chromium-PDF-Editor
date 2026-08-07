using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;

namespace PdfEditor.Core;

/// <summary>
/// Flattens interactive layers into static page content, with a choice of how much to flatten (#44):
/// form fields only, non-form annotations (comments/markup/stamps) only, or everything. Flattening
/// bakes the visible appearance into the page so it prints and renders identically everywhere and can
/// no longer be edited or removed as an object.
/// </summary>
public static class FlattenTool
{
    /// <summary>What to flatten.</summary>
    public enum Mode
    {
        /// <summary>AcroForm fields → their appearances; the fields stop being interactive.</summary>
        Forms,
        /// <summary>Markup/comment annotations (highlight, ink, stamp, note…) → page content.</summary>
        AnnotationsOnly,
        /// <summary>Both: forms and annotations, leaving a fully static page.</summary>
        Everything,
    }

    /// <summary>Subtypes that carry no bakeable page appearance and are left alone.</summary>
    private static readonly HashSet<PdfName> Skip = new() { PdfName.Link, PdfName.Popup };

    /// <summary>Flattens per <paramref name="mode"/>; returns the counts flattened.</summary>
    public static FlattenResult Flatten(byte[] pdf, Mode mode, string? password = null)
    {
        int forms = 0, annotations = 0;
        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            if (mode is Mode.Forms or Mode.Everything)
            {
                var form = PdfFormCreator.GetAcroForm(doc, false);
                if (form != null)
                {
                    forms = form.GetAllFormFields().Count;
                    if (forms > 0) form.FlattenFields(); // draws each field's appearance, drops the widgets
                }
            }

            if (mode is Mode.AnnotationsOnly or Mode.Everything)
                annotations = FlattenAnnotations(doc);
        }
        return new FlattenResult(output.ToArray(), forms, annotations);
    }

    /// <summary>
    /// Draws each non-form annotation's normal appearance onto its page and removes the annotation.
    /// Annotations with no bakeable appearance stream (or that are links/popups, or widgets — those
    /// are the form path) are left untouched, so nothing visible is silently dropped.
    /// </summary>
    private static int FlattenAnnotations(PdfDocument doc)
    {
        int count = 0;
        for (int i = 1; i <= doc.GetNumberOfPages(); i++)
        {
            var page = doc.GetPage(i);
            // Snapshot: RemoveAnnotation mutates the page's annotation list.
            var annotations = page.GetAnnotations().ToArray();
            PdfCanvas? canvas = null;
            foreach (var annot in annotations)
            {
                var subtype = annot.GetSubtype();
                if (subtype == null || subtype.Equals(PdfName.Widget) || Skip.Contains(subtype)) continue;

                var normal = annot.GetAppearanceDictionary()?.GetAsStream(PdfName.N);
                var rect = annot.GetRectangle()?.ToRectangle();
                if (normal == null || rect == null) continue; // nothing to bake at a known place

                // Lazily open a default-user-space canvas only when there is something to draw.
                canvas ??= PdfContentGuard.InDefaultUserSpace(page, doc);
                canvas.AddXObjectFittedIntoRectangle(new PdfFormXObject(normal), rect);
                page.RemoveAnnotation(annot);
                count++;
            }
        }
        return count;
    }
}

/// <summary>Result of a flatten run: the edited PDF and how many fields/annotations were baked in.</summary>
public sealed record FlattenResult(byte[] Pdf, int FormFieldsFlattened, int AnnotationsFlattened);
