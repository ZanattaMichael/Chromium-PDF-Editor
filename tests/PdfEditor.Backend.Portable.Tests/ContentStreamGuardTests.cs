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
}
