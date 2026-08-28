using OfficeIMO.Pdf;
using Xunit;

namespace PdfEditor.Backend.Portable.Tests;

public class PdfPreflightGateTests
{
    [Fact]
    public void A_plain_document_routes_straight_through()
    {
        PdfPlan plan = PdfPreflightGate.Plan(PdfFixtures.Balanced(), PdfOperation.Redact);

        Assert.Equal(PdfRoute.Direct, plan.Route);
        Assert.True(plan.CanExecute);
        Assert.Empty(plan.Blockers);
    }

    [Fact]
    public void Encryption_alone_routes_to_the_envelope_when_a_password_is_held()
    {
        PdfPlan plan = PdfPreflightGate.Plan(PdfFixtures.Encrypted(), PdfOperation.Redact, PdfFixtures.OwnerPassword);

        // The backend still refuses the operation outright; the point of the gate is knowing that
        // this particular refusal is one we can undo, rather than reporting it to the user as final.
        Assert.Equal(PdfRoute.CryptoEnvelope, plan.Route);
        Assert.True(plan.CanExecute);
        Assert.Equal([PdfBlocker.Encryption], plan.Blockers);
    }

    [Fact]
    public void An_encrypted_document_with_no_password_is_blocked_on_the_password()
    {
        PdfPlan plan = PdfPreflightGate.Plan(PdfFixtures.Encrypted(), PdfOperation.Redact);

        Assert.Equal(PdfRoute.Blocked, plan.Route);
        Assert.False(plan.CanExecute);
        Assert.Equal([PdfBlocker.PasswordRequired], plan.Blockers);
    }

    [Fact]
    public void A_wrong_password_is_reported_rather_than_thrown()
    {
        PdfPlan plan = PdfPreflightGate.Plan(PdfFixtures.Encrypted(), PdfOperation.Redact, "not-the-password");

        Assert.Equal(PdfRoute.Blocked, plan.Route);
        Assert.Equal([PdfBlocker.InvalidPassword], plan.Blockers);
    }

    [Fact]
    public void Bytes_that_are_not_a_pdf_are_reported_rather_than_thrown()
    {
        PdfPlan plan = PdfPreflightGate.Plan("not a pdf at all"u8.ToArray(), PdfOperation.Redact);

        Assert.Equal(PdfRoute.Blocked, plan.Route);
        Assert.NotEmpty(plan.Blockers);
    }

    [Fact]
    public void Every_operation_in_the_vocabulary_maps_onto_the_backend()
    {
        // An unmapped operation throws, so this is what stops the enum and the backend drifting
        // apart silently when either side gains a member.
        byte[] pdf = PdfFixtures.Balanced();

        foreach (PdfOperation operation in Enum.GetValues<PdfOperation>())
        {
            PdfPlan plan = PdfPreflightGate.Plan(pdf, operation);
            Assert.Equal(operation, plan.Operation);
            Assert.NotEmpty(plan.Summary);
        }
    }

    [Fact]
    public void A_password_opts_into_ignoring_the_permission_bits()
    {
        // Permission bits are advisory and the user is opening their own document; this is the same
        // stance PdfEditor.Core takes with iText's SetUnethicalReading.
        PdfReadOptions options = PdfPreflightGate.ReadOptions(PdfFixtures.OwnerPassword)!;

        Assert.Equal(PdfFixtures.OwnerPassword, options.Password);
        Assert.Equal(PdfPermissionPolicy.IgnoreRestrictions, options.PermissionPolicy);
        Assert.Null(PdfPreflightGate.ReadOptions(null));
    }
}
