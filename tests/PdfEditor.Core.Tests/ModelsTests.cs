using PdfEditor.Core;
using Xunit;

namespace PdfEditor.Tests;

public class ModelsTests
{
    [Fact]
    public void RectRegion_ComputesRightAndTopFromOrigin()
    {
        var region = new RectRegion(1, X: 50, Y: 100, Width: 30, Height: 20);

        Assert.Equal(80, region.Right);
        Assert.Equal(120, region.Top);
    }
}
