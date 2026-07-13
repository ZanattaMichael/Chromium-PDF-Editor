using System.Text;
using System.Text.Json.Nodes;

namespace PdfEditor.NativeHost;

/// <summary>
/// Reassembles chunked incoming requests. The extension splits very large requests into
/// frames of the form {id, chunkIndex, chunkCount, data} where data is base64 of a slice
/// of the underlying JSON request.
/// </summary>
public sealed class ChunkAssembler
{
    private readonly Dictionary<string, (string?[] Parts, int Received)> _pending = new();

    /// <summary>
    /// Feeds one raw frame. Returns the complete request JSON when available,
    /// or null when more chunks are needed.
    /// </summary>
    public string? Feed(string frameJson)
    {
        var node = JsonNode.Parse(frameJson)?.AsObject();
        if (node == null || node["chunkIndex"] == null || node["chunkCount"] == null)
            return frameJson; // not chunked — pass through

        string id = node["id"]?.GetValue<string>() ?? "";
        int index = node["chunkIndex"]!.GetValue<int>();
        int count = node["chunkCount"]!.GetValue<int>();
        string data = node["data"]?.GetValue<string>() ?? "";

        if (!_pending.TryGetValue(id, out var entry) || entry.Parts.Length != count)
            entry = (new string?[count], 0);
        if (index < 0 || index >= count)
            throw new InvalidDataException($"Chunk index {index} out of range for '{id}'.");
        if (entry.Parts[index] == null) entry.Received++;
        entry.Parts[index] = data;
        _pending[id] = entry;

        if (entry.Received < count) return null;
        _pending.Remove(id);
        string encoded = string.Concat(entry.Parts.Select(p => p ?? ""));
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }
}
