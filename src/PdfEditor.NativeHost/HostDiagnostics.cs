using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using PdfEditor.Core;

namespace PdfEditor.NativeHost;

/// <summary>
/// Self-reported health/environment of the native messaging host: version, .NET runtime, OS,
/// architecture, where the executable lives, and which optional capabilities are available. It is
/// exposed two ways — as the <c>diagnostics</c> message action (so the extension can show it when
/// the host connects) and as a command-line mode (<c>PdfEditor.NativeHost --diagnostics</c>) so a
/// user can confirm the host runs at all, independently of the browser. Deliberately carries no
/// personal data (no user name, home path, or machine name).
/// </summary>
public static class HostDiagnostics
{
    private static readonly JsonSerializerOptions CliJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Collects the diagnostic snapshot returned by both the action and the CLI.</summary>
    public static object Collect() => new
    {
        host = "com.pdfeditor.host",
        version = typeof(HostDiagnostics).Assembly.GetName().Version?.ToString() ?? "unknown",
        runtime = RuntimeInformation.FrameworkDescription,
        os = RuntimeInformation.OSDescription,
        osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
        processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
        executablePath = Environment.ProcessPath ?? "unknown",
        ocrAvailable = OcrTool.CanOcr,
        utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// If <paramref name="args"/> requests diagnostics (the first argument is <c>--diagnostics</c>,
    /// <c>--selftest</c>, or <c>--version</c>), writes the snapshot as pretty JSON to
    /// <paramref name="output"/> and returns an exit code; otherwise returns <c>null</c> so the
    /// caller proceeds with the normal native-messaging loop. A real browser launch passes the
    /// calling extension's origin (e.g. <c>chrome-extension://…</c>) as the first argument, never
    /// one of these flags, so this never intercepts a genuine host session.
    /// </summary>
    public static int? TryRunCli(string[] args, TextWriter output)
    {
        if (args.Length == 0) return null;
        switch (args[0])
        {
            case "--diagnostics":
            case "--selftest":
            case "--version":
                output.WriteLine(JsonSerializer.Serialize(Collect(), CliJson));
                return 0;
            default:
                return null;
        }
    }
}
