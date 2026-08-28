using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

namespace PdfEditor.Backend.Portable;

/// <summary>
/// Makes a page safe to draw on, whatever its producer left behind.
///
/// Chrome and Google Docs print-to-PDF emit pages whose content stream opens with a bare
/// <c>.24 0 0 -.24 0 792 cm</c> that is never wrapped in <c>q</c>…<c>Q</c>. Anything appended
/// afterwards inherits that flipped, quarter-scale space, so a watermark, highlight, ink stroke or
/// redaction box lands in the wrong place and at the wrong size. PdfEditor.Core already fixes this
/// with iText (see PdfContentGuard); OfficeIMO's stamper does not, so the fix has to be rebuilt on
/// an object model that is actually public — hence PdfSharp.
///
/// Note the failing shape needs no unbalanced <c>q</c> at all: counting q/Q depth alone misses it,
/// which is why <see cref="State.LeakedCtm"/> is tracked separately.
/// </summary>
public static class ContentStreamGuard
{
    /// <summary>How a page leaves the graphics-state stack.</summary>
    /// <param name="Depth">Pushes the page never closed.</param>
    /// <param name="Underflow">Pops issued against an already-empty stack.</param>
    /// <param name="LeakedCtm">A <c>cm</c> executed at nesting depth zero, permanently redefining user space.</param>
    public readonly record struct State(int Depth, int Underflow, bool LeakedCtm)
    {
        public bool NeedsGuard => Depth != 0 || Underflow != 0 || LeakedCtm;
    }

    public static State Analyze(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        int depth = 0, underflow = 0;
        bool leaked = false;
        Walk(PortableGuard.Run(() => ContentReader.ReadContent(page)), ref depth, ref underflow, ref leaked);
        return new State(depth, underflow, leaked);
    }

    static void Walk(CSequence sequence, ref int depth, ref int underflow, ref bool leaked)
    {
        foreach (CObject item in sequence)
        {
            if (item is CSequence nested)
            {
                Walk(nested, ref depth, ref underflow, ref leaked);
                continue;
            }

            if (item is not COperator op) continue;
            switch (op.OpCode.OpCodeName)
            {
                case OpCodeName.q:
                    depth++;
                    break;
                case OpCodeName.Q:
                    if (depth == 0) underflow++; else depth--;
                    break;
                case OpCodeName.cm:
                    if (depth == 0) leaked = true;
                    break;
            }
        }
    }

    /// <summary>
    /// Brackets the page's existing content so that anything appended after it draws in default
    /// user space. Returns false when the page was already well-formed and nothing was written.
    ///
    /// The prefix is <c>Underflow + 1</c> pushes so the page's own excess pops cannot reach past our
    /// base; the suffix is <c>Depth + 1</c> pops, closing what the page left open plus our own. Net
    /// change to the stack is zero, so a document that was already balanced stays balanced.
    /// </summary>
    public static bool Normalize(PdfPage page)
    {
        State state = Analyze(page);
        if (!state.NeedsGuard) return false;

        Write(page.Contents.PrependContent(), Repeat("q", state.Underflow + 1));
        Write(page.Contents.AppendContent(), Repeat("Q", state.Depth + 1));
        return true;
    }

    /// <summary>Normalizes every page. Returns how many needed it.</summary>
    public static int NormalizeAll(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        int count = 0;
        for (int i = 0; i < document.PageCount; i++)
            if (Normalize(document.Pages[i])) count++;
        return count;
    }

    // Content-stream operators are whitespace-delimited tokens, so "QQQ" is one unknown operator
    // rather than three pops — and an unknown operator is enough to crash PdfSharp's own parser.
    static string Repeat(string op, int times) => string.Concat(Enumerable.Repeat(op + "\n", times));

    static void Write(PdfContent content, string operators)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(operators);
        if (content.Stream is null) content.CreateStream(bytes);
        else content.Stream.Value = bytes;
    }
}
