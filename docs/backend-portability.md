# Backend portability

`PdfEditor.Core` does its PDF work with [iText](https://itextpdf.com/), which is AGPL-3.0 or
commercial. That is a licensing decision the project may one day want to revisit, so this note
records what a move to a permissively licensed backend would actually cost, and what
`PdfEditor.Backend.Portable` already does about it.

Nothing here is wired into the shipping path. `PdfEditor.Backend.Portable` is a separate project
with no reference from `PdfEditor.Core` or `PdfEditor.NativeHost`, and the extension behaves
exactly as it did before it existed. It is a proof, with tests, that the hard parts are solvable —
not a swap.

## Why one library is not enough

[OfficeIMO.Pdf](https://github.com/EvotecIT/OfficeIMO) (MIT) is the closest thing to a drop-in
replacement, and on the surface it covers most of what the native host's 49 actions need. But its
object model is `internal`: everything reachable from outside is a task-oriented facade
(`Redactions.Apply`, `Text.Inspect`, `Security.Encrypt`). PdfEditor does not only perform tasks —
it inspects and repairs content streams, walks resource dictionaries, and writes graphics state
by hand. None of that has a public counterpart.

[PdfSharp](https://github.com/empira/PDFsharp) (MIT) exposes precisely what OfficeIMO hides: the
object graph, the content-stream operator sequence, and page resources. So the portable backend
uses **both** — OfficeIMO for the high-level operations, PdfSharp for the low-level access those
operations do not reach. Both are MIT, so the licensing goal survives.

## The four structural blockers, and what was done

### 1. The object model is internal

Addressed, not eliminated. PdfSharp makes operator interception and object-graph walking possible,
which is what the other three workarounds are built on. Porting the twelve files in
`PdfEditor.Core` that touch iText's low-level types remains real work — this project does not
pretend otherwise.

### 2. OfficeIMO is fail-closed on encrypted documents

OfficeIMO refuses to rewrite a document it cannot reproduce faithfully. That is the right default
for a redaction tool: a silently partial redaction is worse than a refusal. But it means an
encrypted document cannot be redacted at all, even by a user who holds the password.

`PdfPreflightGate` asks the backend *before* running anything and returns a `PdfPlan` saying
whether the operation is possible and, if not, why — so the UI can disable an action up front
rather than failing after the user has committed to it. Crucially it distinguishes a refusal that
is final from one that is recoverable.

`PdfCryptoEnvelope` is the recovery. It decrypts in memory, runs the operation on the plaintext,
and re-applies the original scheme — algorithm, permission bits, metadata setting — to the result.
The caller already supplied a password that opens the file, so this grants no access they did not
have; it only moves the work onto a copy the backend is willing to rewrite.

One deliberate, lossy detail: **a user password cannot be read back out of a PDF, only verified
against it.** `PdfCryptoEnvelope.Run` therefore takes an optional `userPassword`, and callers that
know it should pass it. Left null, the re-sealed document carries the owner password in both
roles, which is a visible behaviour change for anyone who opened the file with a separate user
password.

### 3. Producers leak the graphics state

Chrome and Google Docs print-to-PDF emit pages whose content stream opens with a bare
`.24 0 0 -.24 0 792 cm` that is never wrapped in `q`/`Q`. Anything appended afterwards inherits
that flipped, quarter-scale space, so a watermark, highlight, ink stroke or redaction box lands in
the wrong place at the wrong size. `PdfEditor.Core` already fixes this with iText; OfficeIMO's
stamper does not.

`ContentStreamGuard` rebuilds the fix on PdfSharp. Note that the failing shape contains **no
unbalanced `q` at all** — counting `q`/`Q` depth misses it entirely, which is why a `cm` executed
at depth zero is tracked as its own condition.

Normalizing brackets the page's existing content with `Underflow + 1` pushes in front and
`Depth + 1` pops behind, so the page's own excess pops cannot reach past our base and the net
change to the stack is zero. A page that was already well-formed is left untouched.

### 4. No blend modes and no constant alpha

Highlighting needs `/BM /Multiply` so the highlight darkens the text beneath it rather than
covering it; watermarking needs opacity. OfficeIMO has no blend-mode surface at all, and
`PdfTextStampOptions` has no opacity. `GraphicsStateWorkarounds` writes both directly into the
page's `/ExtGState` resources under a name that cannot collide with the producer's own, and returns
that name for the caller to emit before its drawing operators.

## Two things that must not regress in a port

`FormXObjectGuard` is a port of `PdfEditor.Core`'s `PdfStructureGuard`, with the same bounds
(32 levels of nesting, 4096 nodes). A form XObject that lists itself in its own resources drives a
content processor into infinite recursion, and `StackOverflowException` cannot be caught on .NET —
the host process simply dies. That is a denial of service in a single file, so it has to be
prevented rather than handled.

`PortableGuard` mirrors `PdfIo.Guarded`. It is just as necessary here as with iText, and for a
sharper reason: PdfSharp's content-stream parser throws a bare `NullReferenceException` on any
operator it does not recognise, which a hostile or merely unusual document can trigger at will.

## Running the proof

```
dotnet test tests/PdfEditor.Backend.Portable.Tests
```

The fixtures are assembled byte by byte rather than through a library, because no library will
write a bare leaked `cm` or a self-referencing form XObject — neither is well-formed, and
reproducing them is the entire point.
