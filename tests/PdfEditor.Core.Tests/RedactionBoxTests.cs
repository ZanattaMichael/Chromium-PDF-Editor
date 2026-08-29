using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Tests;

public class RedactionBoxTests
{
    // TestPdfs pages are A4 (595 x 842); crop box starts at (0,0).
    private const float PageWidth = 595f;

    [Fact]
    public void Expand_None_ReturnsRegionsUnchanged()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        var regions = new[] { new RectRegion(1, 72, 700, 40, 12) };

        var result = RedactionBox.Expand(pdf, regions, RedactionBox.LengthObfuscation.None);

        Assert.Equal(regions, result);
    }

    [Fact]
    public void Expand_FullLine_WidensToThePage_KeepingTheVerticalBand()
    {
        byte[] pdf = TestPdfs.WithText(("secret", 72, 700, 12));
        var region = new RectRegion(1, 72, 700, 40, 12);

        var r = Assert.Single(RedactionBox.Expand(pdf, new[] { region },
            RedactionBox.LengthObfuscation.FullLine));

        Assert.True(r.Width > PageWidth - 60, "full-line box should span almost the whole page width");
        Assert.Equal(700f, r.Y, 1f);        // same vertical band
        Assert.Equal(12f, r.Height, 1f);
        Assert.True(r.X < 40, "should start near the left margin");
    }

    [Fact]
    public void Expand_Quantize_RoundsWidthUpToTheGrid_KeepingTheLeftEdge()
    {
        byte[] pdf = TestPdfs.WithText(("secret", 72, 700, 12));
        var region = new RectRegion(1, 72, 700, 40, 12);

        var r = Assert.Single(RedactionBox.Expand(pdf, new[] { region },
            RedactionBox.LengthObfuscation.Quantize));

        Assert.Equal(72f, r.X, 1f);           // left edge preserved
        Assert.Equal(144f, r.Width, 1f);      // widened, and landing on a grid step
        Assert.Equal(0f, r.Width % 72f, 1f);
    }

    [Theory]
    // Widths that sit just under a grid step are the ones that used to come back barely touched:
    // 71pt rounded to 72, a box a hair wider than the word it covered, which is the Exact
    // behaviour wearing the Rounded label (#118).
    [InlineData(1f)]
    [InlineData(35f)]
    [InlineData(36f)]
    [InlineData(70f)]
    [InlineData(71f)]
    [InlineData(72f)]
    [InlineData(143f)]
    [InlineData(200f)]
    public void Expand_Quantize_AlwaysClearsTheTextByHalfAGridStep(float width)
    {
        byte[] pdf = TestPdfs.WithText(("secret", 72, 700, 12));
        var region = new RectRegion(1, 72, 700, width, 12);

        var r = Assert.Single(RedactionBox.Expand(pdf, new[] { region },
            RedactionBox.LengthObfuscation.Quantize));

        // A box that traces the glyphs still reports their length, whatever the mode is called.
        Assert.True(r.Width >= width + 36f,
            $"a {width}pt box widened only to {r.Width}pt, close enough to still trace the text");
        Assert.Equal(0f, r.Width % 72f, 1f);
        Assert.True(r.X <= region.X && r.X + r.Width >= region.X + region.Width,
            "the widened box must still cover the original");
    }

    [Fact]
    public void Expand_Quantize_NearTheRightEdge_StillCoversTheOriginal()
    {
        // A box near the right margin: rounding up must never shrink it below the original (which
        // used to leave the marked text exposed) — it slides left instead.
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        var region = new RectRegion(1, 550, 700, 30, 12); // right edge at 580; page is 595 wide

        var r = Assert.Single(RedactionBox.Expand(pdf, new[] { region },
            RedactionBox.LengthObfuscation.Quantize));

        Assert.True(r.X <= region.X, "the box must still cover the original's left edge");
        Assert.True(r.X + r.Width >= region.X + region.Width, "the box must still cover the original's right edge");
        Assert.True(r.X + r.Width <= PageWidth + 0.5f, "the box must stay on the page");
    }

    [Fact]
    public void Expand_Quantize_DifferentLengths_CollapseToTheSameBucket()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        var shortR = new RectRegion(1, 72, 700, 20, 12);
        var longR = new RectRegion(1, 72, 700, 34, 12);

        var a = Assert.Single(RedactionBox.Expand(pdf, new[] { shortR }, RedactionBox.LengthObfuscation.Quantize));
        var b = Assert.Single(RedactionBox.Expand(pdf, new[] { longR }, RedactionBox.LengthObfuscation.Quantize));

        // Two lengths inside one band come back as the same box — which is the whole point of
        // bucketing, and survives widening the box to clear the text.
        Assert.Equal(a.Width, b.Width, 1f);
    }

    [Fact]
    public void MergeAdjacent_JoinsTwoBoxesSeparatedByASpace_IntoOne()
    {
        // Two same-line boxes with a small (whitespace-sized) gap.
        var a = new RectRegion(1, 72, 700, 40, 12);
        var b = new RectRegion(1, 120, 700, 40, 12); // 8pt gap after a's right edge (112)

        var merged = Assert.Single(RedactionBox.MergeAdjacent(new[] { a, b }));

        Assert.Equal(72f, merged.X, 1f);
        Assert.Equal(160f, merged.X + merged.Width, 1f); // spans both, bridging the gap
    }

    [Fact]
    public void MergeAdjacent_LeavesBoxesFarApartSeparate()
    {
        var a = new RectRegion(1, 72, 700, 40, 12);
        var far = new RectRegion(1, 400, 700, 40, 12); // a wide gap — could hold un-redacted content

        var result = RedactionBox.MergeAdjacent(new[] { a, far });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void MergeAdjacent_LeavesBoxesOnDifferentLinesSeparate()
    {
        var top = new RectRegion(1, 72, 700, 40, 12);
        var below = new RectRegion(1, 80, 660, 40, 12); // different line

        Assert.Equal(2, RedactionBox.MergeAdjacent(new[] { top, below }).Count);
    }

    [Fact]
    public void Prepare_Intensity0_IsExactAndUnmerged()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        var regions = new[] { new RectRegion(1, 72, 700, 40, 12), new RectRegion(1, 120, 700, 40, 12) };

        var result = RedactionBox.Prepare(pdf, regions, 0);

        Assert.Equal(2, result.Count); // no merge at intensity 0
    }

    [Fact]
    public void Prepare_Intensity1_MergesButKeepsWidthExact()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        var regions = new[] { new RectRegion(1, 72, 700, 40, 12), new RectRegion(1, 120, 700, 40, 12) };

        var merged = Assert.Single(RedactionBox.Prepare(pdf, regions, 1));

        Assert.Equal(72f, merged.X, 1f);
        Assert.Equal(160f, merged.X + merged.Width, 1f);
    }

    [Fact]
    public void Prepare_Intensity2_MergesAndThenRoundsTheJoinedBox()
    {
        // Both halves of level 2 have to fire: the two words become one box, and that box is
        // widened past the text rather than left wrapping it.
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        var regions = new[] { new RectRegion(1, 72, 700, 40, 12), new RectRegion(1, 120, 700, 40, 12) };

        var r = Assert.Single(RedactionBox.Prepare(pdf, regions, 2));

        Assert.Equal(72f, r.X, 1f);
        Assert.True(r.Width >= 88f + 36f, "the merged 88pt box should be widened, not traced");
        Assert.Equal(0f, r.Width % 72f, 1f);
    }

    [Fact]
    public void Prepare_Intensity3_MergesAndExtendsToFullLine()
    {
        byte[] pdf = TestPdfs.WithText(("x", 72, 700, 12));
        var regions = new[] { new RectRegion(1, 72, 700, 40, 12) };

        var r = Assert.Single(RedactionBox.Prepare(pdf, regions, 3));

        Assert.True(r.Width > PageWidth - 60, "intensity 3 should span the page width");
    }
}
