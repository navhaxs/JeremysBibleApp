using Avalonia;
using MyBibleApp.Controls;
using Xunit;

namespace MyBibleApp.Journal.Tests.Unit;

public class InkEraserGeometryTests
{
    [Fact]
    public void DistToSegmentSq_PointOnSegment_ReturnsZero()
    {
        var dist = InkOverlayCanvas.DistToSegmentSq(
            new Point(5, 0), new Point(0, 0), new Point(10, 0));
        Assert.Equal(0.0, dist, precision: 6);
    }

    [Fact]
    public void DistToSegmentSq_PointPerpendicularAboveSegment_ReturnsSquaredHeight()
    {
        var dist = InkOverlayCanvas.DistToSegmentSq(
            new Point(5, 3), new Point(0, 0), new Point(10, 0));
        Assert.Equal(9.0, dist, precision: 6);
    }

    [Fact]
    public void DistToSegmentSq_PointPastEndpoint_ReturnsDistToEndpoint()
    {
        var dist = InkOverlayCanvas.DistToSegmentSq(
            new Point(15, 0), new Point(0, 0), new Point(10, 0));
        Assert.Equal(25.0, dist, precision: 6);
    }

    [Fact]
    public void DistToSegmentSq_PointPastStartpoint_ReturnsDistToStartpoint()
    {
        var dist = InkOverlayCanvas.DistToSegmentSq(
            new Point(-3, 0), new Point(0, 0), new Point(10, 0));
        Assert.Equal(9.0, dist, precision: 6);
    }

    [Fact]
    public void DistToSegmentSq_ZeroLengthSegment_ReturnsDistToPoint()
    {
        var dist = InkOverlayCanvas.DistToSegmentSq(
            new Point(8, 5), new Point(5, 5), new Point(5, 5));
        Assert.Equal(9.0, dist, precision: 6);
    }

    [Fact]
    public void DistToSegmentSq_EraserHitsMidpointOfSparseStroke()
    {
        const double radiusSq = 14.0 * 14.0;
        var dist = InkOverlayCanvas.DistToSegmentSq(
            new Point(50, 10), new Point(0, 0), new Point(100, 0));
        Assert.True(dist <= radiusSq, $"Expected dist² {dist} <= {radiusSq}");
    }
}
