using MyBibleApp.Helpers;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class OrientationPanHelperTests
{
    [Theory]
    [InlineData(400, 800, true)]   // taller than wide -> portrait
    [InlineData(800, 400, false)]  // wider than tall -> landscape
    [InlineData(500, 500, true)]   // square -> treat as portrait
    public void IsPortrait_ClassifiesFromWidthAndHeight(double width, double height, bool expected)
    {
        Assert.Equal(expected, OrientationPanHelper.IsPortrait(width, height));
    }

    [Fact]
    public void ClampPanX_WithinRange_ReturnsStoredValue()
    {
        var result = OrientationPanHelper.ClampPanX(stored: 100, extentWidth: 1000, viewportWidth: 400);
        Assert.Equal(100, result);
    }

    [Fact]
    public void ClampPanX_ExceedsNewMax_ClampsDown()
    {
        // maxX = 1000 - 400 = 600, stored 800 should clamp to 600
        var result = OrientationPanHelper.ClampPanX(stored: 800, extentWidth: 1000, viewportWidth: 400);
        Assert.Equal(600, result);
    }

    [Fact]
    public void ClampPanX_Negative_ClampsToZero()
    {
        var result = OrientationPanHelper.ClampPanX(stored: -50, extentWidth: 1000, viewportWidth: 400);
        Assert.Equal(0, result);
    }

    [Fact]
    public void ClampPanX_ViewportWiderThanExtent_ReturnsZero()
    {
        // maxX = max(0, 400 - 1000) = 0
        var result = OrientationPanHelper.ClampPanX(stored: 300, extentWidth: 400, viewportWidth: 1000);
        Assert.Equal(0, result);
    }
}
