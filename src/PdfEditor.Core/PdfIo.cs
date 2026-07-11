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
        return new PdfDocument(reader, new PdfWriter(output));
    }

    public static PdfDocument OpenReadOnly(byte[] pdf, string? password = null)
    {
        var readerProperties = new ReaderProperties();
        if (!string.IsNullOrEmpty(password))
            readerProperties.SetPassword(System.Text.Encoding.UTF8.GetBytes(password));
        var reader = new PdfReader(new MemoryStream(pdf), readerProperties);
        reader.SetUnethicalReading(true);
        return new PdfDocument(reader);
    }
}
