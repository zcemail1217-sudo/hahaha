using System.Globalization;
using OpenCvSharp;
using VisionStation.Domain;

namespace VisionStation.Vision;

public sealed class OpenCvCalibrationService
{
    private static readonly TermCriteria CornerCriteria = new(
        CriteriaTypes.Count | CriteriaTypes.Eps,
        30,
        0.001);

    private static readonly TermCriteria CalibrationCriteria = new(
        CriteriaTypes.Count | CriteriaTypes.Eps,
        100,
        1e-9);

    public ChessboardDetectionResult DetectChessboard(ImageFrame frame, ChessboardCalibrationPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidatePattern(pattern);

        using var gray = ImageFrameMatFactory.ToGrayMat(frame);
        var patternSize = new Size(pattern.Columns, pattern.Rows);
        var flags = ChessboardFlags.Exhaustive | ChessboardFlags.Accuracy | ChessboardFlags.NormalizeImage;
        var found = Cv2.FindChessboardCornersSB(gray, patternSize, out Point2f[] corners, flags);

        if (!found)
        {
            var classicFlags = ChessboardFlags.AdaptiveThresh |
                               ChessboardFlags.NormalizeImage |
                               ChessboardFlags.FilterQuads;
            found = Cv2.FindChessboardCorners(gray, patternSize, out corners, classicFlags);
            if (found)
            {
                Cv2.CornerSubPix(gray, corners, new Size(11, 11), new Size(-1, -1), CornerCriteria);
            }
        }

        return new ChessboardDetectionResult
        {
            FrameId = frame.Id,
            ImageWidth = frame.Width,
            ImageHeight = frame.Height,
            Found = found,
            Message = found
                ? $"Chessboard found: {corners.Length} corners"
                : $"Chessboard not found: expected {pattern.PointCount} corners",
            ImagePoints = found
                ? corners.Select(point => new Point2D(point.X, point.Y)).ToArray()
                : Array.Empty<Point2D>()
        };
    }

    public CameraCalibrationResult CalibrateCamera(
        IReadOnlyList<ChessboardDetectionResult> observations,
        ChessboardCalibrationPattern pattern,
        int minimumViews = 3,
        CalibrationFlags flags = CalibrationFlags.None)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ValidatePattern(pattern);

        var validObservations = observations
            .Where(observation => observation.Found && observation.ImagePoints.Count == pattern.PointCount)
            .ToArray();
        if (validObservations.Length < Math.Max(2, minimumViews))
        {
            throw new ArgumentException(
                $"At least {Math.Max(2, minimumViews)} valid chessboard views are required.",
                nameof(observations));
        }

        var imageWidth = validObservations[0].ImageWidth;
        var imageHeight = validObservations[0].ImageHeight;
        if (validObservations.Any(observation => observation.ImageWidth != imageWidth || observation.ImageHeight != imageHeight))
        {
            throw new ArgumentException("All calibration views must use the same image size.", nameof(observations));
        }

        var objectTemplate = CreateObjectPoints(pattern);
        var objectPoints = validObservations
            .Select(_ => objectTemplate)
            .ToArray();
        var imagePoints = validObservations
            .Select(observation => observation.ImagePoints.Select(ToPoint2f).ToArray())
            .ToArray();
        var cameraMatrix = new double[3, 3];
        var distCoeffs = new double[5];
        var rms = Cv2.CalibrateCamera(
            objectPoints,
            imagePoints,
            new Size(imageWidth, imageHeight),
            cameraMatrix,
            distCoeffs,
            out Vec3d[] rvecs,
            out Vec3d[] tvecs,
            flags,
            CalibrationCriteria);

        var views = new CameraCalibrationViewResult[validObservations.Length];
        for (var index = 0; index < validObservations.Length; index++)
        {
            views[index] = new CameraCalibrationViewResult
            {
                FrameId = validObservations[index].FrameId,
                RotationVector = ToArray(rvecs[index]),
                TranslationVector = ToArray(tvecs[index]),
                ReprojectionError = ComputeReprojectionError(
                    objectTemplate,
                    imagePoints[index],
                    rvecs[index],
                    tvecs[index],
                    cameraMatrix,
                    distCoeffs)
            };
        }

