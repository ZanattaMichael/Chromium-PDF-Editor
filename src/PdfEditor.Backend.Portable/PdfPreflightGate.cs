using OfficeIMO.Pdf;
using ImoDocument = OfficeIMO.Pdf.PdfDocument;

namespace PdfEditor.Backend.Portable;

/// <summary>
/// Asks the backend whether an operation is possible *before* running it, and translates the
/// answer into <see cref="PdfPlan"/>.
///
/// This exists because OfficeIMO is deliberately fail-closed: rather than degrading, it refuses a
/// rewrite whenever the document carries a structure it cannot faithfully reproduce. That is the
/// right default for a redaction tool — a silently partial redaction is worse than a refusal — but
/// it means the app has to be able to say *why*, and to know when a refusal is recoverable.
/// </summary>
public static class PdfPreflightGate
{
    public static PdfPlan Plan(byte[] pdf, PdfOperation operation, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        PdfReadOptions? read = ReadOptions(password);
        PdfMutationOperation native = ToNative(operation);

        try
        {
            ImoDocument doc = ImoDocument.Open(pdf, read);
            PdfMutationPlan plan = doc.PlanMutation(native, null, read);
            if (plan.CanExecute)
                return new PdfPlan(operation, PdfRoute.Direct, [], plan.Summary ?? "Supported.");

            var blockers = Translate(plan.Preflight);

            // The one refusal we can undo. If encryption is the *only* thing in the way and we can
            // open the document, the crypto envelope runs the operation on a decrypted copy and
            // puts the original scheme back afterwards.
            if (password is not null &&
                blockers.Count > 0 &&
                blockers.All(b => b is PdfBlocker.Encryption))
            {
                return new PdfPlan(operation, PdfRoute.CryptoEnvelope, blockers,
                    "Blocked by encryption only; recoverable via the crypto envelope.");
            }

            return new PdfPlan(operation, PdfRoute.Blocked, blockers,
                plan.Summary ?? string.Join(", ", plan.BlockerCodes));
        }
        catch (PdfPasswordRequiredException)
        {
            return PdfPlan.Blocked(operation, "The document is encrypted and no password was supplied.",
                PdfBlocker.PasswordRequired);
        }
        catch (PdfInvalidPasswordException)
        {
            // A mistyped password is the most ordinary thing a user can do here, and it has to come
            // back as an answer rather than as an exception crossing the native-host boundary.
            return PdfPlan.Blocked(operation, "The supplied password does not open this document.",
                PdfBlocker.InvalidPassword);
        }
        catch (Exception ex) when (PortableGuard.IsMalformedInput(ex))
        {
            return PdfPlan.Blocked(operation, "The document could not be parsed.", PdfBlocker.ParserUnsupported);
        }
    }

    internal static PdfReadOptions? ReadOptions(string? password) =>
        password is null
            ? null
            // IgnoreRestrictions is OfficeIMO's analogue of iText's SetUnethicalReading(true): the
            // permission bits in a PDF are advisory, and PdfEditor already honours the user's own
            // decision to open their own document.
            : new PdfReadOptions { Password = password, PermissionPolicy = PdfPermissionPolicy.IgnoreRestrictions };

    static List<PdfBlocker> Translate(PdfDocumentPreflight preflight)
    {
        var result = new List<PdfBlocker>();
        foreach (var blocker in preflight.ReadBlockers)
            Add(result, Translate(blocker.Kind));

        foreach (var blocker in preflight.RewriteBlockers)
            Add(result, Translate(blocker.Kind));

        return result;
    }

    // The two mappings are separated from the walk above so they can be exercised exhaustively.
    // A kind OfficeIMO adds in a later version falls through to Unknown, which is safe but silent —
    // the tests enumerate the enums so that silence shows up as a failure here rather than as a
    // blocker the UI cannot explain.
    internal static PdfBlocker Translate(PdfReadBlockerKind kind) => kind switch
    {
        PdfReadBlockerKind.MissingHeader => PdfBlocker.MissingHeader,
        PdfReadBlockerKind.Encryption => PdfBlocker.Encryption,
        PdfReadBlockerKind.NoPages => PdfBlocker.NoPages,
        PdfReadBlockerKind.ParserUnsupported => PdfBlocker.ParserUnsupported,
        PdfReadBlockerKind.UnsupportedContentStreamFilter => PdfBlocker.UnsupportedFilter,
        _ => PdfBlocker.Unknown,
    };

    internal static PdfBlocker Translate(PdfRewriteBlockerKind kind) => kind switch
    {
        PdfRewriteBlockerKind.Encryption => PdfBlocker.Encryption,
        PdfRewriteBlockerKind.Signatures => PdfBlocker.Signatures,
        PdfRewriteBlockerKind.Forms => PdfBlocker.Forms,
        PdfRewriteBlockerKind.Outlines => PdfBlocker.Outlines,
        PdfRewriteBlockerKind.CatalogViewSettings => PdfBlocker.CatalogViewSettings,
        PdfRewriteBlockerKind.PageLabels => PdfBlocker.PageLabels,
        PdfRewriteBlockerKind.CatalogNameTrees => PdfBlocker.NameTrees,
        PdfRewriteBlockerKind.NamedDestinations => PdfBlocker.NamedDestinations,
        PdfRewriteBlockerKind.OpenActions => PdfBlocker.OpenActions,
        PdfRewriteBlockerKind.ViewerPreferences => PdfBlocker.ViewerPreferences,
        PdfRewriteBlockerKind.TaggedContent => PdfBlocker.TaggedContent,
        PdfRewriteBlockerKind.XmpMetadata => PdfBlocker.XmpMetadata,
        PdfRewriteBlockerKind.CatalogUri => PdfBlocker.CatalogUri,
        PdfRewriteBlockerKind.OutputIntents => PdfBlocker.OutputIntents,
        PdfRewriteBlockerKind.EmbeddedFiles => PdfBlocker.EmbeddedFiles,
        PdfRewriteBlockerKind.OptionalContent => PdfBlocker.OptionalContent,
        PdfRewriteBlockerKind.ActiveContent => PdfBlocker.ActiveContent,
        PdfRewriteBlockerKind.InvalidObjectReferences => PdfBlocker.InvalidObjectReferences,
        _ => PdfBlocker.Unknown,
    };

    static void Add(List<PdfBlocker> into, PdfBlocker blocker)
    {
        if (!into.Contains(blocker)) into.Add(blocker);
    }

    internal static PdfMutationOperation ToNative(PdfOperation operation) => operation switch
    {
        PdfOperation.ModifyPageContent => PdfMutationOperation.ModifyPageContent,
        PdfOperation.ModifyPageTree => PdfMutationOperation.ModifyPageTree,
        PdfOperation.Redact => PdfMutationOperation.Redact,
        PdfOperation.FillFormFields => PdfMutationOperation.FillFormFields,
        PdfOperation.FlattenFormFields => PdfMutationOperation.FlattenFormFields,
        PdfOperation.MergeDocuments => PdfMutationOperation.MergeDocuments,
        PdfOperation.ExtractPages => PdfMutationOperation.ExtractPages,
        PdfOperation.Sanitize => PdfMutationOperation.Sanitize,
        PdfOperation.UpdateMetadata => PdfMutationOperation.UpdateMetadata,
        PdfOperation.ModifyAnnotations => PdfMutationOperation.ModifyAnnotations,
        PdfOperation.ModifyJavaScript => PdfMutationOperation.ModifyJavaScript,
        PdfOperation.ChangeEncryption => PdfMutationOperation.ChangeEncryption,
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unmapped operation."),
    };
}
