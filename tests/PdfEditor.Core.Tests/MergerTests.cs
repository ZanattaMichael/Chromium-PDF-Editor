using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Tests;

public class MergerTests
{
    [Fact]
    public void MergesDocuments_InOrder()
    {
        byte[] a = TestPdfs.MultiPage(2, "DocA");
        byte[] b = TestPdfs.MultiPage(3, "DocB");

        byte[] merged = Merger.Merge(new[] { a, b });

        var info = PdfInspector.GetInfo(merged);
        Assert.Equal(5, info.PageCount);
        Assert.Contains("DocA 1", TestPdfAssert.ExtractText(merged, 1));
        Assert.Contains("DocA 2", TestPdfAssert.ExtractText(merged, 2));
        Assert.Contains("DocB 1", TestPdfAssert.ExtractText(merged, 3));
        Assert.Contains("DocB 3", TestPdfAssert.ExtractText(merged, 5));
    }

    [Fact]
    public void MergeSingleDocument_ReturnsSameBytes()
    {
        byte[] a = TestPdfs.MultiPage(1);
        Assert.Equal(a, Merger.Merge(new[] { a }));
    }

    [Fact]
    public void MergeEmptyList_Throws()
    {
        Assert.Throws<ArgumentException>(() => Merger.Merge(Array.Empty<byte[]>()));
    }

    [Fact]
    public void MergesEncryptedSource_WithPassword()
    {
        byte[] a = TestPdfs.MultiPage(1, "Open");
        byte[] b = Encryptor.Encrypt(TestPdfs.MultiPage(1, "Locked"), "pw");

        byte[] merged = Merger.Merge(new[] { a, b }, new string?[] { null, "pw" });

        Assert.Equal(2, PdfInspector.GetInfo(merged).PageCount);
        Assert.Contains("Locked 1", TestPdfAssert.ExtractText(merged, 2));
        Assert.False(Encryptor.IsEncrypted(merged));
    }
}