        return new CameraCalibrationResult
        {
            ImageWidth = imageWidth,
            ImageHeight = imageHeight,
            Pattern = pattern,
            CameraMatrix = Flatten(cameraMatrix),
            DistortionCoefficients = distCoeffs.ToArray(),
            RmsReprojectionError = rms,
            Views = views
        };
    }

    public CameraPoseEstimate EstimatePose(
        CameraCalibrationResult calibration,
        IReadOnlyList<Point2D> imagePoints,
        ChessboardCalibrationPattern? pattern = null)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(imagePoints);
        var effectivePattern = pattern ?? calibration.Pattern;
        ValidatePattern(effectivePattern);
        if (imagePoints.Count != effectivePattern.PointCount)
        {
            throw new ArgumentException(
                $"Expected {effectivePattern.PointCount} image points.",
                nameof(imagePoints));
        }

        var objectPoints = CreateObjectPoints(effectivePattern);
        var cameraMatrix = ToCameraMatrix(calibration.CameraMatrix);
        var distCoeffs = calibration.DistortionCoefficients;
        double[] rvec = [];
        double[] tvec = [];
        Cv2.SolvePnP(
            objectPoints,
            imagePoints.Select(ToPoint2f).ToArray(),
            cameraMatrix,
            distCoeffs,
            ref rvec,
            ref tvec,
            false,
            SolvePnPMethod.Iterative);

        if (rvec.Length != 3 || tvec.Length != 3)
        {
            throw new InvalidOperationException("OpenCV solvePnP failed to estimate camera pose.");
        }

        return new CameraPoseEstimate
        {
            RotationVector = rvec,
            TranslationVector = tvec,
            ReprojectionError = ComputeReprojectionError(
                objectPoints,
                imagePoints.Select(ToPoint2f).ToArray(),
                rvec,
                tvec,
                cameraMatrix,
                distCoeffs)
        };
    }

    public ImageFrame Undistort(ImageFrame frame, CameraCalibrationResult calibration)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(calibration);

        using var source = CreateSourceMat(frame);
        using var cameraMatrix = ToCameraMatrixMat(calibration.CameraMatrix);
        using var distCoeffs = Mat.FromArray(calibration.DistortionCoefficients);
        using var undistorted = new Mat();
        Cv2.Undistort(source, undistorted, cameraMatrix, distCoeffs);
        return CreateFrame(frame, undistorted, "undistort");
    }

    public PlaneCalibrationResult CalibratePlane(
        IReadOnlyList<CalibrationPointPair> pointPairs,
        PlaneCalibrationModel model = PlaneCalibrationModel.Affine,
        string unit = "mm",
        double ransacReprojectionThreshold = 1.0)
    {
        ArgumentNullException.ThrowIfNull(pointPairs);
        var pairs = pointPairs.ToArray();
        if (pairs.Length < 3)
        {
            throw new ArgumentException("At least three point pairs are required for plane calibration.", nameof(pointPairs));
        }

        if (model == PlaneCalibrationModel.Auto)
        {
            var affine = CreateAffineCalibration(pairs, unit, ransacReprojectionThreshold);
            if (pairs.Length < 4)
            {
                return affine;
            }

            var homography = CreateHomographyCalibration(pairs, unit, ransacReprojectionThreshold);
            return homography.RmsError < affine.RmsError * 0.75
                ? homography
                : affine;
        }

        return model == PlaneCalibrationModel.Homography
            ? CreateHomographyCalibration(pairs, unit, ransacReprojectionThreshold)
            : CreateAffineCalibration(pairs, unit, ransacReprojectionThreshold);
    }

    internal static Point3f[] CreateObjectPoints(ChessboardCalibrationPattern pattern)
    {
        ValidatePattern(pattern);
        var points = new Point3f[pattern.PointCount];
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

    private static PlaneCalibrationResult CreateAffineCalibration(
        IReadOnlyList<CalibrationPointPair> pairs,
        string unit,
        double ransacReprojectionThreshold)
    {
        using var from = InputArray.Create(pairs.Select(pair => ToPoint2f(pair.ImagePoint)).ToArray());
        using var to = InputArray.Create(pairs.Select(pair => ToPoint2f(pair.WorldPoint)).ToArray());
        using var inliers = new Mat();
        using var affine = Cv2.EstimateAffine2D(
            from,
            to,
            inliers,
            RobustEstimationAlgorithms.RANSAC,
            Math.Max(0.001, ransacReprojectionThreshold),
            2000,
            0.99,
            10);

        if (affine is null || affine.Empty())
        {
            throw new InvalidOperationException("OpenCV estimateAffine2D failed to estimate a plane calibration.");
        }

        using var inverse = new Mat();
        Cv2.InvertAffineTransform(affine, inverse);
        return CreatePlaneResult(
            PlaneCalibrationModel.Affine,
            unit,
            Flatten(affine, 2, 3),
            Flatten(inverse, 2, 3),
            pairs,
            CountInliers(inliers, pairs.Count));
    }

    private static PlaneCalibrationResult CreateHomographyCalibration(
        IReadOnlyList<CalibrationPointPair> pairs,
        string unit,
        double ransacReprojectionThreshold)
    {
        if (pairs.Count < 4)
        {
            throw new ArgumentException("At least four point pairs are required for homography calibration.", nameof(pairs));
        }

        using var inliers = new Mat();
        using var homography = Cv2.FindHomography(
            pairs.Select(pair => ToPoint2d(pair.ImagePoint)),
            pairs.Select(pair => ToPoint2d(pair.WorldPoint)),
            HomographyMethods.Ransac,
            Math.Max(0.001, ransacReprojectionThreshold),
            inliers,
            2000,
            0.99);

        if (homography.Empty())
        {
            throw new InvalidOperationException("OpenCV findHomography failed to estimate a plane calibration.");
        }

        using var inverse = homography.Inv();
        return CreatePlaneResult(
            PlaneCalibrationModel.Homography,
            unit,
            Flatten(homography, 3, 3),
            Flatten(inverse, 3, 3),
            pairs,
            CountInliers(inliers, pairs.Count));
    }

    private static PlaneCalibrationResult CreatePlaneResult(
        PlaneCalibrationModel model,
        string unit,
        double[] imageToWorld,
        double[] worldToImage,
        IReadOnlyList<CalibrationPointPair> pairs,
        int inlierCount)
    {
        var errors = pairs
            .Select(pair =>
            {
                var mapped = PlaneCalibrationMapper.Transform(model, imageToWorld, pair.ImagePoint);
                return new PlaneCalibrationPointError
                {
                    ImagePoint = pair.ImagePoint,
                    ExpectedWorldPoint = pair.WorldPoint,
                    MappedWorldPoint = mapped,
                    Error = Distance(mapped, pair.WorldPoint)
                };
            })
            .ToArray();
        var rms = Math.Sqrt(errors.Sum(error => error.Error * error.Error) / Math.Max(1, errors.Length));

        return new PlaneCalibrationResult
        {
            Model = model,
            Unit = string.IsNullOrWhiteSpace(unit) ? "mm" : unit,
            ImageToWorldMatrix = imageToWorld,
            WorldToImageMatrix = worldToImage,
            RmsError = rms,
            MaxError = errors.Length == 0 ? 0 : errors.Max(error => error.Error),
            PointCount = pairs.Count,
            InlierCount = inlierCount,
            PointErrors = errors
        };
    }

    private static double ComputeReprojectionError(
        IReadOnlyList<Point3f> objectPoints,
        IReadOnlyList<Point2f> imagePoints,
        Vec3d rvec,
        Vec3d tvec,
        double[,] cameraMatrix,
        double[] distCoeffs)
    {
        return ComputeReprojectionError(
            objectPoints,
            imagePoints,
            ToArray(rvec),
            ToArray(tvec),
            cameraMatrix,
            distCoeffs);
    }

    private static double ComputeReprojectionError(
        IReadOnlyList<Point3f> objectPoints,
        IReadOnlyList<Point2f> imagePoints,
        double[] rvec,
        double[] tvec,
        double[,] cameraMatrix,
        double[] distCoeffs)
    {
        Cv2.ProjectPoints(
            objectPoints,
            rvec,
            tvec,
            cameraMatrix,
            distCoeffs,
            out Point2f[] projected,
            out _);
        var sumSquared = 0.0;
        for (var index = 0; index < imagePoints.Count; index++)
        {
            var dx = projected[index].X - imagePoints[index].X;
            var dy = projected[index].Y - imagePoints[index].Y;
            sumSquared += dx * dx + dy * dy;
        }

        return Math.Sqrt(sumSquared / Math.Max(1, imagePoints.Count));
    }

    private static void ValidatePattern(ChessboardCalibrationPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.Columns < 2 || pattern.Rows < 2)
        {
            throw new ArgumentException("Chessboard pattern must contain at least 2x2 inner corners.", nameof(pattern));
        }

        if (pattern.SquareSize <= 0)
        {
            throw new ArgumentException("Chessboard square size must be greater than zero.", nameof(pattern));
        }
    }

    private static Mat CreateSourceMat(ImageFrame frame)
    {
        var sourceType = frame.Format switch
        {
            PixelFormatKind.Gray8 => MatType.CV_8UC1,
            PixelFormatKind.Bgr24 => MatType.CV_8UC3,
            _ => MatType.CV_8UC4
        };

        using var view = Mat.FromPixelData(frame.Height, frame.Width, sourceType, frame.Pixels, frame.Stride);
        return view.Clone();
    }

    private static ImageFrame CreateFrame(ImageFrame input, Mat output, string operation)
    {
        using var normalized = NormalizeOutputMat(output);
        var format = normalized.Channels() switch
        {
            1 => PixelFormatKind.Gray8,
            3 => PixelFormatKind.Bgr24,
            4 => PixelFormatKind.Bgra32,
            _ => PixelFormatKind.Gray8
        };
        var stride = (int)normalized.Step();
        normalized.GetArray(out byte[] pixels);
        return new ImageFrame(
            Guid.NewGuid().ToString("N"),
            normalized.Width,
            normalized.Height,
            stride,
            format,
            pixels,
            DateTimeOffset.Now,
            $"{input.Source}|calibration:{operation}");
    }

    private static Mat NormalizeOutputMat(Mat source)
    {
        if (source.Channels() is 1 or 3 or 4 && source.IsContinuous())
        {
            return source.Clone();
        }

        if (source.Channels() is 1 or 3 or 4)
        {
            return source.Clone();
        }

        var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static Point2f ToPoint2f(Point2D point)
    {
        return new Point2f((float)point.X, (float)point.Y);
    }

    private static Point2d ToPoint2d(Point2D point)
    {
        return new Point2d(point.X, point.Y);
    }

    private static double[] ToArray(Vec3d vector)
    {
        return [vector.Item0, vector.Item1, vector.Item2];
    }

    private static double[] Flatten(double[,] matrix)
    {
        return
        [
            matrix[0, 0], matrix[0, 1], matrix[0, 2],
            matrix[1, 0], matrix[1, 1], matrix[1, 2],
            matrix[2, 0], matrix[2, 1], matrix[2, 2]
        ];
    }

    private static double[] Flatten(Mat matrix, int rows, int columns)
    {
        using var converted = new Mat();
        matrix.ConvertTo(converted, MatType.CV_64F);
        var values = new double[rows * columns];
        var index = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                values[index++] = converted.At<double>(row, column);
            }
        }

        return values;
    }

    private static double[,] ToCameraMatrix(double[] values)
    {
        if (values.Length != 9)
        {
            throw new ArgumentException("Camera matrix must contain nine values.", nameof(values));
        }

        return new[,]
        {
            { values[0], values[1], values[2] },
            { values[3], values[4], values[5] },
            { values[6], values[7], values[8] }
        };
    }

    private static Mat ToCameraMatrixMat(double[] values)
    {
        return Mat.FromArray(ToCameraMatrix(values));
    }

    private static int CountInliers(Mat inliers, int fallback)
    {
        if (inliers.Empty())
        {
            return fallback;
        }

        inliers.GetArray(out byte[] values);
        return values.Count(value => value != 0);
    }

    private static double Distance(Point2D first, Point2D second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

public static class PlaneCalibrationMapper
{
    public static Point2D MapImageToWorld(PlaneCalibrationResult calibration, Point2D imagePoint)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        return Transform(ResolveModel(calibration.Model, calibration.ImageToWorldMatrix), calibration.ImageToWorldMatrix, imagePoint);
    }

    public static Point2D MapWorldToImage(PlaneCalibrationResult calibration, Point2D worldPoint)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        return Transform(ResolveModel(calibration.Model, calibration.WorldToImageMatrix), calibration.WorldToImageMatrix, worldPoint);
    }

    internal static Point2D Transform(PlaneCalibrationModel model, IReadOnlyList<double> matrix, Point2D point)
    {
        if (model == PlaneCalibrationModel.Affine)
        {
            if (matrix.Count != 6)
            {
                throw new ArgumentException("Affine matrix must contain six values.", nameof(matrix));
            }

            return new Point2D(
                matrix[0] * point.X + matrix[1] * point.Y + matrix[2],
                matrix[3] * point.X + matrix[4] * point.Y + matrix[5]);
        }

        if (matrix.Count != 9)
        {
            throw new ArgumentException("Homography matrix must contain nine values.", nameof(matrix));
        }

        var denominator = matrix[6] * point.X + matrix[7] * point.Y + matrix[8];
        if (Math.Abs(denominator) < 1e-12)
        {
            throw new InvalidOperationException("Homography transform produced a point at infinity.");
        }

        return new Point2D(
            (matrix[0] * point.X + matrix[1] * point.Y + matrix[2]) / denominator,
            (matrix[3] * point.X + matrix[4] * point.Y + matrix[5]) / denominator);
    }

    private static PlaneCalibrationModel ResolveModel(PlaneCalibrationModel model, IReadOnlyList<double> matrix)
    {
        if (model == PlaneCalibrationModel.Auto)
        {
            return matrix.Count == 6 ? PlaneCalibrationModel.Affine : PlaneCalibrationModel.Homography;
        }

        return model;
    }
}

public static class CalibrationProfileText
{
    public static string FormatMatrix(IReadOnlyList<double> values)
    {
        return string.Join(",", values.Select(value => value.ToString("G17", CultureInfo.InvariantCulture)));
    }

    public static bool TryParseMatrix(string? text, out double[] values)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            values = Array.Empty<double>();
            return false;
        }

        var parsed = new List<double>();
        foreach (var segment in text.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(segment, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
                !double.TryParse(segment, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                values = Array.Empty<double>();
                return false;
            }

            parsed.Add(value);
        }

        values = parsed.ToArray();
        return values.Length is 6 or 9;
    }
}
