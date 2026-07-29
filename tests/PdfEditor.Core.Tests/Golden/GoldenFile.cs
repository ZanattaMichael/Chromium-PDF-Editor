using System.Runtime.CompilerServices;
using System.Text;

namespace PdfEditor.Tests.Golden;

/// <summary>
/// Reads, compares and (only when explicitly asked) rewrites the recorded golden files.
/// <para>
/// <b>Regenerating is opt-in and nothing else.</b> A failing comparison never updates anything: it
/// fails, prints the differing lines, and tells you the environment variable to set. Setting
/// <c>PDFEDITOR_UPDATE_GOLDENS=1</c> rewrites the files <em>and still fails the run</em>, so a
/// regeneration can never be mistaken for a green build and the rewritten files have to be read
/// and committed by a human. A suite that silently re-records whatever the code currently does is
/// not a regression net; it is a very expensive way of asserting <c>true</c>.
/// </para>
/// </summary>
internal static class GoldenFile
{
    /// <summary>Set to <c>1</c>/<c>true</c> to rewrite goldens. Never set by the suite itself.</summary>
    public const string UpdateVariable = "PDFEDITOR_UPDATE_GOLDENS";

    public static bool UpdateRequested =>
        Environment.GetEnvironmentVariable(UpdateVariable) is "1" or "true" or "TRUE";

    /// <summary>
    /// The recorded files live next to this source file, so they are read from the repository
    /// rather than from a copy in the build output — a stale copy in <c>bin/</c> would let a
    /// changed golden pass against the old recording.
    /// </summary>
    public static string Directory { get; } = Path.Combine(SourceDirectory(), "goldens");

    private static string SourceDirectory([CallerFilePath] string path = "") =>
        Path.GetDirectoryName(path)!;

    public static string PathFor(string name) => Path.Combine(Directory, name + ".txt");

    /// <summary>
    /// Compares <paramref name="actual"/> against the recording for <paramref name="name"/>.
    /// Returns null when they match, otherwise the failure message to report.
    /// </summary>
    public static string? Compare(string name, string actual)
    {
        actual = actual.ReplaceLineEndings("\n");
        string path = PathFor(name);

        if (UpdateRequested)
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return $"Golden '{name}' was rewritten because {UpdateVariable} is set. "
                + "Review and commit the diff; then unset the variable and re-run. "
                + "(Regeneration always fails the run on purpose — it must never look like a pass.)";
        }

        if (!File.Exists(path))
            return $"No golden recorded for '{name}' (expected {path}). "
                + $"Re-run with {UpdateVariable}=1 to record it, then review the file before committing."
                + Environment.NewLine + Indent(actual);

        string expected = File.ReadAllText(path).ReplaceLineEndings("\n");
        return expected == actual ? null : Describe(name, path, expected, actual);
    }

    /// <summary>A line-oriented diff: enough to see what moved without opening both files.</summary>
    private static string Describe(string name, string path, string expected, string actual)
    {
        string[] want = expected.Split('\n'), got = actual.Split('\n');
        var report = new StringBuilder()
            .Append("Golden '").Append(name).AppendLine("' no longer matches.")
            .Append("Recorded: ").AppendLine(path)
            .AppendLine();

        int shown = 0;
        for (int i = 0; i < Math.Max(want.Length, got.Length) && shown < 25; i++)
        {
            string a = i < want.Length ? want[i] : "<end of file>";
            string b = i < got.Length ? got[i] : "<end of file>";
            if (a == b) continue;
            report.Append("  line ").Append(i + 1).AppendLine(":")
                .Append("    - expected: ").AppendLine(a)
                .Append("    + actual:   ").AppendLine(b);
            shown++;
        }

        return report
            .AppendLine()
            .Append("If this change is intended, re-run with ").Append(UpdateVariable)
            .AppendLine("=1 to re-record, then review the diff before committing.")
            .ToString();
    }

    private static string Indent(string text) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(l => "  | " + l));
}
