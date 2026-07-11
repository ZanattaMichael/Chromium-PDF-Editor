using iText.Kernel.Exceptions;
using iText.Kernel.Pdf;

namespace PdfEditor.Core;

/// <summary>Password protection using AES-256 encryption.</summary>
public static class Encryptor
{
    public static byte[] Encrypt(byte[] pdf, string userPassword, string? ownerPassword = null,
        string? currentPassword = null)
    {
        if (string.IsNullOrEmpty(userPassword))
            throw new ArgumentException("A user password is required.", nameof(userPassword));
        ownerPassword ??= userPassword;

        using var output = new MemoryStream();
        var writerProperties = new WriterProperties().SetStandardEncryption(
            System.Text.Encoding.UTF8.GetBytes(userPassword),
            System.Text.Encoding.UTF8.GetBytes(ownerPassword),
            EncryptionConstants.ALLOW_PRINTING,
            EncryptionConstants.ENCRYPTION_AES_256);

        var readerProperties = new ReaderProperties();
        if (!string.IsNullOrEmpty(currentPassword))
            readerProperties.SetPassword(System.Text.Encoding.UTF8.GetBytes(currentPassword));
        var reader = new PdfReader(new MemoryStream(pdf), readerProperties);
        reader.SetUnethicalReading(true);
        using (new PdfDocument(reader, new PdfWriter(output, writerProperties)))
        {
        }
        return output.ToArray();
    }

    /// <summary>Removes password protection (requires the current password).</summary>
    public static byte[] Decrypt(byte[] pdf, string password)
    {
        using var output = new MemoryStream();
        using (PdfIo.Open(pdf, output, password))
        {
        }
        return output.ToArray();
    }

    public static bool IsEncrypted(byte[] pdf)
    {
        try
        {
            using var doc = new PdfDocument(new PdfReader(new MemoryStream(pdf)));
            return false;
        }
        catch (BadPasswordException)
        {
            return true;
        }
    }

    /// <summary>True when the supplied password opens the document.</summary>
    public static bool CanOpen(byte[] pdf, string? password)
    {
        try
        {
            using var doc = PdfIo.OpenReadOnly(pdf, password);
            return true;
        }
        catch (BadPasswordException)
        {
            return false;
        }
    }
}
