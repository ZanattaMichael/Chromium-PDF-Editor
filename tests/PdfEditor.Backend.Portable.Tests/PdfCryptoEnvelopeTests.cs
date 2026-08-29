using OfficeIMO.Pdf;
using Xunit;
using ImoDocument = OfficeIMO.Pdf.PdfDocument;

namespace PdfEditor.Backend.Portable.Tests;

public class PdfCryptoEnvelopeTests
{
    static readonly PdfPageRegion WholePage = new(1, 0, 0, 612, 792);

    [Fact]
    public void Redacts_an_encrypted_document_the_backend_refuses_to_touch()
    {
        byte[] encrypted = PdfFixtures.Encrypted();
        Assert.False(Plan(encrypted).CanExecute);

        byte[] result = PdfCryptoEnvelope.Run(
            encrypted,
            PdfFixtures.OwnerPassword,
            document => document.Redactions.Apply([new PdfRedactionArea(1, 70, 690, 130, 30, "test")]),
            PdfFixtures.UserPassword);

        Assert.Equal("BALANCED", TextOf(PdfFixtures.Balanced(), null));
        Assert.Equal(string.Empty, TextOf(result, PdfFixtures.OwnerPassword));
    }

    [Fact]
    public void The_result_is_still_encrypted()
    {
        byte[] result = PdfCryptoEnvelope.Run(
            PdfFixtures.Encrypted(), PdfFixtures.OwnerPassword, document => document, PdfFixtures.UserPassword);

        // A workaround that quietly handed back an unprotected document would be worse than the
        // refusal it replaces. Asserted through the gate rather than through Open, which does not
        // check the password until something actually reads the document.
        Assert.Equal([PdfBlocker.PasswordRequired], Route(result, password: null).Blockers);
        Assert.Contains(PdfBlocker.Encryption, Route(result, PdfFixtures.UserPassword).Blockers);
    }

    [Fact]
    public void Both_of_the_original_passwords_still_open_the_result()
    {
        byte[] result = PdfCryptoEnvelope.Run(
            PdfFixtures.Encrypted(), PdfFixtures.OwnerPassword, document => document, PdfFixtures.UserPassword);

        Assert.Equal(PdfRoute.CryptoEnvelope, Route(result, PdfFixtures.UserPassword).Route);
        Assert.Equal(PdfRoute.CryptoEnvelope, Route(result, PdfFixtures.OwnerPassword).Route);
    }

    [Fact]
    public void Handles_agrees_with_the_gate()
    {
        Assert.True(PdfCryptoEnvelope.Handles(
            PdfPreflightGate.Plan(PdfFixtures.Encrypted(), PdfOperation.Redact, PdfFixtures.OwnerPassword)));
        Assert.False(PdfCryptoEnvelope.Handles(
            PdfPreflightGate.Plan(PdfFixtures.Balanced(), PdfOperation.Redact)));
    }

    [Theory]
    [InlineData(PdfStandardEncryptionAlgorithm.Aes256)]
    [InlineData(PdfStandardEncryptionAlgorithm.Aes128)]
    public void The_original_scheme_is_reconstructed_rather_than_defaulted(PdfStandardEncryptionAlgorithm algorithm)
    {
        byte[] encrypted = PdfFixtures.Encrypted(algorithm);
        PdfDocumentSecurityInfo source = ImoDocument
            .Open(encrypted, PdfPreflightGate.ReadOptions(PdfFixtures.OwnerPassword))
            .Security.Decrypt(PdfFixtures.OwnerPassword).SourceSecurity;

        PdfStandardEncryptionOptions rebuilt = PdfCryptoEnvelope.Describe(
            source, PdfFixtures.OwnerPassword, PdfFixtures.UserPassword);

        Assert.Equal(algorithm, rebuilt.Algorithm);
        Assert.Equal(PdfStandardPermissions.Print, rebuilt.AllowedPermissions);
        Assert.Equal(PdfFixtures.OwnerPassword, rebuilt.OwnerPassword);
        Assert.Equal(PdfFixtures.UserPassword, rebuilt.UserPassword);
    }

    [Fact]
    public void Without_the_user_password_the_owner_password_takes_both_roles()
    {
        // Documented, deliberate, and lossy: a user password cannot be read back out of a PDF, only
        // verified against it. Callers that know it should pass it.
        byte[] result = PdfCryptoEnvelope.Run(PdfFixtures.Encrypted(), PdfFixtures.OwnerPassword, document => document);

        Assert.Equal(PdfRoute.CryptoEnvelope, Route(result, PdfFixtures.OwnerPassword).Route);
        Assert.Equal([PdfBlocker.InvalidPassword], Route(result, PdfFixtures.UserPassword).Blockers);
    }

    static PdfPlan Route(byte[] pdf, string? password) =>
        PdfPreflightGate.Plan(pdf, PdfOperation.Redact, password);

    static PdfMutationPlan Plan(byte[] pdf)
    {
        PdfReadOptions read = PdfPreflightGate.ReadOptions(PdfFixtures.OwnerPassword)!;
        return ImoDocument.Open(pdf, read).PlanMutation(PdfMutationOperation.Redact, null, read);
    }

    static string TextOf(byte[] pdf, string? password)
    {
        PdfReadOptions? read = PdfPreflightGate.ReadOptions(password);
        return ImoDocument.Open(pdf, read).Text.Inspect(WholePage, read).Text.Trim();
    }

    [Theory]
    // V5 is AES-256 by definition, whichever revision reports it.
    [InlineData(5, 5, 256, PdfStandardEncryptionAlgorithm.Aes256)]
    [InlineData(4, 6, 128, PdfStandardEncryptionAlgorithm.Aes256)]
    // V4 at 128 bits is AESV2 in practice; a V4 with no stated length is treated the same way.
    [InlineData(4, 4, 128, PdfStandardEncryptionAlgorithm.Aes128)]
    [InlineData(4, 4, null, PdfStandardEncryptionAlgorithm.Aes128)]
    // Everything older re-seals as RC4 rather than being silently strengthened, because a document
    // that has to stay readable by an old consumer is the only reason it is still RC4.
    [InlineData(2, 3, 128, PdfStandardEncryptionAlgorithm.LegacyRc4)]
    [InlineData(1, 2, 40, PdfStandardEncryptionAlgorithm.LegacyRc4)]
    [InlineData(null, null, null, PdfStandardEncryptionAlgorithm.LegacyRc4)]
    public void Infers_the_scheme_from_the_encrypt_dictionary_numbers(
        int? version, int? revision, int? lengthBits, PdfStandardEncryptionAlgorithm expected)
    {
        // The document never names its algorithm — /V and /R are what identify it, and getting this
        // wrong re-seals the file under a scheme the owner did not choose.
        Assert.Equal(expected, PdfCryptoEnvelope.AlgorithmOf(version, revision, lengthBits));
    }
}
