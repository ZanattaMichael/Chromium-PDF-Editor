using OfficeIMO.Pdf;
using Xunit;

namespace PdfEditor.Backend.Portable.Tests;

/// <summary>
/// The gate's whole value is that a refusal arrives as something the UI can explain. A blocker
/// kind that falls through to <see cref="PdfBlocker.Unknown"/> defeats that silently — the
/// operation is still correctly refused, but the user is told nothing. These tests enumerate
/// OfficeIMO's own enums, so a kind added by a future version fails here instead.
/// </summary>
public class BlockerTranslationTests
{
    public static TheoryData<PdfReadBlockerKind> ReadKinds()
    {
        var data = new TheoryData<PdfReadBlockerKind>();
        foreach (PdfReadBlockerKind kind in Enum.GetValues<PdfReadBlockerKind>()) data.Add(kind);
        return data;
    }

    public static TheoryData<PdfRewriteBlockerKind> RewriteKinds()
    {
        var data = new TheoryData<PdfRewriteBlockerKind>();
        foreach (PdfRewriteBlockerKind kind in Enum.GetValues<PdfRewriteBlockerKind>()) data.Add(kind);
        return data;
    }

    [Theory]
    [MemberData(nameof(ReadKinds))]
    public void Every_read_blocker_kind_has_a_name_the_ui_can_show(PdfReadBlockerKind kind)
    {
        Assert.NotEqual(PdfBlocker.Unknown, PdfPreflightGate.Translate(kind));
    }

    [Theory]
    [MemberData(nameof(RewriteKinds))]
    public void Every_rewrite_blocker_kind_has_a_name_the_ui_can_show(PdfRewriteBlockerKind kind)
    {
        Assert.NotEqual(PdfBlocker.Unknown, PdfPreflightGate.Translate(kind));
    }

    [Fact]
    public void An_unrecognised_read_blocker_kind_degrades_to_unknown_rather_than_throwing()
    {
        // A refusal we cannot name is still a refusal. Throwing here would turn an explainable
        // block into a crash on the native-host boundary.
        Assert.Equal(PdfBlocker.Unknown, PdfPreflightGate.Translate((PdfReadBlockerKind)9999));
        Assert.Equal(PdfBlocker.Unknown, PdfPreflightGate.Translate((PdfRewriteBlockerKind)9999));
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void Every_operation_maps_onto_a_native_operation(PdfOperation operation)
    {
        // Enum.IsDefined is the assertion: an unmapped operation would come back as the zero value
        // of PdfMutationOperation, which is a different operation rather than an error.
        PdfMutationOperation native = PdfPreflightGate.ToNative(operation);
        Assert.True(Enum.IsDefined(native));
    }

    public static TheoryData<PdfOperation> Operations()
    {
        var data = new TheoryData<PdfOperation>();
        foreach (PdfOperation operation in Enum.GetValues<PdfOperation>()) data.Add(operation);
        return data;
    }

    [Fact]
    public void An_operation_outside_the_enum_is_refused_loudly()
    {
        // Unlike an unknown blocker kind, this one is our bug rather than the document's: a caller
        // has invented an operation, and silently planning a different one would be worse.
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPreflightGate.ToNative((PdfOperation)9999));
    }
}
