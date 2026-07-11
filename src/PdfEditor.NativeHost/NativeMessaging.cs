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
        if (length < 0 || length > 512 * 1024 * 1024)
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
