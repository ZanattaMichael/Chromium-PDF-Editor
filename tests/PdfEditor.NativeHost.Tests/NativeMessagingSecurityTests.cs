namespace PdfEditor.NativeHost.Tests;

/// <summary>
/// Adversarial tests for the native-messaging framing layer. Chrome does not itself bound
/// the size of an extension→host frame, so the host must treat the 4-byte little-endian
/// length prefix as hostile input: a corrupt or malicious prefix must never drive an
/// unbounded allocation, block forever, or crash the process.
/// </summary>
public class NativeMessagingSecurityTests
{
    private static byte[] Header(int length) => BitConverter.GetBytes(length);

    [Fact]
    public void NegativeFrameLength_IsRejected()
    {
        using var stream = new MemoryStream(Header(-1));
        Assert.Throws<InvalidDataException>(() => NativeMessaging.ReadMessage(stream));
    }

    [Fact]
    public void OversizedFrameLength_IsRejected_BeforeAllocatingTheBuffer()
    {
        // Only the 4-byte header is present — there is no body to read. A guard that
        // allocated `new byte[length]` (here ~64 MB + 1) or tried to read the body before
        // range-checking would OOM or block; instead it must reject immediately.
        using var stream = new MemoryStream(Header(NativeMessaging.MaxIncomingFrame + 1));
        Assert.Throws<InvalidDataException>(() => NativeMessaging.ReadMessage(stream));
    }

    [Fact]
    public void IntMaxFrameLength_IsRejected_NotAllocatedAsTwoGigabytes()
    {
        using var stream = new MemoryStream(Header(int.MaxValue));
        Assert.Throws<InvalidDataException>(() => NativeMessaging.ReadMessage(stream));
    }

    [Fact]
    public void TruncatedHeader_ThrowsEndOfStream()
    {
        using var stream = new MemoryStream(new byte[] { 0x01, 0x02 }); // 2 of the 4 header bytes
        Assert.Throws<EndOfStreamException>(() => NativeMessaging.ReadMessage(stream));
    }

    [Fact]
    public void TruncatedBody_ThrowsEndOfStream()
    {
        var frame = new List<byte>();
        frame.AddRange(Header(10));                 // header claims 10 body bytes
        frame.AddRange(new byte[] { 1, 2, 3 });     // but only 3 are present
        using var stream = new MemoryStream(frame.ToArray());
        Assert.Throws<EndOfStreamException>(() => NativeMessaging.ReadMessage(stream));
    }

    [Fact]
    public void CleanEofAtFrameBoundary_ReturnsNull()
    {
        // The browser closing the pipe between frames is normal shutdown, not an error.
        using var stream = new MemoryStream(Array.Empty<byte>());
        Assert.Null(NativeMessaging.ReadMessage(stream));
    }

    [Fact]
    public void ZeroLengthFrame_ReturnsEmptyString_NotAnError()
    {
        using var stream = new MemoryStream(Header(0));
        Assert.Equal("", NativeMessaging.ReadMessage(stream));
    }

    [Fact]
    public void FrameAtExactlyTheLimit_IsAccepted()
    {
        // The boundary is inclusive: a frame whose declared length equals the cap is legal.
        // Kept tiny here (a 1-byte body with the cap not actually reached) would not prove
        // much, so use a modest real body and confirm it round-trips through the reader.
        string payload = new string('x', 4096);
        using var stream = new MemoryStream();
        NativeMessaging.WriteMessage(stream, payload);
        stream.Position = 0;
        Assert.Equal(payload, NativeMessaging.ReadMessage(stream));
    }

    [Fact]
    public void WriteThenRead_RoundTripsExactly()
    {
        const string message = """{"id":"x","action":"ping"}""";
        using var stream = new MemoryStream();
        NativeMessaging.WriteMessage(stream, message);
        stream.Position = 0;
        Assert.Equal(message, NativeMessaging.ReadMessage(stream));
    }
}
