using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class PoseSimilarityTransformTests
{
    [Fact]
    public void MapPointUsesCurrentToReferenceScaleRatio()
    {
        var reference = new Pose2D(100, 100, 0) { Scale = 0.5 };
        var current = new Pose2D(200, 150, 90) { Scale = 1.0 };

        var mapped = PoseSimilarityTransform.MapPoint(new Point2D(110, 100), reference, current);

        Assert.Equal(200, mapped.X, 6);
        Assert.Equal(170, mapped.Y, 6);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void MapPointRejectsInvalidReferenceScale(double scale)
    {
        var reference = new Pose2D(0, 0, 0) { Scale = scale };
        var current = new Pose2D(0, 0, 0);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PoseSimilarityTransform.MapPoint(new Point2D(1, 1), reference, current));

        Assert.Equal("referencePose", error.ParamName);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void MapPointRejectsInvalidCurrentScale(double scale)
    {
        var reference = new Pose2D(0, 0, 0);
        var current = new Pose2D(0, 0, 0) { Scale = scale };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PoseSimilarityTransform.MapPoint(new Point2D(1, 1), reference, current));

        Assert.Equal("currentPose", error.ParamName);
    }

    [Theory]
    [InlineData(RoiShapeKind.Rectangle, 0.90)]
    [InlineData(RoiShapeKind.Rectangle, 1.00)]
    [InlineData(RoiShapeKind.Rectangle, 1.10)]
    [InlineData(RoiShapeKind.RotatedRectangle, 0.90)]
    [InlineData(RoiShapeKind.RotatedRectangle, 1.00)]
    [InlineData(RoiShapeKind.RotatedRectangle, 1.10)]
    [InlineData(RoiShapeKind.Circle, 0.90)]
    [InlineData(RoiShapeKind.Circle, 1.00)]
    [InlineData(RoiShapeKind.Circle, 1.10)]
    [InlineData(RoiShapeKind.Polygon, 0.90)]
    [InlineData(RoiShapeKind.Polygon, 1.00)]
    [InlineData(RoiShapeKind.Polygon, 1.10)]
    public void MapRoiScalesAllShapeGeometry(RoiShapeKind shape, double scale)
    {
        var source = CreateRoi(shape);
        var reference = new Pose2D(0, 0, 0);
        var current = new Pose2D(0, 0, 0) { Scale = scale };

        var mapped = PoseSimilarityTransform.MapRoi(source, reference, current);

        switch (shape)
        {
            case RoiShapeKind.Rectangle:
                Assert.Equal(RoiShapeKind.RotatedRectangle, mapped.Shape);
                Assert.Equal(25 * scale, mapped.X, 6);
                Assert.Equal(40 * scale, mapped.Y, 6);
                Assert.Equal(30 * scale, mapped.Width, 6);
                Assert.Equal(40 * scale, mapped.Height, 6);
                break;
            case RoiShapeKind.RotatedRectangle:
                Assert.Equal(10 * scale, mapped.X, 6);
                Assert.Equal(20 * scale, mapped.Y, 6);
                Assert.Equal(30 * scale, mapped.Width, 6);
                Assert.Equal(40 * scale, mapped.Height, 6);
                Assert.Equal(15, mapped.Angle, 6);
                break;
            case RoiShapeKind.Circle:
                Assert.Equal(10 * scale, mapped.X, 6);
                Assert.Equal(20 * scale, mapped.Y, 6);
                Assert.Equal(8 * scale, mapped.Radius, 6);
                break;
            case RoiShapeKind.Polygon:
                Assert.Equal(3, mapped.Points.Count);
                Assert.Equal(10 * scale, mapped.Points[0].X, 6);
                Assert.Equal(20 * scale, mapped.Points[0].Y, 6);
                Assert.Equal(40 * scale, mapped.Points[2].X, 6);
                Assert.Equal(60 * scale, mapped.Points[2].Y, 6);
                break;
        }
    }

    private static RoiDefinition CreateRoi(RoiShapeKind shape)
    {
        return new RoiDefinition
        {
            Shape = shape,
            X = 10,
            Y = 20,
            Width = 30,
            Height = 40,
            Angle = 15,
            Radius = 8,
            Points = [new Point2D(10, 20), new Point2D(30, 20), new Point2D(40, 60)]
        };
    }
}
