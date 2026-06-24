using System.Text.Json.Serialization;

namespace VisionStation.Domain;

public enum PlaneCalibrationModel
{
    Auto,
    Affine,
    Homography
}

public sealed record ChessboardCalibrationPattern
{
    public int Columns { get; init; } = 9;

    public int Rows { get; init; } = 6;

    public double SquareSize { get; init; } = 1.0;

    public string Unit { get; init; } = "mm";

    [JsonIgnore]
    public int PointCount => Columns * Rows;
}

public sealed record ChessboardDetectionResult
{
    public string FrameId { get; init; } = string.Empty;

    public int ImageWidth { get; init; }

    public int ImageHeight { get; init; }

    public bool Found { get; init; }

    public string Message { get; init; } = string.Empty;

    public IReadOnlyList<Point2D> ImagePoints { get; init; } = Array.Empty<Point2D>();
}

public sealed record CameraCalibrationViewResult
{
    public string FrameId { get; init; } = string.Empty;

    public double[] RotationVector { get; init; } = Array.Empty<double>();

    public double[] TranslationVector { get; init; } = Array.Empty<double>();

    public double ReprojectionError { get; init; }
}

public sealed record CameraCalibrationResult
{
    public int ImageWidth { get; init; }

    public int ImageHeight { get; init; }

    public ChessboardCalibrationPattern Pattern { get; init; } = new();

    public double[] CameraMatrix { get; init; } = Array.Empty<double>();

    public double[] DistortionCoefficients { get; init; } = Array.Empty<double>();

    public double RmsReprojectionError { get; init; }

    public IReadOnlyList<CameraCalibrationViewResult> Views { get; init; } = Array.Empty<CameraCalibrationViewResult>();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record CameraPoseEstimate
{
    public double[] RotationVector { get; init; } = Array.Empty<double>();

    public double[] TranslationVector { get; init; } = Array.Empty<double>();

    public double ReprojectionError { get; init; }
}

public sealed record CalibrationPointPair(Point2D ImagePoint, Point2D WorldPoint);

public sealed record PlaneCalibrationPointError
{
    public Point2D ImagePoint { get; init; } = new(0, 0);

    public Point2D ExpectedWorldPoint { get; init; } = new(0, 0);

    public Point2D MappedWorldPoint { get; init; } = new(0, 0);

    public double Error { get; init; }
}

public sealed record PlaneCalibrationResult
{
    public PlaneCalibrationModel Model { get; init; } = PlaneCalibrationModel.Affine;

    public string Unit { get; init; } = "mm";

    public double[] ImageToWorldMatrix { get; init; } = Array.Empty<double>();

    public double[] WorldToImageMatrix { get; init; } = Array.Empty<double>();

    public double RmsError { get; init; }

    public double MaxError { get; init; }

    public int PointCount { get; init; }

    public int InlierCount { get; init; }

    public IReadOnlyList<PlaneCalibrationPointError> PointErrors { get; init; } = Array.Empty<PlaneCalibrationPointError>();

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}
