using System.Text;
using System.Text.Json;
using Xunit;

namespace PdfEditor.NativeHost.Tests;

public class ChunkAssemblerTests
{
    private readonly ChunkAssembler _assembler = new();

    private static string Chunk(string id, int index, int count, string data) =>
        JsonSerializer.Serialize(new { id, chunkIndex = index, chunkCount = count, data });

    [Fact]
    public void NonChunkedFrame_PassesThroughUnchanged()
    {
        string frame = """{"id":"a","action":"ping"}""";

        Assert.Equal(frame, _assembler.Feed(frame));
    }

    [Fact]
    public void ReassemblesChunksInOrder()
    {
        string original = """{"id":"req-1","action":"info","payload":{"pdf":"AAAABBBBCCCCDDDD"}}""";
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(original));
        int chunkSize = encoded.Length / 3 + 1;
        int count = (encoded.Length + chunkSize - 1) / chunkSize;

        string? result = null;
        for (int i = 0; i < count; i++)
        {
            string slice = encoded.Substring(i * chunkSize, Math.Min(chunkSize, encoded.Length - i * chunkSize));
            result = _assembler.Feed(Chunk("req-1", i, count, slice));
        }

        Assert.Equal(original, result);
    }

    [Fact]
    public void ReassemblesChunks_FedOutOfOrder()
    {
        string original = """{"id":"req-2","action":"ping"}""";
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(original));
        int chunkSize = Math.Max(1, encoded.Length / 4);
        int count = (encoded.Length + chunkSize - 1) / chunkSize;
        var slices = Enumerable.Range(0, count)
            .Select(i => encoded.Substring(i * chunkSize, Math.Min(chunkSize, encoded.Length - i * chunkSize)))
            .ToList();

        string? result = null;
        // Feed the last chunk first, then the rest in forward order.
        var order = new[] { count - 1 }.Concat(Enumerable.Range(0, count - 1));
        foreach (int i in order)
            result = _assembler.Feed(Chunk("req-2", i, count, slices[i]));

        Assert.Equal(original, result);
    }

    [Fact]
    public void ReturnsNull_UntilAllChunksReceived()
    {
        Assert.Null(_assembler.Feed(Chunk("req-3", 0, 2, "aGVsbG8=")));
    }

    [Fact]
    public void InterleavedRequests_WithDifferentIds_DoNotCorruptEachOther()
    {
        string first = """{"id":"a","action":"one"}""";
        string second = """{"id":"b","action":"two"}""";
        string firstEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(first));
        string secondEncoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(second));

        // Interleave: chunk 0 of "a", chunk 0 of "b", chunk 1 of "a", chunk 1 of "b".
        int half = firstEncoded.Length / 2 + 1;
        _assembler.Feed(Chunk("a", 0, 2, firstEncoded.Substring(0, Math.Min(half, firstEncoded.Length))));
        _assembler.Feed(Chunk("b", 0, 2, secondEncoded.Substring(0, Math.Min(half, secondEncoded.Length))));
        string? resultA = _assembler.Feed(Chunk("a", 1, 2,
            firstEncoded.Length > half ? firstEncoded.Substring(half) : ""));
        string? resultB = _assembler.Feed(Chunk("b", 1, 2,
            secondEncoded.Length > half ? secondEncoded.Substring(half) : ""));

        Assert.Equal(first, resultA);
        Assert.Equal(second, resultB);
    }

    [Fact]
    public void OutOfRangeChunkIndex_Throws()
    {
        Assert.Throws<InvalidDataException>(() => _assembler.Feed(Chunk("req-4", 5, 2, "AAAA")));
    }

    [Fact]
    public void ExcessiveChunkCount_ThrowsInsteadOfAllocating()
    {
        // A malformed or hostile frame must not be able to force an unbounded array
        // allocation just by claiming a huge chunkCount.
        Assert.Throws<InvalidDataException>(() => _assembler.Feed(Chunk("req-6", 0, 10_000_001, "AAAA")));
    }

    [Fact]
    public void ZeroOrNegativeChunkCount_Throws()
    {
        Assert.Throws<InvalidDataException>(() => _assembler.Feed(Chunk("req-7", 0, 0, "AAAA")));
    }

    [Fact]
    public void RestartingAnId_WithADifferentChunkCount_DiscardsThePreviousAttempt()
    {
        // Feed a partial 3-chunk attempt for "req-5", then restart it as a 2-chunk send —
        // the assembler must not try to merge state from the abandoned attempt.
        _assembler.Feed(Chunk("req-5", 0, 3, "AAAA"));

        string original = """{"id":"req-5","action":"ping"}""";
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(original));
        int half = encoded.Length / 2 + 1;
        _assembler.Feed(Chunk("req-5", 0, 2, encoded.Substring(0, Math.Min(half, encoded.Length))));
        string? result = _assembler.Feed(Chunk("req-5", 1, 2,
            encoded.Length > half ? encoded.Substring(half) : ""));

        Assert.Equal(original, result);
    }
}
