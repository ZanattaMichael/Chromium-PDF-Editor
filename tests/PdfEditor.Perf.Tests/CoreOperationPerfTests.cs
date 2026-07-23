using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Perf.Tests;

/// <summary>
/// Absolute-time budgets for the core operations behind the interactive editor. The budgets are
/// generous backstops (typically 10-30× the observed local time) that fire only on a catastrophic
/// regression — a broken cache, an accidental full-document reprocess, an algorithmic blow-up.
/// </summary>
[Trait("Category", "Performance")]
public class CoreOperationPerfTests
{
    [Fact]
    public void RenderPage_IsFast()
    {
        byte[] pdf = PerfHarness.Doc(10);
        double ms = PerfHarness.BestMs(3, () => PageRenderer.RenderPagePng(pdf, 1, 150));
        PerfHarness.AssertUnder(ms, 3000, "render page @150dpi");
    }

    [Fact]
    public void AddHighlight_IsFast()
    {
        byte[] pdf = PerfHarness.Doc(10);
        var rects = new[] { new RectRegion(1, 56, 770, 300, 14) };
        double ms = PerfHarness.BestMs(5, () => HighlightTool.AddHighlight(pdf, 1, rects, "#ffeb3b"));
        PerfHarness.AssertUnder(ms, 1000, "add highlight");
    }

    [Fact]
    public void AddText_IsFast()
    {
        byte[] pdf = PerfHarness.Doc(10);
        var region = new RectRegion(1, 72, 400, 200, 20);
        double ms = PerfHarness.BestMs(5, () => TextTools.AddText(pdf, region, "stamped caption", 14f));
        PerfHarness.AssertUnder(ms, 1000, "add text");
    }

    [Fact]
    public void Redact_IsFast()
    {
        byte[] pdf = PerfHarness.Doc(10);
        var regions = new[] { new RectRegion(1, 56, 770, 300, 20) };
        double ms = PerfHarness.BestMs(5, () => Redactor.Redact(pdf, regions));
        PerfHarness.AssertUnder(ms, 1500, "redact");
    }

    [Fact]
    public void InspectHidden_IsFast()
    {
        byte[] pdf = PerfHarness.Doc(20);
        double ms = PerfHarness.BestMs(5, () => Sanitizer.Inspect(pdf));
        PerfHarness.AssertUnder(ms, 1500, "inspect hidden info (20pp)");
    }

    [Fact]
    public void Sanitize_IsFast()
    {
        byte[] pdf = PerfHarness.Doc(20);
        double ms = PerfHarness.BestMs(3, () => Sanitizer.Sanitize(pdf, new SanitizeOptions()));
        PerfHarness.AssertUnder(ms, 2500, "sanitize (20pp)");
    }

    [Fact]
    public void Compare_IsFast()
    {
        byte[] a = PerfHarness.Doc(20);
        byte[] b = PerfHarness.Doc(20);
        double ms = PerfHarness.BestMs(3, () => DocComparer.Compare(a, b));
        PerfHarness.AssertUnder(ms, 3000, "compare (20pp × 20pp)");
    }

    [Fact]
    public void ArrangePages_IsFast()
    {
        byte[] pdf = PerfHarness.Doc(50);
        var reversed = Enumerable.Range(1, 50).Reverse().ToArray();
        double ms = PerfHarness.BestMs(5, () => PageTools.Arrange(pdf, reversed));
        PerfHarness.AssertUnder(ms, 2000, "arrange 50 pages (reverse)");
    }

    [Fact]
    public void Merge_IsFast()
    {
        var docs = Enumerable.Range(0, 5).Select(_ => PerfHarness.Doc(10)).ToArray();
        double ms = PerfHarness.BestMs(5, () => Merger.Merge(docs));
        PerfHarness.AssertUnder(ms, 2000, "merge 5 × 10pp");
    }
}
