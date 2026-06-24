using System.Diagnostics;
using System.Globalization;
using VisionStation.Domain;
using VisionStation.Vision;

namespace VisionStation.Vision.Tools;

public sealed class CoordinateTransformTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.CoordinateTransform;

    public Task<ToolResult> ExecuteAsync(
        VisionToolDefinition definition,
        VisionToolContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();
        if (!TryResolveCalibration(context.Recipe, definition.Parameters, out var calibration, out var calibrationError))
        {
            stopwatch.Stop();
            return Task.FromResult(CreateFailure(definition, stopwatch.Elapsed, calibrationError));
        }

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["model"] = calibration.Model.ToString(),
            ["unit"] = calibration.Unit,
            ["rmsError"] = calibration.RmsError.ToInvariant(),
            ["maxError"] = calibration.MaxError.ToInvariant(),
            ["pointCount"] = calibration.PointCount.ToString(CultureInfo.InvariantCulture),
            ["inlierCount"] = calibration.InlierCount.ToString(CultureInfo.InvariantCulture)
        };

        if (context.TryGetPortInput<Point2D>(definition, "PointInput", out var imagePoint))
        {
            var worldPoint = PlaneCalibrationMapper.MapImageToWorld(calibration, imagePoint);
            stopwatch.Stop();
            context.SetPortOutput(definition, "PointOutput", worldPoint);
            context.SetPortOutput(definition, "XOutput", worldPoint.X);
            context.SetPortOutput(definition, "YOutput", worldPoint.Y);
            AddPoint(data, "image", imagePoint);
            AddPoint(data, "world", worldPoint);
            return Task.FromResult(CreateSuccess(
                definition,
                stopwatch.Elapsed,
                $"Coordinate transform completed: X={worldPoint.X.ToInvariant()} Y={worldPoint.Y.ToInvariant()} {calibration.Unit}",
                data));
        }

        if (context.TryGetPortInput<Pose2D>(definition, "PositionInput", out var imagePose))
        {
            var worldPose = MapPose(calibration, imagePose);
            stopwatch.Stop();
            context.SetPortOutput(definition, "PositionOutput", worldPose);
            context.SetPortOutput(definition, "PointOutput", new Point2D(worldPose.X, worldPose.Y));
            context.SetPortOutput(definition, "XOutput", worldPose.X);
            context.SetPortOutput(definition, "YOutput", worldPose.Y);
            context.SetPortOutput(definition, "AngleOutput", worldPose.Angle);
            data["imageAngle"] = imagePose.Angle.ToInvariant();
            data["worldAngle"] = worldPose.Angle.ToInvariant();
            AddPoint(data, "image", new Point2D(imagePose.X, imagePose.Y));
            AddPoint(data, "world", new Point2D(worldPose.X, worldPose.Y));
            return Task.FromResult(CreateSuccess(
                definition,
                stopwatch.Elapsed,
                $"Coordinate pose transform completed: X={worldPose.X.ToInvariant()} Y={worldPose.Y.ToInvariant()} A={worldPose.Angle.ToInvariant()}",
                data));
        }

        stopwatch.Stop();
        return Task.FromResult(CreateFailure(
            definition,
            stopwatch.Elapsed,
            "Coordinate transform failed: connect PointInput or PositionInput."));
    }

    private static bool TryResolveCalibration(
        Recipe recipe,
        IReadOnlyDictionary<string, string> parameters,
        out PlaneCalibrationResult calibration,
        out string error)
    {
        if (TryGetParameterCalibration(parameters, out calibration))
        {
            error = string.Empty;
            return true;
        }

        if (recipe.Camera.PlaneCalibration is not null)
        {
            calibration = recipe.Camera.PlaneCalibration;
            error = string.Empty;
            return true;
        }

        error = "Coordinate transform failed: no plane calibration was configured.";
        calibration = default!;
        return false;
    }

    private static bool TryGetParameterCalibration(
        IReadOnlyDictionary<string, string> parameters,
        out PlaneCalibrationResult calibration)
    {
        calibration = default!;
        var matrixText = parameters.GetValueOrDefault("imageToWorldMatrix") ??
                         parameters.GetValueOrDefault("matrix") ??
                         parameters.GetValueOrDefault("affine") ??
                         parameters.GetValueOrDefault("homography");
        if (!CalibrationProfileText.TryParseMatrix(matrixText, out var imageToWorld))
        {
            return false;
        }

        var model = ResolveModel(parameters.GetValueOrDefault("model"), imageToWorld);
        var worldToImage = CalibrationProfileText.TryParseMatrix(parameters.GetValueOrDefault("worldToImageMatrix"), out var inverse)
            ? inverse
            : InvertMatrix(model, imageToWorld);

        calibration = new PlaneCalibrationResult
        {
            Model = model,
            Unit = parameters.GetValueOrDefault("unit") ?? "mm",
            ImageToWorldMatrix = imageToWorld,
            WorldToImageMatrix = worldToImage,
            PointCount = (int)Math.Round(parameters.GetDouble("pointCount", 0)),
            InlierCount = (int)Math.Round(parameters.GetDouble("inlierCount", 0)),
            RmsError = parameters.GetDouble("rmsError", 0),
            MaxError = parameters.GetDouble("maxError", 0)
        };
        return true;
    }

    private static PlaneCalibrationModel ResolveModel(string? text, IReadOnlyList<double> matrix)
    {
        if (Enum.TryParse<PlaneCalibrationModel>(text, true, out var model) &&
            model is PlaneCalibrationModel.Affine or PlaneCalibrationModel.Homography)
        {
            return model;
        }

        return matrix.Count == 9 ? PlaneCalibrationModel.Homography : PlaneCalibrationModel.Affine;
    }

    private static double[] InvertMatrix(PlaneCalibrationModel model, IReadOnlyList<double> matrix)
    {
        if (model == PlaneCalibrationModel.Affine)
        {
            var determinant = matrix[0] * matrix[4] - matrix[1] * matrix[3];
            if (Math.Abs(determinant) < 1e-12)
            {
                throw new InvalidOperationException("Affine calibration matrix is singular.");
            }

            return
            [
                matrix[4] / determinant,
                -matrix[1] / determinant,
                (matrix[1] * matrix[5] - matrix[4] * matrix[2]) / determinant,
                -matrix[3] / determinant,
                matrix[0] / determinant,
                (matrix[3] * matrix[2] - matrix[0] * matrix[5]) / determinant
            ];
        }

        return InvertHomography(matrix);
    }

    private static double[] InvertHomography(IReadOnlyList<double> matrix)
    {
        var a = matrix[0];
        var b = matrix[1];
        var c = matrix[2];
        var d = matrix[3];
        var e = matrix[4];
        var f = matrix[5];
        var g = matrix[6];
        var h = matrix[7];
        var i = matrix[8];
        var determinant = a * (e * i - f * h) -
                          b * (d * i - f * g) +
                          c * (d * h - e * g);
        if (Math.Abs(determinant) < 1e-12)
        {
            throw new InvalidOperationException("Homography calibration matrix is singular.");
        }

        return
        [
            (e * i - f * h) / determinant,
            (c * h - b * i) / determinant,
            (b * f - c * e) / determinant,
            (f * g - d * i) / determinant,
            (a * i - c * g) / determinant,
            (c * d - a * f) / determinant,
            (d * h - e * g) / determinant,
            (b * g - a * h) / determinant,
            (a * e - b * d) / determinant
        ];
    }

    private static Pose2D MapPose(PlaneCalibrationResult calibration, Pose2D imagePose)
    {
        var center = new Point2D(imagePose.X, imagePose.Y);
        var worldCenter = PlaneCalibrationMapper.MapImageToWorld(calibration, center);
        var radians = imagePose.Angle * Math.PI / 180.0;
        var imageDirection = new Point2D(imagePose.X + Math.Cos(radians), imagePose.Y + Math.Sin(radians));
        var worldDirection = PlaneCalibrationMapper.MapImageToWorld(calibration, imageDirection);
        var worldAngle = Math.Atan2(worldDirection.Y - worldCenter.Y, worldDirection.X - worldCenter.X) * 180.0 / Math.PI;
        return new Pose2D(worldCenter.X, worldCenter.Y, NormalizeAngle(worldAngle));
    }

    private static double NormalizeAngle(double angle)
    {
        while (angle > 180)
        {
            angle -= 360;
        }

        while (angle <= -180)
        {
            angle += 360;
        }

        return angle;
    }

    private static void AddPoint(Dictionary<string, string> data, string prefix, Point2D point)
    {
        data[$"{prefix}X"] = point.X.ToInvariant();
        data[$"{prefix}Y"] = point.Y.ToInvariant();
    }

    private static ToolResult CreateSuccess(
        VisionToolDefinition definition,
        TimeSpan duration,
        string message,
        Dictionary<string, string> data)
    {
        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = VisionToolKind.CoordinateTransform,
            Outcome = InspectionOutcome.Ok,
            Duration = duration,
            Message = message,
            Data = data
        };
    }

    private static ToolResult CreateFailure(
        VisionToolDefinition definition,
        TimeSpan duration,
        string message)
    {
        return new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = VisionToolKind.CoordinateTransform,
            Outcome = InspectionOutcome.Ng,
            Duration = duration,
            Message = message,
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }
}
