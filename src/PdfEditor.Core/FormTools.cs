using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Pdf;

namespace PdfEditor.Core;

/// <summary>Reads and fills AcroForm (Adobe form) fields.</summary>
public static class FormTools
{
    /// <summary>Lists every fillable field with its type, current value, and allowed options.</summary>
    public static IReadOnlyList<FormField> ListFields(byte[] pdf, string? password = null)
    {
        using var doc = PdfIo.OpenReadOnly(pdf, password);
        var form = PdfFormCreator.GetAcroForm(doc, false);
        if (form == null) return Array.Empty<FormField>();

        var fields = new List<FormField>();
        foreach (var (name, field) in form.GetAllFormFields())
        {
            string type = FieldType(field);
            if (type == "container") continue; // non-terminal parent — not directly fillable
            bool readOnly = (field.GetFieldFlags() & PdfFormField.FF_READ_ONLY) != 0;
            fields.Add(new FormField(name, type, field.GetValueAsString() ?? "", Options(field), readOnly));
        }
        return fields;
    }

    /// <summary>
    /// Sets the given field values (keyed by fully-qualified field name). Unknown names are
    /// ignored. When <paramref name="flatten"/> is true the form is flattened afterwards so the
    /// values become static page content that can no longer be edited.
    /// </summary>
    public static EditResult FillFields(byte[] pdf, IReadOnlyDictionary<string, string> values,
        bool flatten = false, string? password = null)
    {
        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            var form = PdfFormCreator.GetAcroForm(doc, false);
            if (form != null)
            {
                var all = form.GetAllFormFields();
                foreach (var (name, value) in values)
                    if (all.TryGetValue(name, out var field)) field.SetValue(value);
                if (flatten) form.FlattenFields();
            }
        }
        return EditResult.Of(output.ToArray());
    }

    private static string FieldType(PdfFormField field)
    {
        var type = field.GetFormType();
        if (type == null) return "container";
        if (type.Equals(PdfName.Tx)) return "text";
        if (type.Equals(PdfName.Ch)) return "choice";
        if (type.Equals(PdfName.Sig)) return "signature";
        if (type.Equals(PdfName.Btn))
        {
            long flags = field.GetFieldFlags();
            if ((flags & PdfButtonFormField.FF_PUSH_BUTTON) != 0) return "button";
            return (flags & PdfButtonFormField.FF_RADIO) != 0 ? "radio" : "checkbox";
        }
        return "text";
    }

    /// <summary>Allowed values: choice /Opt entries, or a checkbox/radio's appearance states.</summary>
    private static IReadOnlyList<string> Options(PdfFormField field)
    {
        var opts = field.GetPdfObject().GetAsArray(PdfName.Opt);
        if (opts != null)
        {
            var list = new List<string>();
            foreach (var entry in opts)
            {
                if (entry is PdfString s) list.Add(s.ToUnicodeString());
                else if (entry is PdfArray pair && pair.Size() > 1 && pair.Get(1) is PdfString disp)
                    list.Add(disp.ToUnicodeString());
            }
            return list;
        }

        var type = field.GetFormType();
        if (type != null && type.Equals(PdfName.Btn))
        {
            var states = field.GetAppearanceStates();
            if (states != null && states.Length > 0) return states.ToList();
        }
        return Array.Empty<string>();
    }
}
