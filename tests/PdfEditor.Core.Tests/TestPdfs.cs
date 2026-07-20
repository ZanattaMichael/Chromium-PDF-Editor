using System.Text;
using iText.Forms;
using iText.Forms.Fields;
using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Xobject;
using SkiaSharp;

namespace PdfEditor.Tests;

/// <summary>Builds small, deterministic PDFs for tests.</summary>
public static class TestPdfs
{
    public const float PageWidth = 595;   // A4 in points
    public const float PageHeight = 842;

    /// <summary>A single page with absolutely positioned text lines.</summary>
    public static byte[] WithText(params (string Text, float X, float Y, float Size)[] lines)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var canvas = new PdfCanvas(page);
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            foreach (var (text, x, y, size) in lines)
            {
                canvas.BeginText().SetFontAndSize(font, size)
                    .MoveText(x, y).ShowText(text).EndText();
            }
        }
        return output.ToArray();
    }

    /// <summary>A document with the given number of pages, each labelled.</summary>
    public static byte[] MultiPage(int pages, string labelPrefix = "Page")
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            for (int i = 1; i <= pages; i++)
            {
                var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
                new PdfCanvas(page).BeginText().SetFontAndSize(font, 14)
                    .MoveText(72, 770).ShowText($"{labelPrefix} {i}").EndText();
            }
        }
        return output.ToArray();
    }

    /// <summary>A page with a solid-colour raster image drawn into the given rectangle.</summary>
    public static byte[] WithImage(float x, float y, float width, float height)
    {
        using var bitmap = new SKBitmap(60, 40);
        using (var c = new SKCanvas(bitmap)) c.Clear(SKColors.Red);
        using var img = SKImage.FromBitmap(bitmap);
        byte[] png = img.Encode(SKEncodedImageFormat.Png, 100).ToArray();

        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var canvas = new PdfCanvas(page);
            canvas.AddImageFittedIntoRectangle(ImageDataFactory.Create(png),
                new Rectangle(x, y, width, height), false);
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            canvas.BeginText().SetFontAndSize(font, 12)
                .MoveText(72, 800).ShowText("Document with image").EndText();
        }
        return output.ToArray();
    }

    /// <summary>
    /// A page with a solid blue square drawn as a genuine inline (BI/ID/EI) image. iText's
    /// canvas API has no high-level method that reliably emits BI/ID/EI (its "asInline"
    /// flag on AddImageFittedIntoRectangle still emits a Do-based XObject in this version),
    /// so the content stream is written by hand, raw pixel bytes included.
    /// </summary>
    public static byte[] WithInlineImage(float x, float y, float width, float height)
    {
        const int size = 20;
        byte[] pixels = new byte[size * size * 3];
        for (int i = 0; i < pixels.Length; i += 3)
            pixels[i + 2] = 255; // solid blue (R=0, G=0, B=255)

        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            var fontName = page.GetResources().AddFont(doc, font).GetValue();

            using var content = new MemoryStream();
            void Write(string s) => content.Write(Encoding.ASCII.GetBytes(s));
            Write("q\n");
            Write($"{width} 0 0 {height} {x} {y} cm\n");
            Write($"BI\n/W {size}\n/H {size}\n/CS /RGB\n/BPC 8\nID\n");
            content.Write(pixels);
            Write("\nEI\nQ\n");
            Write($"BT /{fontName} 12 Tf 72 800 Td (Document with inline image) Tj ET");

            page.GetPdfObject().Put(PdfName.Contents,
                (PdfStream)new PdfStream(content.ToArray()).MakeIndirect(doc));
        }
        return output.ToArray();
    }

    /// <summary>
    /// A page that draws a form XObject at (x, y) whose own content shows <paramref name="formText"/>.
    /// The form's bounding box occupies exactly the given width/height in page space.
    /// </summary>
    public static byte[] WithForm(string formText, float x, float y, float width, float height)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            var form = new PdfFormXObject(new Rectangle(0, 0, width, height));
            new PdfCanvas(form, doc).BeginText().SetFontAndSize(font, 14)
                .MoveText(4, height / 2 - 5).ShowText(formText).EndText();
            new PdfCanvas(page).AddXObjectAt(form, x, y);
            new PdfCanvas(page).BeginText().SetFontAndSize(font, 12)
                .MoveText(72, 800).ShowText("Document with a form").EndText();
        }
        return output.ToArray();
    }

    /// <summary>
    /// A chain of <paramref name="depth"/> nested form XObjects, each drawing the next via Do,
    /// all sharing the same bounding box in page space — used to exercise the recursion
    /// depth guard in <c>ContentStreamEditor</c>.
    /// </summary>
    public static byte[] WithNestedForms(int depth, float x, float y, float width, float height)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);

            PdfFormXObject? previous = null;
            for (int level = depth; level >= 1; level--)
            {
                var form = new PdfFormXObject(new Rectangle(0, 0, width, height));
                var formCanvas = new PdfCanvas(form, doc);
                if (previous == null)
                    formCanvas.BeginText().SetFontAndSize(font, 10)
                        .MoveText(2, 2).ShowText("innermost").EndText();
                else
                    formCanvas.AddXObjectAt(previous, 0, 0);
                previous = form;
            }
            new PdfCanvas(page).AddXObjectAt(previous!, x, y);
            new PdfCanvas(page).BeginText().SetFontAndSize(font, 12)
                .MoveText(72, 800).ShowText("Document with nested forms").EndText();
        }
        return output.ToArray();
    }

    /// <summary>
    /// A page whose content stream is written by hand so it can use the low-level
    /// <c>'</c> and <c>"</c> text-showing operators, which iText's canvas API does not expose.
    /// </summary>
    public static byte[] WithQuoteOperators(string firstLine, string secondLine, string thirdLine)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            var fontName = page.GetResources().AddFont(doc, font);

            string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
            string content =
                $"BT\n/{fontName.GetValue()} 12 Tf\n14 TL\n72 700 Td\n" +
                $"({Escape(firstLine)}) Tj\n" +
                $"({Escape(secondLine)}) '\n" +
                $"0 0 ({Escape(thirdLine)}) \"\n" +
                "ET";
            page.GetPdfObject().Put(PdfName.Contents,
                (PdfStream)new PdfStream(Encoding.ASCII.GetBytes(content)).MakeIndirect(doc));
        }
        return output.ToArray();
    }

    /// <summary>
    /// A page whose content stream shows two strings via a single low-level <c>TJ</c>
    /// operator (an explicit array of string/number operands), with a real kerning
    /// number between them. iText's high-level canvas API rarely emits <c>TJ</c> for
    /// simple text, so this is written by hand.
    /// </summary>
    public static byte[] WithTjArray(string first, string second, float x, float y, float size)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            var fontName = page.GetResources().AddFont(doc, font).GetValue();

            string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
            string content =
                $"BT\n/{fontName} {size} Tf\n{x} {y} Td\n" +
                $"[({Escape(first)}) -250 ({Escape(second)})] TJ\nET";
            page.GetPdfObject().Put(PdfName.Contents,
                (PdfStream)new PdfStream(Encoding.ASCII.GetBytes(content)).MakeIndirect(doc));
        }
        return output.ToArray();
    }

    /// <summary>
    /// A page whose content stream shows a <c>TJ</c> array containing an empty string
    /// alongside two real words. Some renderers (including iText's own event source)
    /// never fire a text-render event for a zero-length string, which makes the number
    /// of render events disagree with the number of string operands in the array — the
    /// scenario <c>ContentStreamEditor</c>'s encoding-mismatch fallback guards against.
    /// </summary>
    public static byte[] WithTjArrayContainingEmptyString(string first, string second, float x, float y, float size)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            var fontName = page.GetResources().AddFont(doc, font).GetValue();

            string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
            string content =
                $"BT\n/{fontName} {size} Tf\n{x} {y} Td\n" +
                $"[({Escape(first)}) -200 () -200 ({Escape(second)})] TJ\nET";
            page.GetPdfObject().Put(PdfName.Contents,
                (PdfStream)new PdfStream(Encoding.ASCII.GetBytes(content)).MakeIndirect(doc));
        }
        return output.ToArray();
    }

    /// <summary>A page whose content stream calls <c>Do</c> for an XObject name that is not
    /// registered in the page's resources at all (a dangling/invalid reference).</summary>
    public static byte[] WithDanglingXObjectReference(string visibleText, float x, float y, float size)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            var fontName = page.GetResources().AddFont(doc, font).GetValue();

            string content =
                "q /Ghost Do Q\n" +
                $"BT /{fontName} {size} Tf {x} {y} Td ({visibleText}) Tj ET";
            page.GetPdfObject().Put(PdfName.Contents,
                (PdfStream)new PdfStream(Encoding.ASCII.GetBytes(content)).MakeIndirect(doc));
        }
        return output.ToArray();
    }

    /// <summary>
    /// A page with an XObject whose Subtype is neither Image nor Form (a made-up
    /// subtype), used to exercise the redactor's passthrough for unrecognised XObjects.
    /// </summary>
    public static byte[] WithUnknownXObjectSubtype(string visibleText, float x, float y, float size)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            var fontName = page.GetResources().AddFont(doc, font).GetValue();

            var weird = (PdfStream)new PdfStream(Array.Empty<byte>()).MakeIndirect(doc);
            weird.Put(PdfName.Type, PdfName.XObject);
            weird.Put(PdfName.Subtype, new PdfName("Mystery"));
            var xobjects = page.GetResources().GetResource(PdfName.XObject) ?? new PdfDictionary();
            xobjects.Put(new PdfName("Weird1"), weird);
            page.GetResources().GetPdfObject().Put(PdfName.XObject, xobjects);

            string content =
                "q /Weird1 Do Q\n" +
                $"BT /{fontName} {size} Tf {x} {y} Td ({visibleText}) Tj ET";
            page.GetPdfObject().Put(PdfName.Contents,
                (PdfStream)new PdfStream(Encoding.ASCII.GetBytes(content)).MakeIndirect(doc));
        }
        return output.ToArray();
    }

    /// <summary>
    /// An otherwise-normal image XObject whose declared bit depth is invalid for its
    /// color space (PDF only allows 1/2/4/8/16 bits per component; this sets 3) — the
    /// structure looks fine but decoding throws, exercising the pixel-scrubber's
    /// exception-handling path.
    /// </summary>
    public static byte[] WithCorruptImage(float x, float y, float width, float height)
    {
        byte[] pdf = WithImage(x, y, width, height);

        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)), new PdfWriter(output)))
        {
            var xobjects = doc.GetPage(1).GetResources().GetResource(PdfName.XObject);
            foreach (var key in xobjects.KeySet())
            {
                var stream = xobjects.GetAsStream(key);
                if (stream != null && PdfName.Image.Equals(stream.GetAsName(PdfName.Subtype)))
                {
                    stream.Put(PdfName.BitsPerComponent, new PdfNumber(3));
                    stream.SetModified();
                }
            }
        }
        return output.ToArray();
    }

    /// <summary>A single-page document with a text form field.</summary>
    public static byte[] WithTextField(string fieldName, string initialValue = "")
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var form = PdfFormCreator.GetAcroForm(doc, true);
            var field = new TextFormFieldBuilder(doc, fieldName)
                .SetWidgetRectangle(new Rectangle(100, 600, 200, 24)).CreateText();
            field.SetValue(initialValue);
            form.AddField(field);
        }
        return output.ToArray();
    }

    /// <summary>A single-page document carrying a document-level JavaScript open action.</summary>
    public static byte[] WithOpenActionJavaScript(string script = "app.alert('hello');")
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            doc.GetCatalog().SetOpenAction(PdfAction.CreateJavaScript(script));
        }
        return output.ToArray();
    }

    /// <summary>A single-page document with a link annotation pointing at the given URL.</summary>
    public static byte[] WithLinkTo(string url)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var link = new PdfLinkAnnotation(new Rectangle(72, 700, 200, 20));
            link.SetAction(PdfAction.CreateURI(url));
            page.AddAnnotation(link);
        }
        return output.ToArray();
    }

    /// <summary>A page with a link annotation covering the given rectangle.</summary>
    public static byte[] WithLinkAnnotation(float x, float y, float width, float height)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var page = doc.AddNewPage(new PageSize(PageWidth, PageHeight));
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            new PdfCanvas(page).BeginText().SetFontAndSize(font, 12)
                .MoveText(x, y + 4).ShowText("clickable link").EndText();
            var link = new PdfLinkAnnotation(new Rectangle(x, y, width, height));
            link.SetAction(PdfAction.CreateURI("https://example.com"));
            page.AddAnnotation(link);
        }
        return output.ToArray();
    }

    /// <summary>
    /// A raw, uncompressed page that mimics how Chrome / Skia print-to-PDF (what Google Docs
    /// "Download as PDF" produces) structures its content: a top-level scale + Y-flip matrix
    /// applied <em>outside</em> any q/Q, so it is never restored and stays active at the end of the
    /// content stream. Text is drawn under that transform via a text matrix, so it renders upright
    /// at a normal absolute position — but anything naively appended to the page inherits the
    /// leftover matrix. Used to regression-test that redaction/edit draw in the page's default
    /// user space regardless. The single Helvetica word renders around absolute (50, 300).
    /// </summary>
    public static byte[] ChromeStyleLeftoverCtm(string word = "SECRET")
    {
        // Top-level CTM: scale by 0.5, flip Y, translate up by 600  ->  maps (x,y) to (0.5x, 600-0.5y).
        // Inside it: clip to the page, then a text object whose text matrix flips glyphs upright.
        string content =
            "0.5 0 0 -0.5 0 600 cm\n" +   // unbalanced top-level transform (never restored)
            "q\n" +
            "0 0 800 1200 re W n\n" +      // clip to the page (in the scaled space)
            "BT\n/F1 48 Tf\n1 0 0 -1 100 600 Tm\n(" + word + ") Tj\nET\n" +
            "Q\n";
        byte[] contentBytes = Encoding.ASCII.GetBytes(content);

        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 600] " +
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {contentBytes.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (int i = 0; i < objs.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= objs.Count; i++)
            sb.Append($"{offsets[i].ToString().PadLeft(10, '0')} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
