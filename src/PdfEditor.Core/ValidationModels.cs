namespace PdfEditor.Core;

/// <summary>How badly a validation finding affects the exported document.</summary>
public enum ValidationSeverity
{
    /// <summary>Worth knowing, but nothing is wrong.</summary>
    Info,

    /// <summary>The file opens, but some viewers will render or behave differently than intended.</summary>
    Warning,

    /// <summary>The file is structurally broken; viewers may fail to open it or lose content.</summary>
    Error
}

/// <summary>
/// One problem found in an exported document. <paramref name="Code"/> is a stable identifier
/// (e.g. <c>PDF031</c>) so regression suites can assert on a defect class without matching prose,
/// <paramref name="Location"/> says exactly where to look (<c>object 4 0 R</c>, <c>page 2</c>,
/// <c>trailer</c>, <c>byte offset 1234</c>), and <paramref name="Message"/> states what is wrong
/// and what it means — enough to act on without opening a hex editor.
/// </summary>
public sealed record ValidationFinding(
    string Code, ValidationSeverity Severity, string Location, string Message)
{
    public override string ToString() => $"{Severity.ToString().ToUpperInvariant()} {Code} [{Location}] {Message}";
}

/// <summary>
/// The outcome of validating an exported document. <see cref="IsValid"/> is false only when at
/// least one <see cref="ValidationSeverity.Error"/> was found; warnings describe fidelity risks
/// that still open in every viewer.
/// </summary>
public sealed record ValidationReport(IReadOnlyList<ValidationFinding> Findings)
{
    public bool IsValid => ErrorCount == 0;

    public int ErrorCount => Findings.Count(f => f.Severity == ValidationSeverity.Error);

    public int WarningCount => Findings.Count(f => f.Severity == ValidationSeverity.Warning);

    /// <summary>True when a finding with the given code was reported.</summary>
    public bool Has(string code) => Findings.Any(f => f.Code == code);

    /// <summary>The findings rendered one per line, ready to write to a log.</summary>
    public string ToLogText() => Findings.Count == 0
        ? "PDF export validation: no problems found."
        : string.Join(Environment.NewLine, Findings.Select(f => f.ToString()));
}
