using OfficeIMO.Pdf;
using ImoDocument = OfficeIMO.Pdf.PdfDocument;

namespace PdfEditor.Backend.Portable;

/// <summary>
/// Runs a fail-closed operation on an encrypted document by taking the encryption off first and
/// putting it back afterwards.
///
/// OfficeIMO refuses <c>Redact</c> outright on an encrypted document and offers no append-only
/// fallback for it, so without this every PdfEditor action that accepts a password would lose the
/// ability to redact or stamp. Since the caller has already supplied a password that opens the
/// file, decrypting in memory grants no access they did not already have — it only moves the work
/// onto a plaintext copy that the backend is willing to rewrite.
///
/// The copy never touches disk, and the original scheme (algorithm, permission bits, metadata
/// setting) is re-applied to the result, so the document the user gets back is protected the same
/// way as the one they handed in.
/// </summary>
public static class PdfCryptoEnvelope
{
    /// <summary>
    /// Applies <paramref name="operation"/> to <paramref name="encrypted"/>, preserving encryption.
    /// <paramref name="ownerPassword"/> must be the owner password: removing encryption is itself a
    /// restricted operation, and the user password does not authorise it.
    ///
    /// A user password cannot be read back out of a PDF — only verified against it — so pass
    /// <paramref name="userPassword"/> whenever the caller knows it. Left null, the re-sealed
    /// document carries the owner password in both roles, which is a visible behaviour change for
    /// anyone who used to open the file with a separate user password.
    /// </summary>
    public static byte[] Run(
        byte[] encrypted,
        string ownerPassword,
        Func<ImoDocument, ImoDocument> operation,
        string? userPassword = null)
    {
        ArgumentNullException.ThrowIfNull(encrypted);
        ArgumentException.ThrowIfNullOrEmpty(ownerPassword);
        ArgumentNullException.ThrowIfNull(operation);

        PdfReadOptions read = PdfPreflightGate.ReadOptions(ownerPassword)!;
        ImoDocument opened = ImoDocument.Open(encrypted, read);

        // Decrypt reports the scheme it dismantled, so the result can be sealed the same way.
        PdfSecurityMutationResult stripped = opened.Security.Decrypt(ownerPassword);
        PdfStandardEncryptionOptions scheme = Describe(stripped.SourceSecurity, ownerPassword, userPassword);

        ImoDocument result = operation(ImoDocument.Open(stripped.Pdf));
        return result.Security.Encrypt(scheme).Pdf;
    }

    /// <summary>
    /// True when <paramref name="plan"/> says the envelope is the way through. Callers should route
    /// on this rather than inspecting blockers themselves.
    /// </summary>
    public static bool Handles(PdfPlan plan) => plan.Route == PdfRoute.CryptoEnvelope;

    /// <summary>
    /// Rebuilds the encryption options that produced <paramref name="info"/>, so the envelope
    /// re-seals with the strength the document already had rather than a library default.
    /// </summary>
    internal static PdfStandardEncryptionOptions Describe(
        PdfDocumentSecurityInfo info,
        string ownerPassword,
        string? userPassword)
    {
        var options = new PdfStandardEncryptionOptions(userPassword ?? ownerPassword)
        {
            OwnerPassword = ownerPassword,
            UserPassword = userPassword ?? ownerPassword,
            Algorithm = AlgorithmOf(info),
        };

        // Permissions are the point of the owner password; dropping them would quietly widen access.
        if (info.AllowedStandardPermissions is { } permissions) options.AllowedPermissions = permissions;
        if (info.EncryptMetadata is { } encryptMetadata) options.EncryptMetadata = encryptMetadata;
        return options;
    }

    /// <summary>
    /// Maps the raw /Encrypt dictionary numbers onto the three schemes OfficeIMO can write.
    /// The document does not name its algorithm; V and R are what identify it.
    /// </summary>
    static PdfStandardEncryptionAlgorithm AlgorithmOf(PdfDocumentSecurityInfo info)
    {
        // V5 is AES-256 by definition (R5 is the deprecated Adobe extension, R6 is PDF 2.0).
        if (info.EncryptionVersion >= 5 || info.EncryptionRevision >= 5)
            return PdfStandardEncryptionAlgorithm.Aes256;

        // V4 with a 128-bit key is AESV2 in every file PdfEditor has seen; V4/RC4 is vanishingly
        // rare and re-sealing it as AES-128 strengthens rather than weakens the result.
        if (info.EncryptionVersion >= 4 && info.EncryptionLengthBits is null or >= 128)
            return PdfStandardEncryptionAlgorithm.Aes128;

        return PdfStandardEncryptionAlgorithm.LegacyRc4;
    }
}
