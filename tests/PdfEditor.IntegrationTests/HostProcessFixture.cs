using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PdfEditor.NativeHost;

namespace PdfEditor.IntegrationTests;

/// <summary>
/// Spawns the real native-messaging host executable and talks to it over stdin/stdout
/// using Chrome's framing, exactly as a Chromium browser would.
/// </summary>
public sealed class HostProcessFixture : IDisposable
{
    private readonly Process _process;
    private readonly object _lock = new();

    public HostProcessFixture()
    {
        string hostDll = LocateHostDll();
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { hostDll },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        _process.Start();
    }

    /// <summary>Sends one request and returns the parsed response (reassembling chunks).</summary>
    public JsonObject Send(object request, TimeSpan? timeout = null)
    {
        string json = JsonSerializer.Serialize(request);
        lock (_lock)
        {
            NativeMessaging.WriteMessage(_process.StandardInput.BaseStream, json);
            return ReadResponse(timeout ?? TimeSpan.FromSeconds(60));
        }
    }

    /// <summary>
    /// Writes a raw, already-serialized frame body verbatim — used to feed the host hostile
    /// or malformed frames (non-JSON, wrong shape) that <see cref="Send"/> would never produce.
    /// </summary>
    public JsonObject SendRaw(string frameBody, TimeSpan? timeout = null)
    {
        lock (_lock)
        {
            NativeMessaging.WriteMessage(_process.StandardInput.BaseStream, frameBody);
            return ReadResponse(timeout ?? TimeSpan.FromSeconds(60));
        }
    }

    /// <summary>Sends a request pre-split into chunk frames, as the extension does for large payloads.</summary>
    public JsonObject SendChunked(object request, int chunkSize = 640)
    {
        string json = JsonSerializer.Serialize(request);
        string id = JsonNode.Parse(json)!["id"]!.GetValue<string>();
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        int count = (encoded.Length + chunkSize - 1) / chunkSize;
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                var frame = new
                {
                    id,
                    chunkIndex = i,
                    chunkCount = count,
                    data = encoded.Substring(i * chunkSize, Math.Min(chunkSize, encoded.Length - i * chunkSize))
                };
                NativeMessaging.WriteMessage(_process.StandardInput.BaseStream,
                    JsonSerializer.Serialize(frame));
            }
            return ReadResponse(TimeSpan.FromSeconds(60));
        }
    }

    private JsonObject ReadResponse(TimeSpan timeout)
    {
        var chunks = new SortedDictionary<int, string>();
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Host did not respond in time.");
            string? frame = NativeMessaging.ReadMessage(_process.StandardOutput.BaseStream)
                ?? throw new EndOfStreamException("Host closed the pipe.");
            var node = JsonNode.Parse(frame)!.AsObject();
            if (node["chunkIndex"] == null)
                return node;

            chunks[node["chunkIndex"]!.GetValue<int>()] = node["data"]!.GetValue<string>();
            if (chunks.Count == node["chunkCount"]!.GetValue<int>())
            {
                string json = Encoding.UTF8.GetString(
                    Convert.FromBase64String(string.Concat(chunks.Values)));
                return JsonNode.Parse(json)!.AsObject();
            }
        }
    }

    private static string LocateHostDll()
    {
        // The integration test project references the host project, so the host dll and
        // its dependency tree are built; run it from its own output folder so its
        // runtimeconfig and native assets resolve.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PdfEditor.sln")))
            dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("Repository root not found.");

        string configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}")
            ? "Release" : "Debug";
        string dll = Path.Combine(dir.FullName, "src", "PdfEditor.NativeHost", "bin",
            configuration, "net8.0", "PdfEditor.NativeHost.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException($"Host binary not built: {dll}");
        return dll;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close(); // EOF → host exits cleanly
                if (!_process.WaitForExit(5000)) _process.Kill();
            }
        }
        finally
        {
            _process.Dispose();
        }
    }
}
