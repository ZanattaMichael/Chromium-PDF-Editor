using PdfEditor.Core;
using SkiaSharp;
using Xunit;

namespace PdfEditor.Tests;

public class VisualDiffTests
{
    private static (double changed, SKBitmap img) Run(byte[] oldPdf, byte[] newPdf, int page = 1)
    {
        var diff = VisualDiff.DiffPage(oldPdf, newPdf, page, dpi: 96);
        return (diff.ChangedFraction, SKBitmap.Decode(diff.Png));
    }

    [Fact]
    public void DiffPage_IdenticalDocuments_ReportNoChange()
    {
        byte[] pdf = TestPdfs.WithText(("Same text here", 72, 700, 14));

        var (changed, _) = Run(pdf, pdf);

        Assert.True(changed < 0.01, $"identical pages should barely differ, got {changed:P}");
    }

    [Fact]
    public void DiffPage_AddedText_IsFlaggedAsChanged()
    {
        byte[] oldPdf = TestPdfs.WithText(("Hello", 72, 700, 14));
        byte[] newPdf = TestPdfs.WithText(("Hello", 72, 700, 14), ("EXTRA LINE ADDED", 72, 650, 14));

        var (changed, _) = Run(oldPdf, newPdf);

        Assert.True(changed > 0, "an added line should register as changed pixels");
    }

    [Fact]
    public void DiffPage_ColoursAddedContentRed_AndRemovedContentBlue()
    {
        // The new version adds a line the old lacks (added → red); the old had a line the new
        // lacks (removed → blue).
        byte[] oldPdf = TestPdfs.WithText(("ONLY IN OLD", 72, 500, 20));
        byte[] newPdf = TestPdfs.WithText(("ONLY IN NEW", 72, 700, 20));

        var (_, img) = Run(oldPdf, newPdf);

        Assert.True(HasColourNear(img, 220, 30, 30), "added content should be painted red");
        Assert.True(HasColourNear(img, 30, 90, 220), "removed content should be painted blue");
    }

    [Fact]
    public void DiffPage_PageMissingFromOneSide_ShowsItAsChanged()
    {
        byte[] onePage = TestPdfs.WithText(("page one", 72, 700, 14));
        byte[] twoPages = TestPdfs.MultiPage(2);

        // Page 2 exists only in the second document: everything on it is a change.
        var (changed, _) = Run(onePage, twoPages, page: 2);

        Assert.True(changed > 0, "a page present on only one side should be all change");
    }

    [Fact]
    public void DiffPage_PageAbsentFromBothSides_IsBlankWithNoChange()
    {
        byte[] onePage = TestPdfs.WithText(("only page", 72, 700, 14));

        var diff = VisualDiff.DiffPage(onePage, onePage, page: 5);

        Assert.Equal(0d, diff.ChangedFraction);
        Assert.NotEmpty(diff.Png); // a valid (blank) PNG, not an empty array
    }

    private static bool HasColourNear(SKBitmap bmp, int r, int g, int b, int tol = 40)
    {
        for (int y = 0; y < bmp.Height; y += 2)
            for (int x = 0; x < bmp.Width; x += 2)
            {
                var c = bmp.GetPixel(x, y);
                if (Math.Abs(c.Red - r) <= tol && Math.Abs(c.Green - g) <= tol && Math.Abs(c.Blue - b) <= tol)
                    return true;
            }
        return false;
    }
}
