using PdfEditor.Core;

namespace PdfEditor.Tests;

public class DocComparerTests
{
    [Fact]
    public void Compare_IdenticalDocuments_ReportsNoChanges()
    {
        byte[] pdf = TestPdfs.WithText(("The quick brown fox", 72, 700, 12));

        var report = DocComparer.Compare(pdf, pdf);

        Assert.True(report.Identical);
        Assert.Equal(0, report.ChangedPages);
        Assert.Equal(0, report.AddedWords);
        Assert.Equal(0, report.RemovedWords);
    }

    [Fact]
    public void Compare_WordAdded_ShowsUpAsAdded()
    {
        byte[] oldPdf = TestPdfs.WithText(("the quick fox", 72, 700, 12));
        byte[] newPdf = TestPdfs.WithText(("the quick brown fox", 72, 700, 12));

        var report = DocComparer.Compare(oldPdf, newPdf);

        Assert.False(report.Identical);
        Assert.Equal(1, report.ChangedPages);
        var page = Assert.Single(report.Pages.Where(p => p.Changed));
        Assert.Contains("brown", page.Added);
        Assert.Empty(page.Removed);
    }

    [Fact]
    public void Compare_WordRemoved_ShowsUpAsRemoved()
    {
        byte[] oldPdf = TestPdfs.WithText(("alpha beta gamma delta", 72, 700, 12));
        byte[] newPdf = TestPdfs.WithText(("alpha gamma delta", 72, 700, 12));

        var report = DocComparer.Compare(oldPdf, newPdf);

        var page = Assert.Single(report.Pages.Where(p => p.Changed));
        Assert.Contains("beta", page.Removed);
        Assert.Empty(page.Added);
        Assert.Equal(1, report.RemovedWords);
    }

    [Fact]
    public void Compare_WordReplaced_ShowsBothAddedAndRemoved()
    {
        byte[] oldPdf = TestPdfs.WithText(("Amount Due 500 dollars", 72, 700, 12));
        byte[] newPdf = TestPdfs.WithText(("Amount Due 750 dollars", 72, 700, 12));

        var report = DocComparer.Compare(oldPdf, newPdf);

        var page = Assert.Single(report.Pages.Where(p => p.Changed));
        Assert.Contains("750", page.Added);
        Assert.Contains("500", page.Removed);
    }

    [Fact]
    public void Compare_AddedPage_CountsAsChanged()
    {
        byte[] oldPdf = TestPdfs.MultiPage(1, "Page");
        byte[] newPdf = TestPdfs.MultiPage(2, "Page");

        var report = DocComparer.Compare(oldPdf, newPdf);

        Assert.Equal(1, report.PagesOld);
        Assert.Equal(2, report.PagesNew);
        Assert.False(report.Identical);
        // The extra page 2 is all-added text.
        Assert.Contains(report.Pages, p => p.Page == 2 && p.Added.Count > 0 && p.Removed.Count == 0);
    }

    [Fact]
    public void Compare_RemovedPage_ShowsItsWordsAsRemoved()
    {
        byte[] oldPdf = TestPdfs.MultiPage(2, "Page");
        byte[] newPdf = TestPdfs.MultiPage(1, "Page");

        var report = DocComparer.Compare(oldPdf, newPdf);

        Assert.Contains(report.Pages, p => p.Page == 2 && p.Removed.Count > 0 && p.Added.Count == 0);
    }

    [Fact]
    public void Compare_ReorderedWords_MinimisesTheDiff()
    {
        // "a b c d" -> "a c b d": the LCS keeps a,c,d (or a,b,d), so exactly one word moves.
        byte[] oldPdf = TestPdfs.WithText(("a b c d", 72, 700, 12));
        byte[] newPdf = TestPdfs.WithText(("a c b d", 72, 700, 12));

        var report = DocComparer.Compare(oldPdf, newPdf);

        // A minimal edit: one word added and one removed (not the whole line rewritten).
        Assert.Equal(1, report.AddedWords);
        Assert.Equal(1, report.RemovedWords);
    }

    [Fact]
    public void Compare_EncryptedVersions_WorkWithPasswords()
    {
        byte[] oldPdf = Encryptor.Encrypt(TestPdfs.WithText(("secret one", 72, 700, 12)), "a");
        byte[] newPdf = Encryptor.Encrypt(TestPdfs.WithText(("secret two", 72, 700, 12)), "b");

        var report = DocComparer.Compare(oldPdf, newPdf, "a", "b");

        var page = Assert.Single(report.Pages.Where(p => p.Changed));
        Assert.Contains("two", page.Added);
        Assert.Contains("one", page.Removed);
    }
}
