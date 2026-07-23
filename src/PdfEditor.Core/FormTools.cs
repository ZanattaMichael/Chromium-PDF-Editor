using iText.Forms;
using iText.Forms.Fields;
using iText.Forms.Fields.Properties;
using iText.IO.Font.Constants;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Canvas;

namespace PdfEditor.Core;

/// <summary>Reads, fills, and inserts AcroForm (Adobe form) fields.</summary>
public static class FormTools
{
    /// <summary>
    /// Inserts a fillable text field on the given page at the given rectangle. When
    /// <paramref name="multiline"/> is true the field accepts multiple lines (a comment/notes box).
    /// </summary>
    public static EditResult AddTextField(byte[] pdf, int page, RectRegion rect, string? name = null,
        string? value = null, string? password = null, bool multiline = false)
    {
        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            if (page < 1 || page > doc.GetNumberOfPages())
                throw new ArgumentOutOfRangeException(nameof(page), $"Page {page} does not exist.");
            var form = PdfFormCreator.GetAcroForm(doc, true);
            var field = new TextFormFieldBuilder(doc, UniqueName(form, name, "text"))
                .SetWidgetRectangle(new Rectangle(rect.X, rect.Y, rect.Width, rect.Height))
                .SetPage(page).CreateText();
            if (multiline) field.SetMultiline(true);
            field.SetValue(value ?? "");
            StyleWidget(field);
            form.AddField(field);
        }
        return EditResult.Of(output.ToArray());
    }

    /// <summary>
    /// Inserts a dropdown (combo box) choice field with the given selectable options. The first
    /// option is preselected; the user picks one when filling the form.
    /// </summary>
    public static EditResult AddDropdown(byte[] pdf, int page, RectRegion rect, string? name,
        IReadOnlyList<string> options, string? password = null)
    {
        var choices = options.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).ToArray();
        if (choices.Length == 0)
            throw new ArgumentException("A dropdown needs at least one option.", nameof(options));

        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            if (page < 1 || page > doc.GetNumberOfPages())
                throw new ArgumentOutOfRangeException(nameof(page), $"Page {page} does not exist.");
            var form = PdfFormCreator.GetAcroForm(doc, true);
            var field = new ChoiceFormFieldBuilder(doc, UniqueName(form, name, "choice"))
                .SetWidgetRectangle(new Rectangle(rect.X, rect.Y, rect.Width, rect.Height))
                .SetPage(page).SetOptions(choices).CreateComboBox();
            field.SetValue(choices[0]);
            StyleWidget(field);
            form.AddField(field);
        }
        return EditResult.Of(output.ToArray());
    }

    /// <summary>
    /// Inserts a radio-button ("option") group: one field with a button per option, stacked
    /// vertically inside the rectangle with each option's label drawn beside it. Exactly one option
    /// can be selected; the first is selected by default. Needs at least two options.
    /// </summary>
    public static EditResult AddRadioGroup(byte[] pdf, int page, RectRegion rect, string? name,
        IReadOnlyList<string> options, string? password = null)
    {
        var choices = options.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim())
            .Distinct().ToArray();
        if (choices.Length < 2)
            throw new ArgumentException("A radio/option group needs at least two options.", nameof(options));

        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            if (page < 1 || page > doc.GetNumberOfPages())
                throw new ArgumentOutOfRangeException(nameof(page), $"Page {page} does not exist.");
            var pdfPage = doc.GetPage(page);
            var form = PdfFormCreator.GetAcroForm(doc, true);
            var builder = new RadioFormFieldBuilder(doc, UniqueName(form, name, "radio"));
            var group = builder.CreateRadioGroup();

            const float box = 14f;
            float rowHeight = Math.Max(box + 4f, Math.Min(28f, rect.Height / choices.Length));
            float top = rect.Y + rect.Height;
            var canvas = new PdfCanvas(pdfPage);
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            for (int i = 0; i < choices.Length; i++)
            {
                float y = top - (i + 1) * rowHeight + (rowHeight - box) / 2f;
                var radio = builder.CreateRadioButton(choices[i], new Rectangle(rect.X, y, box, box));
                radio.SetBorderWidth(1);
                radio.SetBorderColor(ColorConstants.GRAY);
                radio.SetBackgroundColor(FieldFill);
                group.AddKid(radio);
                canvas.BeginText().SetFontAndSize(font, 11)
                    .MoveText(rect.X + box + 6, y + 2).ShowText(choices[i]).EndText();
            }
            group.SetValue(choices[0]); // first option selected by default
            form.AddField(group, pdfPage);
        }
        return EditResult.Of(output.ToArray());
    }

    /// <summary>Inserts a checkbox on the given page at the given rectangle.</summary>
    public static EditResult AddCheckbox(byte[] pdf, int page, RectRegion rect, string? name = null,
        bool isChecked = false, string? password = null)
    {
        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            if (page < 1 || page > doc.GetNumberOfPages())
                throw new ArgumentOutOfRangeException(nameof(page), $"Page {page} does not exist.");
            var form = PdfFormCreator.GetAcroForm(doc, true);
            var field = new CheckBoxFormFieldBuilder(doc, UniqueName(form, name, "check"))
                .SetWidgetRectangle(new Rectangle(rect.X, rect.Y, rect.Width, rect.Height))
                .SetPage(page).SetCheckType(CheckBoxType.CHECK).CreateCheckBox();
            field.SetValue(isChecked ? "Yes" : "Off");
            StyleWidget(field);
            form.AddField(field);
        }
        return EditResult.Of(output.ToArray());
    }

    /// <summary>
    /// Inserts a clickable push button. When <paramref name="script"/> is set the button runs that
    /// JavaScript on activation (mouse-up) in Acrobat/Chrome — e.g. a "Submit" or "Calculate"
    /// button on a fillable form. The caption is the visible label.
    /// </summary>
    public static EditResult AddButton(byte[] pdf, int page, RectRegion rect, string? name = null,
        string? caption = null, string? script = null, string? password = null)
    {
        using var output = new MemoryStream();
        using (var doc = PdfIo.Open(pdf, output, password))
        {
            if (page < 1 || page > doc.GetNumberOfPages())
                throw new ArgumentOutOfRangeException(nameof(page), $"Page {page} does not exist.");
            var form = PdfFormCreator.GetAcroForm(doc, true);
            var field = new PushButtonFormFieldBuilder(doc, UniqueName(form, name, "button"))
                .SetWidgetRectangle(new Rectangle(rect.X, rect.Y, rect.Width, rect.Height))
                .SetCaption(string.IsNullOrWhiteSpace(caption) ? "Button" : caption)
                .SetPage(page).CreatePushButton();
            field.GetFirstFormAnnotation().SetBorderColor(ColorConstants.GRAY);
            if (!string.IsNullOrEmpty(script))
                field.GetWidgets()[0].SetAction(PdfAction.CreateJavaScript(script));
            form.AddField(field);
        }
        return EditResult.Of(output.ToArray());
    }

    // A light fill so an empty field is a visible box on the page (many readers, including the
    // PDFium-based preview, draw nothing for a borderless, value-less widget).
    private static readonly DeviceRgb FieldFill = new(240, 244, 250);

    /// <summary>Gives a widget a visible border + light background and generates its appearance so
    /// it renders as an obvious box (not blank page space) in every viewer.</summary>
    private static void StyleWidget(PdfFormField field)
    {
        var widget = field.GetFirstFormAnnotation();
        widget.SetBorderWidth(1);
        widget.SetBorderColor(ColorConstants.GRAY);
        widget.SetBackgroundColor(FieldFill);
        field.RegenerateField(); // emit an /AP appearance stream so PDFium actually draws it
    }

    /// <summary>A field name that doesn't collide with an existing one.</summary>
    private static string UniqueName(PdfAcroForm form, string? requested, string prefix)
    {
        var existing = form.GetAllFormFields().Keys;
        string baseName = string.IsNullOrWhiteSpace(requested) ? prefix : requested.Trim();
        if (!existing.Contains(baseName)) return baseName;
        for (int i = 2; ; i++)
        {
            string candidate = $"{baseName}_{i}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

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
