using PdfSharp.Pdf;
using Xunit;

namespace PdfEditor.Backend.Portable.Tests;

public class FormXObjectGuardTests
{
    [Fact]
    public void Rejects_a_form_xobject_that_draws_itself()
    {
        PdfPage page = PdfFixtures.Load(PdfFixtures.SelfReferencingForm()).Pages[0];

        // Left unchecked this is not a caught exception but a dead process: a content processor
        // recurses into the form forever, and StackOverflowException cannot be handled on .NET.
        var error = Assert.Throws<InvalidDataException>(() => FormXObjectGuard.EnsureFormXObjectsTerminate(page));
        Assert.Contains("draws itself", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_a_page_with_no_xobjects()
    {
        PdfDocument document = PdfFixtures.Load(PdfFixtures.Balanced());

        // Returning is the whole result here, so it is asserted explicitly rather than left as the
        // absence of a throw — an unasserted call reads as an unfinished test.
        Assert.Null(Record.Exception(() => FormXObjectGuard.EnsureFormXObjectsTerminate(document.Pages[0])));
        Assert.Null(Record.Exception(() => FormXObjectGuard.EnsureAllPagesTerminate(document)));
    }

    [Fact]
    public void Bounds_are_the_ones_PdfEditor_Core_already_ships()
    {
        // These are not free parameters: a portable backend that guarded less tightly than the
        // iText one would be a regression dressed up as a port.
        Assert.Equal(32, FormXObjectGuard.MaxFormNesting);
        Assert.Equal(4096, FormXObjectGuard.MaxFormsInspected);
    }

    [Fact]
    public void Walks_a_well_formed_chain_of_nested_forms_to_the_bottom_and_back()
    {
        // Every other fixture here throws on the way down. This one has to unwind cleanly: a guard
        // that left forms on its path set would reject the second legitimate use of a shared form.
        PdfPage page = PdfFixtures.Load(PdfFixtures.NestedForms(8)).Pages[0];

        Assert.Null(Record.Exception(() => FormXObjectGuard.EnsureFormXObjectsTerminate(page)));
    }

    [Fact]
    public void Rejects_a_form_chain_deeper_than_any_real_document_nests()
    {
        // A chain deep enough to exhaust the stack without ever repeating an object, so the cycle
        // check never fires and only the depth bound stands between this file and a dead process.
        PdfPage page = PdfFixtures.Load(PdfFixtures.NestedForms(40)).Pages[0];

        var error = Assert.Throws<InvalidDataException>(() => FormXObjectGuard.EnsureFormXObjectsTerminate(page));
        Assert.Contains("levels deep", error.Message, StringComparison.Ordinal);
    }
}
