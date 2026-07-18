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

    [Theory]
    [InlineData(RoiShapeKind.Rectangle)]
    [InlineData(RoiShapeKind.RotatedRectangle)]
    [InlineData(RoiShapeKind.Circle)]
    [InlineData(RoiShapeKind.Polygon)]
    public void MapRoiUsesCurrentToReferenceScaleRatioBeforeRotation(RoiShapeKind shape)
    {
        var source = CreateRoi(shape);
        var reference = new Pose2D(10, 20, 10) { Scale = 0.5 };
        var current = new Pose2D(100, 200, 45) { Scale = 1.1 };
        const double expectedRatio = 2.2;
        const double expectedAngleDelta = 35;

        var mapped = PoseSimilarityTransform.MapRoi(source, reference, current);

        switch (shape)
        {
            case RoiShapeKind.Rectangle:
                {
                    var expectedCenter = MapExpectedPoint(
                        new Point2D(25, 40),
                        reference,
                        current,
                        expectedRatio,
                        expectedAngleDelta);
                    Assert.Equal(RoiShapeKind.RotatedRectangle, mapped.Shape);
                    Assert.Equal(expectedCenter.X, mapped.X, 6);
                    Assert.Equal(expectedCenter.Y, mapped.Y, 6);
                    Assert.Equal(30 * expectedRatio, mapped.Width, 6);
                    Assert.Equal(40 * expectedRatio, mapped.Height, 6);
                    Assert.Equal(expectedAngleDelta, mapped.Angle, 6);
                    break;
                }
            case RoiShapeKind.RotatedRectangle:
                Assert.Equal(current.X, mapped.X, 6);
                Assert.Equal(current.Y, mapped.Y, 6);
                Assert.Equal(30 * expectedRatio, mapped.Width, 6);
                Assert.Equal(40 * expectedRatio, mapped.Height, 6);
                Assert.Equal(15 + expectedAngleDelta, mapped.Angle, 6);
                break;
            case RoiShapeKind.Circle:
                Assert.Equal(current.X, mapped.X, 6);
                Assert.Equal(current.Y, mapped.Y, 6);
                Assert.Equal(8 * expectedRatio, mapped.Radius, 6);
                break;
            case RoiShapeKind.Polygon:
                Assert.Equal(source.Points.Count, mapped.Points.Count);
                for (var index = 0; index < source.Points.Count; index++)
                {
                    var expectedPoint = MapExpectedPoint(
                        source.Points[index],
                        reference,
                        current,
                        expectedRatio,
                        expectedAngleDelta);
                    Assert.Equal(expectedPoint.X, mapped.Points[index].X, 6);
                    Assert.Equal(expectedPoint.Y, mapped.Points[index].Y, 6);
                }

                Assert.True(mapped.Points.Max(point => point.X) > mapped.Points.Min(point => point.X));
                Assert.True(mapped.Points.Max(point => point.Y) > mapped.Points.Min(point => point.Y));
                break;
        }
    }

    private static Point2D MapExpectedPoint(
        Point2D point,
        Pose2D reference,
        Pose2D current,
        double scaleRatio,
        double angleDelta)
    {
        var dx = (point.X - reference.X) * scaleRatio;
        var dy = (point.Y - reference.Y) * scaleRatio;
        var radians = angleDelta * Math.PI / 180d;
        return new Point2D(
            current.X + dx * Math.Cos(radians) - dy * Math.Sin(radians),
            current.Y + dx * Math.Sin(radians) + dy * Math.Cos(radians));
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
