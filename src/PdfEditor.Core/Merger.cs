using iText.Kernel.Pdf;
using iText.Kernel.Utils;

namespace PdfEditor.Core;

/// <summary>Concatenates multiple PDF documents into one.</summary>
public static class Merger
{
    public static byte[] Merge(IReadOnlyList<byte[]> pdfs, IReadOnlyList<string?>? passwords = null)
    {
        if (pdfs.Count == 0) throw new ArgumentException("At least one document is required.", nameof(pdfs));
        if (pdfs.Count == 1) return pdfs[0];

        using var output = new MemoryStream();
        using (var target = new PdfDocument(new PdfWriter(output)))
        {
            var merger = new PdfMerger(target);
            for (int i = 0; i < pdfs.Count; i++)
            {
                string? password = passwords != null && i < passwords.Count ? passwords[i] : null;
                using var source = PdfIo.OpenReadOnly(pdfs[i], password);
                merger.Merge(source, 1, source.GetNumberOfPages());
            }
        }
        return output.ToArray();
    }
}
