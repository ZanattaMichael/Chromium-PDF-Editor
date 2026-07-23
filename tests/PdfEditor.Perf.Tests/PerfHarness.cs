using System.Diagnostics;
using System.Text;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using Xunit;

namespace PdfEditor.Perf.Tests;

/// <summary>
/// Shared helpers for the performance guards: a deterministic PDF builder and a timing/assertion
/// harness. Budgets are deliberately generous (many times the observed local timings) so the tests
/// catch *gross* regressions — an accidental O(n²), a full-document reprocess where none is needed —
/// without flaking on slow or busy CI runners. A <c>PDF_EDITOR_PERF_SLACK</c> environment multiplier
/// lets an unusually slow environment relax every budget uniformly without touching the tests.
/// </summary>
internal static class PerfHarness
{
    /// <summary>Uniform multiplier applied to every budget (default 1.0). Set >1 on slow CI.</summary>
    public static readonly double Slack =
        double.TryParse(Environment.GetEnvironmentVariable("PDF_EDITOR_PERF_SLACK"), out var s) && s > 0
            ? s : 1.0;

    /// <summary>Builds a <paramref name="pages"/>-page PDF with ~<paramref name="wordsPerPage"/> words each.</summary>
    public static byte[] Doc(int pages, int wordsPerPage = 14)
    {
        using var output = new MemoryStream();
        using (var doc = new PdfDocument(new PdfWriter(output)))
        {
            var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
            for (int p = 1; p <= pages; p++)
            {
                var page = doc.AddNewPage(new PageSize(595, 842));
                var canvas = new PdfCanvas(page);
                canvas.BeginText().SetFontAndSize(font, 11).SetLeading(13).MoveText(56, 780);
                var line = new StringBuilder();
                int wordsOnLine = 0;
                for (int w = 0; w < wordsPerPage; w++)
                {
                    line.Append('w').Append(p).Append('_').Append(w).Append(' ');
                    if (++wordsOnLine == 12)
                    {
                        canvas.ShowText(line.ToString()).NewlineText();
                        line.Clear();
                        wordsOnLine = 0;
                    }
                }
                if (line.Length > 0) canvas.ShowText(line.ToString());
                canvas.EndText();
            }
        }
        return output.ToArray();
    }

    /// <summary>Runs <paramref name="op"/> once to warm up, then returns the fastest of N timed runs
    /// (the minimum is the most stable estimator — least perturbed by GC/scheduling noise).</summary>
    public static double BestMs(int reps, Action op)
    {
        op(); // warm-up: JIT, first-touch allocations, caches
        double best = double.MaxValue;
        for (int i = 0; i < reps; i++)
        {
            var sw = Stopwatch.StartNew();
            op();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    /// <summary>Asserts a measured time is within a budget (scaled by <see cref="Slack"/>).</summary>
    public static void AssertUnder(double actualMs, double budgetMs, string what)
    {
        double limit = budgetMs * Slack;
        Assert.True(actualMs <= limit,
            $"{what}: {actualMs:F1} ms exceeded the {limit:F0} ms budget " +
            $"(base {budgetMs:F0} ms × slack {Slack:F1}).");
    }
}
