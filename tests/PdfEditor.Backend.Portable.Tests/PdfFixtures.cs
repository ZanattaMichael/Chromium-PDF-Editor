using System.Text;
using OfficeIMO.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PdfEditor.Backend.Portable.Tests;

/// <summary>
/// Builds PDFs byte by byte rather than through a library.
///
/// Every workaround in this project exists because of a document some *other* producer emitted, so
/// the fixtures have to reproduce those shapes exactly — a content stream with a bare, unwrapped
/// <c>cm</c>, or a form XObject that draws itself. No library will write either of those, because
/// no library considers them well-formed. Hand-assembling the file is the only way to get one.
/// </summary>
static class PdfFixtures
{
    public const string UserPassword = "user-pw";
    public const string OwnerPassword = "owner-pw";

    /// <summary>Chrome and Google Docs print-to-PDF: a CTM applied at depth zero, never restored.</summary>
    public static byte[] LeakedCtm() =>
        SinglePage(".24 0 0 -.24 0 792 cm\nBT /F1 60 Tf 100 -400 Td (UNBALANCED) Tj ET\n");

    /// <summary>A well-formed page, as the control.</summary>
    public static byte[] Balanced() =>
        SinglePage("q\n1 0 0 1 0 0 cm\nBT /F1 24 Tf 72 700 Td (BALANCED) Tj ET\nQ\n");

    /// <summary>Two pushes the page never closes.</summary>
    public static byte[] TwoOpenPushes() =>
        SinglePage("q\nq\n2 0 0 2 0 0 cm\nBT /F1 12 Tf 10 350 Td (TWO OPEN) Tj ET\n");

    /// <summary>One more pop than push, which would unwind past anything we appended.</summary>
    public static byte[] StackUnderflow() =>
        SinglePage("q\nQ\nQ\nBT /F1 24 Tf 72 700 Td (UNDERFLOW) Tj ET\n");

    /// <summary>An operator no parser knows, which is enough to make PdfSharp's throw.</summary>
    public static byte[] UnknownOperator() =>
        SinglePage("q\nQQQ\nQ\n");

