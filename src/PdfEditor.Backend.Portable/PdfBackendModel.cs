namespace PdfEditor.Backend.Portable;

/// <summary>
/// The operations the native host actually asks a PDF backend to perform. This is deliberately a
/// smaller, app-shaped vocabulary than any one library's — it is the contract the rest of
/// PdfEditor codes against, so a backend swap does not ripple into every tool.
/// </summary>
public enum PdfOperation
{
    ModifyPageContent,
    ModifyPageTree,
    Redact,
    FillFormFields,
    FlattenFormFields,
    MergeDocuments,
    ExtractPages,
    Sanitize,
    UpdateMetadata,
    ModifyAnnotations,
    ModifyJavaScript,
    ChangeEncryption,
}

/// <summary>
/// Why a backend refused. OfficeIMO reports 18 rewrite-blocker kinds and 5 read-blocker kinds;
/// iText mostly just throws. Both are normalised onto this list so the native-host protocol — and
/// the extension UI that has to explain the refusal to a user — has one stable vocabulary.
/// </summary>
public enum PdfBlocker
{
    Unknown = 0,
    PasswordRequired,
    InvalidPassword,
    Encryption,
    Signatures,
    Forms,
    TaggedContent,
    ActiveContent,
    OptionalContent,
    EmbeddedFiles,
    XmpMetadata,
    Outlines,
    PageLabels,
    NameTrees,
    NamedDestinations,
    OpenActions,
    ViewerPreferences,
    CatalogUri,
    CatalogViewSettings,
    OutputIntents,
    InvalidObjectReferences,
    MissingHeader,
    NoPages,
    ParserUnsupported,
    UnsupportedFilter,
    OperationNotImplemented,
}

/// <summary>How an operation can be made to run, if at all.</summary>
public enum PdfRoute
{
    /// <summary>The backend accepts the document as-is.</summary>
    Direct,

    /// <summary>
    /// The backend refuses only because the document is encrypted, and we hold a password that
    /// opens it. <see cref="PdfCryptoEnvelope"/> peels the encryption off, runs the operation on
    /// the plaintext, and re-applies the original scheme.
    /// </summary>
    CryptoEnvelope,

    /// <summary>No route. <see cref="PdfPlan.Blockers"/> says why.</summary>
    Blocked,
}

/// <summary>
/// The answer to "can this document have this done to it, and how". Produced by
/// <see cref="PdfPreflightGate"/> before the work starts, so the UI can disable an action up front
/// rather than failing after the user has committed to it.
/// </summary>
public sealed record PdfPlan(
    PdfOperation Operation,
    PdfRoute Route,
    IReadOnlyList<PdfBlocker> Blockers,
    string Summary)
{
    public bool CanExecute => Route != PdfRoute.Blocked;

    public static PdfPlan Blocked(PdfOperation op, string summary, params PdfBlocker[] blockers) =>
        new(op, PdfRoute.Blocked, blockers, summary);
}
