using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;

namespace PdfEditor.Core;

/// <summary>
/// Helpers for adding content to an existing page without inheriting whatever graphics state that
/// page's content leaves behind.
/// </summary>
internal static class PdfContentGuard
{
    /// <summary>
    /// Returns a <see cref="PdfCanvas"/> that draws in the page's <em>default</em> (identity) user
    /// space, isolated from any leftover CTM or clip path the existing page content left in effect.
    /// <para>
    /// A plain <c>new PdfCanvas(page)</c> appends operators to the end of the page content, where
    /// they run under whatever transform is still active. Real-world generators — notably Chrome /
    /// Skia print-to-PDF, which is what "Download as PDF" from Google Docs produces — apply a
    /// top-level scale + Y-flip matrix (e.g. <c>.24 0 0 -.24 0 792 cm</c>) that is never wrapped in
    /// a <c>q</c>/<c>Q</c> pair, so it is still active at the stream's end. Anything drawn there is
    /// scaled and flipped and lands in the wrong place.
    /// </para>
    /// <para>
    /// This brackets the existing content with a balanced <c>q</c> … <c>Q</c> so the returned canvas
    /// draws in the page's default user space. The operators are written as raw stream bytes because
    /// <see cref="PdfCanvas"/> validates <c>q</c>/<c>Q</c> balance eagerly per canvas — a lone
    /// <c>Q</c> would throw even though the page as a whole stays balanced.
    /// </para>
    /// </summary>
    internal static PdfCanvas InDefaultUserSpace(PdfPage page, PdfDocument doc)
    {
        // 'q' before all existing content saves the clean default (identity) graphics state.
        page.NewContentStreamBefore().SetData(Encoding.ASCII.GetBytes("q\n"));
        // 'Q' after it pops back to that clean state, discarding the content's leftover CTM/clip.
        page.NewContentStreamAfter().SetData(Encoding.ASCII.GetBytes("Q\n"));
        // Draw into a further-appended stream so this canvas's own q/Q stay self-balanced.
        return new PdfCanvas(page.NewContentStreamAfter(), page.GetResources(), doc);
    }
}