    /// <summary>
    /// A form XObject listing itself in its own resources. Drawing it recurses forever, which on
    /// .NET means an uncatchable StackOverflowException — a denial of service in a single file.
    /// </summary>
    public static byte[] SelfReferencingForm()
    {
        var pdf = new Builder();
        int catalog = pdf.Reserve();
        int pages = pdf.Reserve();
        int font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        int page = pdf.Reserve();
        int contents = pdf.AddStream("/X1 Do\n");
        int form = pdf.Reserve();

        // The whole point of the fixture: /X1 inside the form resolves back to the form itself.
        pdf.Set(form, Builder.Stream(
            $"/Type /XObject /Subtype /Form /BBox [0 0 100 100] " +
            $"/Resources << /XObject << /X1 {form} 0 R >> >>",
            "/X1 Do\n"));

        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page,
            $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] " +
            $"/Resources << /Font << /F1 {font} 0 R >> /XObject << /X1 {form} 0 R >> >> " +
            $"/Contents {contents} 0 R >>");
        return pdf.Build(catalog);
    }

    /// <summary>
    /// A chain of <paramref name="depth"/> well-formed form XObjects, each drawing the next. The
    /// guard has to walk all the way down and unwind cleanly, which is the case a self-referencing
    /// fixture never reaches because it throws on the first step.
    ///
    /// The page's /XObject dictionary also carries a form written as a direct dictionary rather
    /// than a reference, and one entry that is not a dictionary at all, so that every arm of the
    /// reference resolver is exercised by a real document.
    /// </summary>
    public static byte[] NestedForms(int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);

        var pdf = new Builder();
        int catalog = pdf.Reserve();
        int pages = pdf.Reserve();
        int page = pdf.Reserve();
        int contents = pdf.AddStream("/X1 Do\n");

        int[] forms = new int[depth];
        for (int i = 0; i < depth; i++) forms[i] = pdf.Reserve();

        for (int i = 0; i < depth; i++)
        {
            string resources = i + 1 < depth
                ? $"/Resources << /XObject << /X1 {forms[i + 1]} 0 R >> >> "
                : string.Empty;
            pdf.Set(forms[i], Builder.Stream(
                $"/Type /XObject /Subtype /Form /BBox [0 0 100 100] {resources}",
                i + 1 < depth ? "/X1 Do\n" : "0 0 100 100 re f\n"));
        }

        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page,
            $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] " +
            $"/Resources << /XObject << /X1 {forms[0]} 0 R " +
            "/XDirect << /Type /XObject /Subtype /Form /BBox [0 0 10 10] >> " +
            "/XNotADictionary /Nonsense >> >> " +
            $"/Contents {contents} 0 R >>");
        return pdf.Build(catalog);
    }

    /// <summary>The balanced page, sealed the way a protected document arrives from a user.</summary>
    public static byte[] Encrypted(PdfStandardEncryptionAlgorithm algorithm = PdfStandardEncryptionAlgorithm.Aes256) =>
        OfficeIMO.Pdf.PdfDocument.Open(Balanced()).Security.Encrypt(EncryptionOptions(algorithm)).Pdf;

    public static PdfStandardEncryptionOptions EncryptionOptions(
        PdfStandardEncryptionAlgorithm algorithm = PdfStandardEncryptionAlgorithm.Aes256) =>
        new(UserPassword)
        {
            OwnerPassword = OwnerPassword,
            Algorithm = algorithm,
            AllowedPermissions = PdfStandardPermissions.Print,
        };

    /// <summary>Reads a fixture into PdfSharp's object model, which is where the guards operate.</summary>
    public static PdfSharp.Pdf.PdfDocument Load(byte[] pdf) =>
        PdfReader.Open(new MemoryStream(pdf, writable: false), PdfDocumentOpenMode.Modify);

    /// <summary>Saves and re-reads, so a test asserts on what was actually written to the file.</summary>
    public static PdfSharp.Pdf.PdfDocument RoundTrip(PdfSharp.Pdf.PdfDocument document)
    {
        var buffer = new MemoryStream();
        document.Save(buffer, closeStream: false);
        buffer.Position = 0;
        return PdfReader.Open(buffer, PdfDocumentOpenMode.Modify);
    }

    static byte[] SinglePage(string content, string extraResources = "")
    {
        var pdf = new Builder();
        int catalog = pdf.Reserve();
        int pages = pdf.Reserve();
        int font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        int page = pdf.Reserve();
        int contents = pdf.AddStream(content);

        pdf.Set(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.Set(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        pdf.Set(page,
            $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] " +
            $"/Resources << /Font << /F1 {font} 0 R >> {extraResources}>> /Contents {contents} 0 R >>");
        return pdf.Build(catalog);
    }

    /// <summary>
    /// A minimal cross-reference-table writer. Objects are numbered from 1 in the order they are
    /// added; <see cref="Reserve"/> takes a number now and fills it in later, which is what lets a
    /// fixture contain the forward and circular references these tests need.
    /// </summary>
    sealed class Builder
    {
        readonly List<byte[]?> objects = [];

        public int Reserve()
        {
            objects.Add(null);
            return objects.Count;
        }

        public int Add(string body)
        {
            objects.Add(Encoding.ASCII.GetBytes(body));
            return objects.Count;
        }

        public int Add(byte[] body)
        {
            objects.Add(body);
            return objects.Count;
        }

        public int AddStream(string content) => Add(Stream(string.Empty, content));

        public void Set(int number, string body) => objects[number - 1] = Encoding.ASCII.GetBytes(body);

        public void Set(int number, byte[] body) => objects[number - 1] = body;

        /// <summary>
        /// Emits a stream object, with the /Length the readers will trust.
        /// <paramref name="entries"/> are the dictionary's own entries, unbraced — the outer
        /// &lt;&lt; &gt;&gt; is added here, so nested dictionaries in the entries stay intact.
        /// </summary>
        public static byte[] Stream(string entries, string content)
        {
            byte[] payload = Encoding.ASCII.GetBytes(content);
            string open = entries.Trim();
            if (open.Length > 0) open += " ";

            var buffer = new MemoryStream();
            Ascii(buffer, $"<< {open}/Length {payload.Length} >>\nstream\n");
            buffer.Write(payload);
            Ascii(buffer, "\nendstream");
            return buffer.ToArray();
        }

        public byte[] Build(int rootNumber)
        {
            var buffer = new MemoryStream();
            Ascii(buffer, "%PDF-1.7\n");
            buffer.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]); // the binary marker readers sniff for

            var offsets = new long[objects.Count + 1];
            for (int i = 0; i < objects.Count; i++)
            {
                byte[] body = objects[i] ?? throw new InvalidOperationException($"Object {i + 1} was reserved but never set.");
                offsets[i + 1] = buffer.Position;
                Ascii(buffer, $"{i + 1} 0 obj\n");
                buffer.Write(body);
                Ascii(buffer, "\nendobj\n");
            }

            long startXref = buffer.Position;
            int size = objects.Count + 1;
            Ascii(buffer, $"xref\n0 {size}\n0000000000 65535 f \n");
            for (int i = 1; i < size; i++) Ascii(buffer, $"{offsets[i]:D10} 00000 n \n");
            Ascii(buffer, $"trailer\n<< /Size {size} /Root {rootNumber} 0 R >>\nstartxref\n{startXref}\n%%EOF\n");
            return buffer.ToArray();
        }

        static void Ascii(Stream to, string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            to.Write(bytes);
        }
    }
}
