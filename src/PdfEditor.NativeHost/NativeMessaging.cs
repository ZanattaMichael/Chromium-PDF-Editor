using System.Text;

namespace PdfEditor.NativeHost;

/// <summary>
/// Chrome native messaging framing: each message is a 32-bit little-endian byte length
/// followed by that many bytes of UTF-8 JSON.
/// </summary>
public static class NativeMessaging
{
    /// <summary>Chrome rejects host→extension messages of 1 MB or more; stay safely below.</summary>
    public const int MaxOutgoingFrame = 900_000;

    /// <summary>
    /// Chrome does not itself cap extension→host message size, so this host must. The
    /// extension chunks anything above 16 MB (host-client.js's REQUEST_CHUNK_THRESHOLD) into
    /// 8 MB base64 slices, so no legitimate single frame should approach this; it exists only
    /// to bound the allocation a malformed or hostile frame can force.
    /// </summary>
    public const int MaxIncomingFrame = 64 * 1024 * 1024;

    public static string? ReadMessage(Stream input)
    {
        var lengthBytes = new byte[4];
        int read = 0;
        while (read < 4)
        {
            int n = input.Read(lengthBytes, read, 4 - read);
            if (n == 0) return read == 0 ? null : throw new EndOfStreamException("Truncated frame header.");
            read += n;
        }
        int length = BitConverter.ToInt32(lengthBytes, 0);
        if (length < 0 || length > MaxIncomingFrame)
            throw new InvalidDataException($"Unreasonable frame length {length}.");
        var buffer = new byte[length];
        read = 0;
        while (read < length)
        {
            int n = input.Read(buffer, read, length - read);
            if (n == 0) throw new EndOfStreamException("Truncated frame body.");
            read += n;
        }
        return Encoding.UTF8.GetString(buffer);
    }

    public static void WriteMessage(Stream output, string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        output.Write(BitConverter.GetBytes(payload.Length), 0, 4);
        output.Write(payload, 0, payload.Length);
        output.Flush();
    }
}
