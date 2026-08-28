using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Xunit;

namespace PdfEditor.Backend.Portable.Tests;

public class GraphicsStateWorkaroundsTests
{
    [Fact]
    public void A_registered_blend_mode_survives_being_written_to_the_file()
    {
        PdfDocument document = PdfFixtures.Load(PdfFixtures.Balanced());
        string name = GraphicsStateWorkarounds.Register(document, document.Pages[0], GraphicsStateWorkarounds.Multiply, 0.4);

        PdfDictionary state = ExtGState(PdfFixtures.RoundTrip(document).Pages[0], name);

        // Multiply is what makes a highlight darken the text under it instead of hiding it, and it
        // is the one thing OfficeIMO's stamper offers no way to say.
        Assert.Equal("/Multiply", state.Elements.GetName("/BM"));
        Assert.Equal("/ExtGState", state.Elements.GetName("/Type"));
        Assert.Equal(0.4, state.Elements.GetReal("/ca"), 4);
        Assert.Equal(0.4, state.Elements.GetReal("/CA"), 4);
    }

    [Fact]
    public void Opacity_can_be_set_without_a_blend_mode()
    {
        PdfDocument document = PdfFixtures.Load(PdfFixtures.Balanced());
        string name = GraphicsStateWorkarounds.Register(document, document.Pages[0], blendMode: null, alpha: 0.25);

        PdfDictionary state = ExtGState(PdfFixtures.RoundTrip(document).Pages[0], name);

        Assert.False(state.Elements.ContainsKey("/BM"));
        Assert.Equal(0.25, state.Elements.GetReal("/ca"), 4);
    }

    [Fact]
    public void Repeated_registrations_get_distinct_names()
    {
        PdfDocument document = PdfFixtures.Load(PdfFixtures.Balanced());
        PdfPage page = document.Pages[0];

        string first = GraphicsStateWorkarounds.Register(document, page, GraphicsStateWorkarounds.Multiply);
        string second = GraphicsStateWorkarounds.Register(document, page, GraphicsStateWorkarounds.Normal);

        Assert.NotEqual(first, second);
        PdfPage reloaded = PdfFixtures.RoundTrip(document).Pages[0];
        Assert.Equal("/Multiply", ExtGState(reloaded, first).Elements.GetName("/BM"));
        Assert.Equal("/Normal", ExtGState(reloaded, second).Elements.GetName("/BM"));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Alpha_outside_the_legal_range_is_refused(double alpha)
    {
        PdfDocument document = PdfFixtures.Load(PdfFixtures.Balanced());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => GraphicsStateWorkarounds.Register(document, document.Pages[0], alpha: alpha));
    }

    [Fact]
    public void A_highlight_normalizes_the_page_before_drawing_on_it()
    {
        // Without this the box would be laid out in the quarter-scale flipped space Chrome leaked,
        // which is the bug the whole guard exists for.
        PdfDocument document = PdfFixtures.Load(PdfFixtures.LeakedCtm());
        PdfPage page = document.Pages[0];

        string operators = GraphicsStateWorkarounds.BuildHighlight(
            document, page, new PdfRectangle(new XPoint(72, 700), new XPoint(272, 720)), 1, 1, 0);

        Assert.False(ContentStreamGuard.Analyze(PdfFixtures.RoundTrip(document).Pages[0]).NeedsGuard);
        Assert.StartsWith("q\n", operators, StringComparison.Ordinal);
        Assert.EndsWith("Q\n", operators, StringComparison.Ordinal);
        Assert.Contains(" gs\n", operators, StringComparison.Ordinal);
        Assert.Contains("1 1 0 rg", operators, StringComparison.Ordinal);
        Assert.Contains("72 700 200 20 re f", operators, StringComparison.Ordinal);
    }

    static PdfDictionary ExtGState(PdfPage page, string name) =>
        page.Elements.GetDictionary("/Resources")!
            .Elements.GetDictionary("/ExtGState")!
            .Elements.GetDictionary(name)!;
}
