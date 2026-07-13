using System.Diagnostics;
using System.Globalization;
using OpenCvSharp;
using VisionStation.Domain;
using Xunit;
using Xunit.Abstractions;

namespace VisionStation.Vision.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalTemplateDatasetCollection
{
    public const string Name = "Local Template Dataset";
}

[Collection(LocalTemplateDatasetCollection.Name)]
public sealed class LocalTemplateDatasetAcceptanceTests
{
    private readonly ITestOutputHelper _output;

    public LocalTemplateDatasetAcceptanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Category", "LocalDataset")]
    public void ShapeV2_meets_local_six_image_quality_and_performance_gates()
    {
        var directory = Environment.GetEnvironmentVariable("VISIONSTATION_TEMPLATE_DATASET");
        if (string.IsNullOrWhiteSpace(directory))
        {
            _output.WriteLine("SKIP: VISIONSTATION_TEMPLATE_DATASET is not configured.");
            return;
        }

        if (!Directory.Exists(directory))
        {
            _output.WriteLine(
                $"SKIP: VISIONSTATION_TEMPLATE_DATASET directory does not exist: {directory}");
            return;
        }

        var positivePath = Path.Combine(directory, "正.bmp");
        Assert.True(File.Exists(positivePath), $"Local dataset must contain '{positivePath}'.");

        var report = LocalTemplateDatasetFixture.Run(directory, _output);
        var positive = Assert.Single(
            report.Results,
            item => Path.GetFileName(item.Path).Equals("正.bmp", StringComparison.OrdinalIgnoreCase));

        Assert.True(positive.V2.HasMatch, positive.V2.Message);
        Assert.Equal(InspectionOutcome.Ok, positive.V2.Outcome);
        Assert.NotNull(positive.V2.ShapeCoverage);
        Assert.True(
            positive.V2.ShapeCoverage.Value >= 0.90,
            $"Positive coverage {positive.V2.ShapeCoverage.Value:0.0000} is below 0.9000.");
        Assert.NotNull(positive.V2.ShapeReverseScore);
        Assert.True(double.IsFinite(positive.V2.Score), "Positive V2 score must be finite.");
        Assert.True(double.IsFinite(positive.V2.ShapeCoverage.Value), "Positive V2 coverage must be finite.");
        Assert.True(double.IsFinite(positive.V2.ShapeReverseScore.Value), "Positive V2 reverse score must be finite.");
        Assert.True(double.IsFinite(positive.V2.Pose.X), "Positive V2 pose X must be finite.");
        Assert.True(double.IsFinite(positive.V2.Pose.Y), "Positive V2 pose Y must be finite.");
        Assert.True(double.IsFinite(positive.V2.Pose.Angle), "Positive V2 pose angle must be finite.");
        Assert.True(report.V1Elapsed > TimeSpan.Zero, "V1 measured elapsed time must be positive.");
        Assert.True(
            report.V2Elapsed.TotalMilliseconds <= report.V1Elapsed.TotalMilliseconds * 1.30,
            $"V2 total {report.V2Elapsed.TotalMilliseconds:0.0} ms exceeds " +
            $"1.30 x V1 total {report.V1Elapsed.TotalMilliseconds:0.0} ms.");
    }
}

internal sealed record LocalTemplateDatasetImageResult(
    string Path,
    TemplateMatchResult V1,
    TemplateMatchResult V2,
    TimeSpan V1Elapsed,
    TimeSpan V2Elapsed);

internal sealed record LocalTemplateDatasetReport(
    IReadOnlyList<LocalTemplateDatasetImageResult> Results,
    TimeSpan V1Elapsed,
    TimeSpan V2Elapsed);

internal sealed record LocalTemplateDatasetLearned(
    ImageFrame PositiveFrame,
    Dictionary<string, string> Parameters,
    Rect ProductBounds,
    Rect TemplateBounds);

internal static class LocalTemplateDatasetFixture
{
    private const int ProductMargin = 14;

