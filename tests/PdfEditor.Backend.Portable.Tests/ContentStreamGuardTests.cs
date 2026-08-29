using PdfSharp.Pdf;
using Xunit;

namespace PdfEditor.Backend.Portable.Tests;

public class ContentStreamGuardTests
{
    [Fact]
    public void Detects_the_leaked_ctm_that_q_depth_counting_misses()
    {
        ContentStreamGuard.State state = ContentStreamGuard.Analyze(PdfFixtures.Load(PdfFixtures.LeakedCtm()).Pages[0]);

        // This is the whole reason LeakedCtm is tracked at all: the Chrome shape is perfectly
        // balanced by the q/Q count, and still leaves every later operator in the wrong space.
        Assert.Equal(0, state.Depth);
        Assert.Equal(0, state.Underflow);
        Assert.True(state.LeakedCtm);
        Assert.True(state.NeedsGuard);
    }

    [Fact]
    public void Leaves_a_well_formed_page_alone()
    {
        PdfPage page = PdfFixtures.Load(PdfFixtures.Balanced()).Pages[0];
        ContentStreamGuard.State state = ContentStreamGuard.Analyze(page);

        Assert.False(state.NeedsGuard);
        Assert.False(ContentStreamGuard.Normalize(page));
    }

    [Fact]
    public void Counts_pushes_the_page_never_closed()
    {
        ContentStreamGuard.State state = ContentStreamGuard.Analyze(PdfFixtures.Load(PdfFixtures.TwoOpenPushes()).Pages[0]);

        Assert.Equal(2, state.Depth);
        Assert.Equal(0, state.Underflow);
        Assert.True(state.NeedsGuard);
    }

    [Fact]
    public void Counts_pops_against_an_empty_stack()
    {
        ContentStreamGuard.State state = ContentStreamGuard.Analyze(PdfFixtures.Load(PdfFixtures.StackUnderflow()).Pages[0]);

        Assert.Equal(0, state.Depth);
        Assert.Equal(1, state.Underflow);
        Assert.True(state.NeedsGuard);
    }

    [Theory]
    [InlineData("leaked ctm")]
    [InlineData("two open pushes")]
    [InlineData("stack underflow")]
    public void Normalizing_leaves_the_page_in_default_user_space(string shape)
    {
        byte[] source = shape switch
        {
            "leaked ctm" => PdfFixtures.LeakedCtm(),
            "two open pushes" => PdfFixtures.TwoOpenPushes(),
            _ => PdfFixtures.StackUnderflow(),
        };

        PdfDocument document = PdfFixtures.Load(source);
        Assert.True(ContentStreamGuard.Normalize(document.Pages[0]));

        // Asserted after a save/reload, because what matters is the state of the file a later tool
        // opens, not of the object graph this test happens to be holding.
        ContentStreamGuard.State after = ContentStreamGuard.Analyze(PdfFixtures.RoundTrip(document).Pages[0]);
        Assert.False(after.NeedsGuard);
    }

    [Fact]
    public void Normalizing_is_idempotent()
    {
        PdfDocument document = PdfFixtures.Load(PdfFixtures.LeakedCtm());
        ContentStreamGuard.Normalize(document.Pages[0]);

        PdfDocument reloaded = PdfFixtures.RoundTrip(document);
        Assert.False(ContentStreamGuard.Normalize(reloaded.Pages[0]));
    }

    [Fact]
    public void NormalizeAll_reports_how_many_pages_needed_it()
    {
        PdfDocument document = PdfFixtures.Load(PdfFixtures.LeakedCtm());
        Assert.Equal(1, ContentStreamGuard.NormalizeAll(document));
        Assert.Equal(0, ContentStreamGuard.NormalizeAll(PdfFixtures.RoundTrip(document)));
    }

    [Fact]
    public void An_unparseable_content_stream_is_an_InvalidDataException_not_a_crash()
    {
        PdfPage page = PdfFixtures.Load(PdfFixtures.UnknownOperator()).Pages[0];

        Assert.Throws<InvalidDataException>(() => ContentStreamGuard.Analyze(page));
    }

    [Fact]
    public void Counts_through_a_nested_sequence_rather_than_stopping_at_the_top_level()
    {
        // PdfSharp nests sequences for structures the walk must descend into. A walk that only
        // looked at the outer level would report this page balanced, and the guard would do nothing.
        var inner = new PdfSharp.Pdf.Content.Objects.CSequence
        {
            PdfSharp.Pdf.Content.Objects.OpCodes.OperatorFromName("q"),
            PdfSharp.Pdf.Content.Objects.OpCodes.OperatorFromName("cm"),
        };
        var outer = new PdfSharp.Pdf.Content.Objects.CSequence
        {
            PdfSharp.Pdf.Content.Objects.OpCodes.OperatorFromName("Q"),
        };
        // CSequence has an Add(CSequence) overload that splices the contents in rather than
        // nesting them, and it is the one a collection initializer picks — which would flatten the
        // very structure under test. Insert takes the CObject overload and stores it as one element.
        outer.Insert(1, inner);
        Assert.IsType<PdfSharp.Pdf.Content.Objects.CSequence>(outer[1]);

        ContentStreamGuard.State state = ContentStreamGuard.Analyze(outer);

        Assert.Equal(1, state.Underflow);   // the Q at the top, against an empty stack
        Assert.Equal(1, state.Depth);       // the q inside, never closed
        Assert.False(state.LeakedCtm);      // the cm ran inside that q, so it is contained
        Assert.True(state.NeedsGuard);
    }
}
