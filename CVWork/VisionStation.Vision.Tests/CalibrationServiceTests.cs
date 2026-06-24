using OpenCvSharp;
using VisionStation.Domain;
using VisionStation.Vision;
using VisionStation.Vision.Tools;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class CalibrationServiceTests
{
    [Fact]
    public void NinePointAffineCalibrationMapsImageToWorld()
    {
        var service = new OpenCvCalibrationService();
        var pairs = CreateGridPairs(
            columns: 3,
            rows: 3,
            worldStep: 10,
            worldToImage: point => new Point2D(
                100 + 2.0 * point.X - 0.3 * point.Y,
                50 + 0.2 * point.X + 1.5 * point.Y));

        var calibration = service.CalibratePlane(pairs, PlaneCalibrationModel.Affine, "mm");
        var expectedWorld = new Point2D(12.5, 16.5);
        var imagePoint = new Point2D(
            100 + 2.0 * expectedWorld.X - 0.3 * expectedWorld.Y,
            50 + 0.2 * expectedWorld.X + 1.5 * expectedWorld.Y);
        var mapped = PlaneCalibrationMapper.MapImageToWorld(calibration, imagePoint);

        Assert.Equal(PlaneCalibrationModel.Affine, calibration.Model);
        Assert.Equal(9, calibration.PointCount);
        Assert.True(calibration.RmsError < 1e-4);
        Assert.InRange(mapped.X, expectedWorld.X - 1e-3, expectedWorld.X + 1e-3);
        Assert.InRange(mapped.Y, expectedWorld.Y - 1e-3, expectedWorld.Y + 1e-3);
    }

    [Fact]
    public void HomographyCalibrationMapsPerspectivePlane()
    {
        var service = new OpenCvCalibrationService();
        double[] imageToWorld =
        [
            0.55, 0.02, -30,
            -0.01, 0.62, 10,
            0.0002, -0.00015, 1
        ];
        var imagePoints = new[]
        {
            new Point2D(80, 70),
            new Point2D(220, 65),
            new Point2D(360, 75),
            new Point2D(90, 200),
            new Point2D(230, 210),
            new Point2D(370, 205),
            new Point2D(75, 340),
            new Point2D(215, 350),
            new Point2D(355, 345)
        };
        var pairs = imagePoints
            .Select(point => new CalibrationPointPair(point, TransformHomography(imageToWorld, point)))
            .ToArray();

        var calibration = service.CalibratePlane(pairs, PlaneCalibrationModel.Homography, "mm");
        var testImagePoint = new Point2D(285, 260);
        var expectedWorld = TransformHomography(imageToWorld, testImagePoint);
        var mapped = PlaneCalibrationMapper.MapImageToWorld(calibration, testImagePoint);

        Assert.Equal(PlaneCalibrationModel.Homography, calibration.Model);
        Assert.True(calibration.RmsError < 1e-3);
        Assert.InRange(mapped.X, expectedWorld.X - 1e-2, expectedWorld.X + 1e-2);
        Assert.InRange(mapped.Y, expectedWorld.Y - 1e-2, expectedWorld.Y + 1e-2);
    }

    [Fact]
    public void CameraCalibrationProducesLowSyntheticReprojectionError()
    {
        var service = new OpenCvCalibrationService();
        var pattern = new ChessboardCalibrationPattern
        {
            Columns = 7,
            Rows = 6,
            SquareSize = 25
        };
        var observations = CreateSyntheticCameraObservations(pattern);

        var calibration = service.CalibrateCamera(observations, pattern, minimumViews: 3);

        Assert.True(calibration.RmsReprojectionError < 1e-3);
        Assert.Equal(observations.Count, calibration.Views.Count);
        Assert.All(calibration.Views, view => Assert.True(view.ReprojectionError < 1e-3));
        Assert.Equal(9, calibration.CameraMatrix.Length);
        Assert.True(calibration.DistortionCoefficients.Length >= 5);
    }

    [Fact]
    public void EstimatePoseReturnsLowReprojectionError()
    {
        var service = new OpenCvCalibrationService();
        var pattern = new ChessboardCalibrationPattern
        {
            Columns = 7,
            Rows = 6,
            SquareSize = 25
        };
        var objectPoints = CreateObjectPoints(pattern);
        var cameraMatrix = CreateCameraMatrix();
        double[] distCoeffs = [0, 0, 0, 0, 0];
        double[] rvec = [0.12, -0.08, 0.04];
        double[] tvec = [-35, 20, 900];
        Cv2.ProjectPoints(objectPoints, rvec, tvec, cameraMatrix, distCoeffs, out Point2f[] imagePoints, out _);
        var calibration = new CameraCalibrationResult
        {
            ImageWidth = 1280,
            ImageHeight = 960,
            Pattern = pattern,
            CameraMatrix =
            [
                cameraMatrix[0, 0], cameraMatrix[0, 1], cameraMatrix[0, 2],
                cameraMatrix[1, 0], cameraMatrix[1, 1], cameraMatrix[1, 2],
                cameraMatrix[2, 0], cameraMatrix[2, 1], cameraMatrix[2, 2]
            ],
            DistortionCoefficients = distCoeffs
        };

        var pose = service.EstimatePose(
            calibration,
            imagePoints.Select(point => new Point2D(point.X, point.Y)).ToArray());

        Assert.True(pose.ReprojectionError < 1e-3);
        Assert.Equal(3, pose.RotationVector.Length);
        Assert.Equal(3, pose.TranslationVector.Length);
    }

    [Fact]
    public async Task CoordinateTransformToolUsesCalibrationMatrixParameter()
    {
        double[] imageToWorld =
        [
            0.5, 0, -10,
            0, 0.5, 20
        ];
        var sourceTool = new VisionToolDefinition
        {
            Id = "source",
            Name = "Source",
            Kind = VisionToolKind.TemplatePoint
        };
        var transformTool = new VisionToolDefinition
        {
            Id = "transform",
            Name = "Transform",
            Kind = VisionToolKind.CoordinateTransform,
            Parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["input:PointInput:toolId"] = sourceTool.Id,
                ["matrix"] = CalibrationProfileText.FormatMatrix(imageToWorld),
                ["model"] = "Affine",
                ["unit"] = "mm"
            }
        };
        using var context = new VisionToolContext(new Recipe
        {
            Tools = [sourceTool, transformTool]
        }, CreateGrayFrame());
        context.SetPortOutput(sourceTool, "CenterOutput", new Point2D(50, 80));

        var result = await new CoordinateTransformTool().ExecuteAsync(transformTool, context);

        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.Equal("15", result.Data["worldX"]);
        Assert.Equal("60", result.Data["worldY"]);
        Assert.Equal("mm", result.Data["unit"]);
    }

    private static IReadOnlyList<CalibrationPointPair> CreateGridPairs(
        int columns,
        int rows,
        double worldStep,
        Func<Point2D, Point2D> worldToImage)
    {
        var pairs = new List<CalibrationPointPair>();
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var world = new Point2D(column * worldStep, row * worldStep);
                pairs.Add(new CalibrationPointPair(worldToImage(world), world));
            }
        }

        return pairs;
    }

    private static IReadOnlyList<ChessboardDetectionResult> CreateSyntheticCameraObservations(
        ChessboardCalibrationPattern pattern)
    {
        var objectPoints = CreateObjectPoints(pattern);
        var cameraMatrix = CreateCameraMatrix();
        double[] distCoeffs = [0, 0, 0, 0, 0];
        (double[] Rvec, double[] Tvec)[] views =
        [
            ([0.02, 0.03, 0.00], [-75, -60, 850]),
            ([0.10, 0.04, -0.03], [20, -45, 900]),
            ([-0.08, 0.12, 0.05], [-20, 35, 940]),
            ([0.05, -0.10, 0.08], [65, 15, 880]),
            ([0.14, -0.05, -0.06], [-55, 45, 910]),
            ([-0.12, -0.08, 0.04], [35, -20, 970]),
            ([0.04, 0.15, -0.10], [80, 55, 930]),
            ([-0.05, 0.06, 0.12], [-85, 10, 890])
        ];

        return views
            .Select((view, index) =>
            {
                Cv2.ProjectPoints(
                    objectPoints,
                    view.Rvec,
                    view.Tvec,
                    cameraMatrix,
                    distCoeffs,
                    out Point2f[] imagePoints,
                    out _);

                return new ChessboardDetectionResult
                {
                    FrameId = $"view-{index}",
                    ImageWidth = 1280,
                    ImageHeight = 960,
                    Found = true,
                    ImagePoints = imagePoints.Select(point => new Point2D(point.X, point.Y)).ToArray()
                };
            })
            .ToArray();
    }

    private static Point3f[] CreateObjectPoints(ChessboardCalibrationPattern pattern)
    {
        var points = new Point3f[pattern.Columns * pattern.Rows];
        var index = 0;
        for (var row = 0; row < pattern.Rows; row++)
        {
            for (var column = 0; column < pattern.Columns; column++)
            {
                points[index++] = new Point3f(
                    (float)(column * pattern.SquareSize),
                    (float)(row * pattern.SquareSize),
                    0);
            }
        }

        return points;
    }

    private static double[,] CreateCameraMatrix()
    {
        return new[,]
        {
            { 900.0, 0.0, 640.0 },
            { 0.0, 910.0, 480.0 },
            { 0.0, 0.0, 1.0 }
        };
    }

    private static Point2D TransformHomography(IReadOnlyList<double> matrix, Point2D point)
    {
        var denominator = matrix[6] * point.X + matrix[7] * point.Y + matrix[8];
        return new Point2D(
            (matrix[0] * point.X + matrix[1] * point.Y + matrix[2]) / denominator,
            (matrix[3] * point.X + matrix[4] * point.Y + matrix[5]) / denominator);
    }

    private static ImageFrame CreateGrayFrame()
    {
        return new ImageFrame(
            "frame",
            2,
            2,
            2,
            PixelFormatKind.Gray8,
            [0, 0, 0, 0],
            DateTimeOffset.UtcNow,
            "test");
    }
}