    public static LocalTemplateDatasetReport Run(string directory, ITestOutputHelper output)
    {
        var positivePath = Path.Combine(directory, "正.bmp");
        var learned = LearnFromPositive(positivePath);
        var v2Parameters = learned.Parameters;
        var v1Parameters = new Dictionary<string, string>(v2Parameters, StringComparer.OrdinalIgnoreCase);
        v1Parameters.Remove("shapeScoreVersion");

        var frames = Directory
            .EnumerateFiles(directory, "*.bmp", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => (Path: path, Frame: LoadFrame(path)))
            .ToArray();
        if (frames.Length == 0)
        {
            throw new InvalidOperationException($"No BMP files were found in local dataset '{directory}'.");
        }

        _ = TemplateMatcher.Match(learned.PositiveFrame, null, v1Parameters);
        _ = TemplateMatcher.Match(learned.PositiveFrame, null, v2Parameters);

        var results = new List<LocalTemplateDatasetImageResult>(frames.Length);
        var v1Total = TimeSpan.Zero;
        var v2Total = TimeSpan.Zero;
        for (var index = 0; index < frames.Length; index++)
        {
            var item = frames[index];
            TemplateMatchResult v1;
            TemplateMatchResult v2;
            TimeSpan v1Elapsed;
            TimeSpan v2Elapsed;

            if (index % 2 == 0)
            {
                (v1, v1Elapsed) = Measure(() => TemplateMatcher.Match(item.Frame, null, v1Parameters));
                (v2, v2Elapsed) = Measure(() => TemplateMatcher.Match(item.Frame, null, v2Parameters));
            }
            else
            {
                (v2, v2Elapsed) = Measure(() => TemplateMatcher.Match(item.Frame, null, v2Parameters));
                (v1, v1Elapsed) = Measure(() => TemplateMatcher.Match(item.Frame, null, v1Parameters));
            }

            v1Total += v1Elapsed;
            v2Total += v2Elapsed;
            results.Add(new LocalTemplateDatasetImageResult(item.Path, v1, v2, v1Elapsed, v2Elapsed));
            output.WriteLine(FormatImageResult(item.Path, v1, v2, v1Elapsed, v2Elapsed));
        }

        var ratio = v1Total > TimeSpan.Zero
            ? v2Total.TotalMilliseconds / v1Total.TotalMilliseconds
            : double.PositiveInfinity;
        output.WriteLine(FormattableString.Invariant(
            $"TOTAL V1={v1Total.TotalMilliseconds:0.0} ms V2={v2Total.TotalMilliseconds:0.0} ms ratio={ratio:0.000}"));

        var positive = results.Single(item =>
            Path.GetFileName(item.Path).Equals("正.bmp", StringComparison.OrdinalIgnoreCase));
        WritePositiveDiagnostics(output, positive.V2, learned);

        return new LocalTemplateDatasetReport(results, v1Total, v2Total);
    }

    private static LocalTemplateDatasetLearned LearnFromPositive(string path)
    {
        var positiveFrame = LoadFrame(path);
        using var gray = Cv2.ImRead(path, ImreadModes.Grayscale);
        if (gray.Empty())
        {
            throw new InvalidOperationException($"Unable to load positive image '{path}'.");
        }

        var productBounds = FindProductBounds(gray, path);
        var templateBounds = Expand(productBounds, ProductMargin, gray.Size());
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["matchMode"] = "Shape",
            ["minScore"] = "0.85",
            ["angleStart"] = "-180",
            ["angleExtent"] = "360",
            ["angleStep"] = "2",
            ["autoContrast"] = "true",
            ["shapeScoreVersion"] = "2",
            ["shapeCoverageDistance"] = "3",
            ["templateRoiX"] = templateBounds.X.ToString(CultureInfo.InvariantCulture),
            ["templateRoiY"] = templateBounds.Y.ToString(CultureInfo.InvariantCulture),
            ["templateRoiWidth"] = templateBounds.Width.ToString(CultureInfo.InvariantCulture),
            ["templateRoiHeight"] = templateBounds.Height.ToString(CultureInfo.InvariantCulture)
        };

        foreach (var item in TemplateMatcher.Learn(positiveFrame, null, parameters))
        {
            parameters[item.Key] = item.Value;
        }

