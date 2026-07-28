using iText.Kernel.Pdf;

namespace PdfEditor.Core;

/// <summary>Shared open/save helpers.</summary>
internal static class PdfIo
{
    public static PdfDocument Open(byte[] pdf, MemoryStream output, string? password = null)
    {
        var readerProperties = new ReaderProperties();
        if (!string.IsNullOrEmpty(password))
            readerProperties.SetPassword(System.Text.Encoding.UTF8.GetBytes(password));
        var reader = new PdfReader(new MemoryStream(pdf), readerProperties);
        reader.SetUnethicalReading(true);
        PdfDocument? doc = null;
        Guarded("opening the document", () => doc = new GuardedPdfDocument(reader, new PdfWriter(output)));
        return doc!;
    }

    public static PdfDocument OpenReadOnly(byte[] pdf, string? password = null)
    {
        var readerProperties = new ReaderProperties();
        if (!string.IsNullOrEmpty(password))
            readerProperties.SetPassword(System.Text.Encoding.UTF8.GetBytes(password));
        var reader = new PdfReader(new MemoryStream(pdf), readerProperties);
        reader.SetUnethicalReading(true);
        PdfDocument? doc = null;
        Guarded("opening the document", () => doc = new GuardedPdfDocument(reader));
        return doc!;
    }

    /// <summary>
    /// Runs a step that feeds raw document bytes into the underlying PDF library and converts the
    /// library's <em>defect-class</em> exceptions into a typed, message-bearing failure.
    /// <para>
    /// Content-stream decoding is the one place where a hostile document reliably drives iText off
    /// the rails: a truncated Flate stream leaves a number where an operator should be
    /// (<see cref="InvalidCastException"/>), a corrupt LZW stream dereferences a null dictionary
    /// entry (<see cref="NullReferenceException"/>), a RunLength stream that promises more bytes
    /// than it carries walks off its buffer (<see cref="IndexOutOfRangeException"/>). Those escape
    /// as "Object reference not set to an instance of an object", which tells a caller nothing and
    /// is indistinguishable from a genuine bug in this engine. Translating them here means every
    /// malformed-input failure reaches the host as a recoverable, explicable one; the original is
    /// kept as the inner exception so a real defect is still diagnosable from the stack trace.
    /// </para>
    /// </summary>
    public static void Guarded(string what, Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex) when (ex is NullReferenceException or IndexOutOfRangeException
                                       or InvalidCastException or KeyNotFoundException)
        {
            throw new InvalidDataException(
                $"This PDF could not be read: {what} failed because the document is malformed or " +
                $"corrupt ({ex.GetType().Name}).", ex);
        }
    }

    /// <summary>
    /// A <see cref="PdfDocument"/> whose close/save step is <see cref="Guarded"/> too. Closing is
    /// not a formality: it re-walks the page tree and serialises every object, so a document whose
    /// <c>/Count</c> lies about the number of kids throws a bare
    /// <see cref="NullReferenceException"/> out of <c>Dispose</c> — from inside a <c>using</c>, at
    /// the end of an operation that had otherwise succeeded. Overriding <see cref="Close"/> covers
    /// every caller at once, because <c>Dispose</c> is implemented in terms of it.
    /// </summary>
    private sealed class GuardedPdfDocument : PdfDocument
    {
        public GuardedPdfDocument(PdfReader reader) : base(reader) { }

        public GuardedPdfDocument(PdfReader reader, PdfWriter writer) : base(reader, writer) { }

        public override void Close() => Guarded("saving the document", base.Close);
    }
}
