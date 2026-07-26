using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Perf.Tests;

/// <summary>
/// Machine-independent guards on algorithmic complexity. Instead of absolute times these compare
/// how an operation's cost grows with input size, so they catch a regression from linear to
/// quadratic regardless of how fast the runner is.
/// </summary>
[Trait("Category", "Performance")]
public class ScalingPerfTests
{
    [Fact]
    public void Compare_ScalesLinearlyWithPageCount()
    {
        // Comparison diffs each page independently, so quadrupling the page count should roughly
        // quadruple the work. A cross-page O(n²) regression would make ×4 pages cost ~×16. We allow
        // a generous ×8 ceiling to absorb per-run noise while still catching a quadratic blow-up.
        byte[] small = PerfHarness.Doc(10);
        byte[] large = PerfHarness.Doc(40);

        double tSmall = PerfHarness.BestMs(3, () => DocComparer.Compare(small, small));
        double tLarge = PerfHarness.BestMs(3, () => DocComparer.Compare(large, large));

        // Guard against dividing by a near-zero baseline on a very fast machine.
        double baseline = Math.Max(tSmall, 5.0);
        double ratio = tLarge / baseline;
        Assert.True(ratio <= 8.0 * PerfHarness.Slack,
            $"compare scaled ×{ratio:F1} from 10→40 pages (tSmall={tSmall:F1} ms, tLarge={tLarge:F1} ms); " +
            "expected ≈×4 (linear), so this looks super-linear across pages.");
    }

    [Fact]
    public void Compare_HugeWordyPage_StaysBounded_ViaTheWordCap()
    {
        // A single page with far more words than the LCS word cap must fall back to the cheap
        // multiset diff instead of allocating an O(n²) DP table (8000² ≈ 64M cells). If the cap were
        // removed this would allocate hundreds of MB and crawl; with it, it stays fast.
        byte[] a = PerfHarness.Doc(1, wordsPerPage: 8000);
        byte[] b = PerfHarness.Doc(1, wordsPerPage: 8000);

        double ms = PerfHarness.BestMs(2, () => DocComparer.Compare(a, b));
        PerfHarness.AssertUnder(ms, 3000, "compare a page of 8000 words (word-cap fallback)");
    }

    [Fact]
    public void ArrangePages_ScalesRoughlyLinearly()
    {
        // Rebuilding page order copies each page once — linear in page count. ×5 pages should cost
        // on the order of ×5, not ×25.
        byte[] small = PerfHarness.Doc(20);
        byte[] large = PerfHarness.Doc(100);
        var smallOrder = Enumerable.Range(1, 20).Reverse().ToArray();
        var largeOrder = Enumerable.Range(1, 100).Reverse().ToArray();

        double tSmall = PerfHarness.BestMs(3, () => PageTools.Arrange(small, smallOrder));
        double tLarge = PerfHarness.BestMs(3, () => PageTools.Arrange(large, largeOrder));

        double baseline = Math.Max(tSmall, 5.0);
        double ratio = tLarge / baseline;
        Assert.True(ratio <= 12.0 * PerfHarness.Slack,
            $"arrange scaled ×{ratio:F1} from 20→100 pages; expected ≈×5 (linear).");
    }
}