        return new LocalTemplateDatasetLearned(positiveFrame, parameters, productBounds, templateBounds);
    }

    private static ImageFrame LoadFrame(string path)
    {
        using var gray = Cv2.ImRead(path, ImreadModes.Grayscale);
        if (gray.Empty())
        {
            throw new InvalidOperationException($"Unable to load local dataset image '{path}'.");
        }

        gray.GetArray(out byte[] pixels);
        return new ImageFrame(
            Guid.NewGuid().ToString("N"),
            gray.Width,
            gray.Height,
            checked((int)gray.Step()),
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UtcNow,
            path);
    }

    private static Rect FindProductBounds(Mat gray, string path)
    {
        using var binary = new Mat();
        Cv2.Threshold(gray, binary, 100, 255, ThresholdTypes.BinaryInv);
        using var contourSource = binary.Clone();
        Cv2.FindContours(
            contourSource,
            out Point[][] contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        var candidates = contours
            .Select(contour => new ContourCandidate(Cv2.BoundingRect(contour), Cv2.ContourArea(contour)))
            .Where(item => item.Area is >= 55_000 and <= 70_000)
            .Where(item => Math.Max(item.Bounds.Width, item.Bounds.Height) is >= 900 and <= 1_050)
            .OrderBy(item => CenterDistanceSquared(item.Bounds, gray.Size()))
            .ToArray();
        if (candidates.Length > 0)
        {
            return candidates[0].Bounds;
        }

        var contourSummary = string.Join(
            "; ",
            contours
                .Select(contour => new ContourCandidate(Cv2.BoundingRect(contour), Cv2.ContourArea(contour)))
                .OrderBy(item => CenterDistanceSquared(item.Bounds, gray.Size()))
                .Take(8)
                .Select(item => FormattableString.Invariant(
                    $"area={item.Area:0} bounds=({item.Bounds.X},{item.Bounds.Y},{item.Bounds.Width},{item.Bounds.Height})")));
        throw new InvalidOperationException(
            $"No complete product contour was found in '{path}'. " +
            $"Expected area 55000..70000 and long side 900..1050 after BinaryInv threshold 100. " +
            $"Contours={contours.Length}; nearest candidates: {contourSummary}");
    }

    private static Rect Expand(Rect value, int margin, Size imageSize)
    {
        var left = Math.Max(0, value.X - margin);
        var top = Math.Max(0, value.Y - margin);
        var right = Math.Min(imageSize.Width, value.Right + margin);
        var bottom = Math.Min(imageSize.Height, value.Bottom + margin);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static double CenterDistanceSquared(Rect bounds, Size imageSize)
    {
        var dx = bounds.X + bounds.Width / 2.0 - imageSize.Width / 2.0;
        var dy = bounds.Y + bounds.Height / 2.0 - imageSize.Height / 2.0;
        return dx * dx + dy * dy;
    }

    private static (TemplateMatchResult Result, TimeSpan Elapsed) Measure(
        Func<TemplateMatchResult> match)
    {
        var watch = Stopwatch.StartNew();
        var result = match();
        watch.Stop();
        return (result, watch.Elapsed);
    }

    private static string FormatImageResult(
        string path,
        TemplateMatchResult v1,
        TemplateMatchResult v2,
        TimeSpan v1Elapsed,
        TimeSpan v2Elapsed)
    {
        return FormattableString.Invariant(
            $"{Path.GetFileName(path)} V1 score={v1.Score:0.0000} V2 score={v2.Score:0.0000} coverage={FormatNullable(v2.ShapeCoverage)} reverse={FormatNullable(v2.ShapeReverseScore)} pose=({v2.Pose.X:0.0},{v2.Pose.Y:0.0},{v2.Pose.Angle:0.0}) V1={v1Elapsed.TotalMilliseconds:0.0} ms V2={v2Elapsed.TotalMilliseconds:0.0} ms");
    }

    private static string FormatNullable(double? value)
    {
        return value.HasValue
            ? value.Value.ToString("0.0000", CultureInfo.InvariantCulture)
            : "null";
    }

    private static void WritePositiveDiagnostics(
        ITestOutputHelper output,
        TemplateMatchResult positive,
        LocalTemplateDatasetLearned learned)
    {
        var expectedX = learned.ProductBounds.X + learned.ProductBounds.Width / 2.0;
        var expectedY = learned.ProductBounds.Y + learned.ProductBounds.Height / 2.0;
        var centerError = Math.Sqrt(
            Math.Pow(positive.Pose.X - expectedX, 2) +
            Math.Pow(positive.Pose.Y - expectedY, 2));
        var angleModulo180 = Math.Abs(positive.Pose.Angle % 180.0);
        var angleError = Math.Min(angleModulo180, 180.0 - angleModulo180);
        var contourRangeRatio = GetContourRangeRatio(positive, learned.TemplateBounds);
        output.WriteLine(FormattableString.Invariant(
            $"POSITIVE diagnostics centerError={centerError:0.00}px angleErrorMod180={angleError:0.00}deg shapeToTemplateRange={FormatNullable(contourRangeRatio)}"));
    }

    private static double? GetContourRangeRatio(
        TemplateMatchResult result,
        Rect templateBounds)
    {
        var points = result.ShapeContours?
            .SelectMany(contour => contour)
            .ToArray();
        if (points is not { Length: > 0 } || templateBounds.Width <= 0 || templateBounds.Height <= 0)
        {
            return null;
        }

        var contourWidth = points.Max(point => point.X) - points.Min(point => point.X);
        var contourHeight = points.Max(point => point.Y) - points.Min(point => point.Y);
        return Math.Max(contourWidth / templateBounds.Width, contourHeight / templateBounds.Height);
    }

    private sealed record ContourCandidate(Rect Bounds, double Area);
}
