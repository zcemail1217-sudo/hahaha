using System.Globalization;
using OpenCvSharp;
using VisionStation.Domain;

namespace VisionStation.Vision;

public sealed record TemplateSearchRegion(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

public sealed record TemplateMatchResult(
    bool HasMatch,
    InspectionOutcome Outcome,
    double Score,
    Pose2D Pose,
    int MatchX,
    int MatchY,
    int TemplateWidth,
    int TemplateHeight,
    TemplateSearchRegion SearchRegion,
    string Message,
    bool UsedAutoTemplate,
    IReadOnlyList<Point2D>? ShapePoints = null,
    IReadOnlyList<IReadOnlyList<Point2D>>? ShapeContours = null);

public static class TemplateMatcher
{
    private const string TemplateVersion = "1";
    private const int DefaultSearchStep = 5;
    private const int MinimumTemplateSize = 12;

    public static Dictionary<string, string> Learn(
        ImageFrame frame,
        RoiDefinition? searchRoi,
        IReadOnlyDictionary<string, string> parameters)
    {
        return ShouldUseOpenCv(parameters)
            ? OpenCvTemplateMatcher.Learn(frame, searchRoi, parameters)
            : LearnManaged(frame, searchRoi, parameters);
    }

    public static TemplateMatchResult Match(
        ImageFrame frame,
        RoiDefinition? searchRoi,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        return ShouldUseOpenCv(parameters)
            ? OpenCvTemplateMatcher.Match(frame, searchRoi, parameters, cancellationToken)
            : MatchManaged(frame, searchRoi, parameters, cancellationToken);
    }

    internal static TemplateMatchResult Match(
        ImageFrame frame,
        RoiDefinition? searchRoi,
        IReadOnlyDictionary<string, string> parameters,
        Mat gray,
        CancellationToken cancellationToken = default)
    {
        return ShouldUseOpenCv(parameters)
            ? OpenCvTemplateMatcher.Match(frame, searchRoi, parameters, gray, cancellationToken)
            : MatchManaged(frame, searchRoi, parameters, cancellationToken);
    }

    internal static Dictionary<string, string> LearnManaged(
        ImageFrame frame,
        RoiDefinition? searchRoi,
        IReadOnlyDictionary<string, string> parameters)
    {
        var gray = ToGrayBuffer(frame);
        var searchRegion = GetSearchRegion(frame, searchRoi);
        var templateRegion = GetTemplateRegion(frame, searchRegion, parameters);
        var pixels = new byte[templateRegion.Width * templateRegion.Height];

        for (var y = 0; y < templateRegion.Height; y++)
        {
            var sourceOffset = (templateRegion.Y + y) * frame.Width + templateRegion.X;
            var targetOffset = y * templateRegion.Width;
            Buffer.BlockCopy(gray, sourceOffset, pixels, targetOffset, templateRegion.Width);
        }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["templateVersion"] = TemplateVersion,
            ["templateX"] = templateRegion.X.ToString(CultureInfo.InvariantCulture),
            ["templateY"] = templateRegion.Y.ToString(CultureInfo.InvariantCulture),
            ["templateWidth"] = templateRegion.Width.ToString(CultureInfo.InvariantCulture),
            ["templateHeight"] = templateRegion.Height.ToString(CultureInfo.InvariantCulture),
            ["templateFrameWidth"] = frame.Width.ToString(CultureInfo.InvariantCulture),
            ["templateFrameHeight"] = frame.Height.ToString(CultureInfo.InvariantCulture),
            ["templatePixels"] = Convert.ToBase64String(pixels),
            ["templateSourceRoiId"] = searchRoi?.Id ?? string.Empty,
            ["searchStep"] = GetInt(parameters, "searchStep", DefaultSearchStep).ToString(CultureInfo.InvariantCulture)
        };
    }

    internal static TemplateMatchResult MatchManaged(
        ImageFrame frame,
        RoiDefinition? searchRoi,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        var usedAutoTemplate = false;
        var runtimeParameters = parameters;
        if (!TryReadTemplate(parameters, out var templatePixels, out var templateWidth, out var templateHeight))
        {
            if (!GetBool(parameters, "autoLearnTemplate", false))
            {
                return CreateFailedResult(frame, "Template has not been learned.", false);
            }

            var learned = Learn(frame, searchRoi, parameters);
            var merged = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in learned)
            {
                merged[parameter.Key] = parameter.Value;
            }

            runtimeParameters = merged;
            usedAutoTemplate = true;
            if (!TryReadTemplate(runtimeParameters, out templatePixels, out templateWidth, out templateHeight))
            {
                return CreateFailedResult(frame, "Template model is not available.", usedAutoTemplate);
            }
        }

        var searchRegion = GetSearchRegion(frame, searchRoi);
        if (searchRegion.Width < templateWidth || searchRegion.Height < templateHeight)
        {
            return CreateFailedResult(
                frame,
                "Search region is smaller than the learned template.",
                usedAutoTemplate,
                searchRegion,
                templateWidth,
                templateHeight);
        }

        var gray = ToGrayBuffer(frame);
        var sampleStep = GetSampleStep(templateWidth, templateHeight, runtimeParameters);
        var samples = CreateSamples(templatePixels, templateWidth, templateHeight, sampleStep);
        if (samples.Count == 0)
        {
            return CreateFailedResult(frame, "Template has no usable samples.", usedAutoTemplate, searchRegion, templateWidth, templateHeight);
        }

        var templateMean = samples.Average(sample => sample.Value);
        var templateEnergy = samples.Sum(sample =>
        {
            var delta = sample.Value - templateMean;
            return delta * delta;
        });

        if (templateEnergy < 1)
        {
            return CreateFailedResult(frame, "Template contrast is too low.", usedAutoTemplate, searchRegion, templateWidth, templateHeight);
        }

        var searchStep = Math.Clamp(GetInt(runtimeParameters, "searchStep", DefaultSearchStep), 1, 32);
        var best = FindBest(gray, frame.Width, searchRegion, templateWidth, templateHeight, samples, templateMean, templateEnergy, searchStep, cancellationToken);
        best = RefineBest(gray, frame.Width, searchRegion, templateWidth, templateHeight, samples, templateMean, templateEnergy, best, searchStep, cancellationToken);

        var minScore = GetDouble(runtimeParameters, "minScore", 0.85);
        var pose = new Pose2D(best.X + templateWidth / 2.0, best.Y + templateHeight / 2.0, 0);
        var outcome = best.Score >= minScore ? InspectionOutcome.Ok : InspectionOutcome.Ng;
        var message = outcome == InspectionOutcome.Ok
            ? $"Template locate OK, score {best.Score.ToString("0.000", CultureInfo.InvariantCulture)}"
            : $"Template locate NG, score {best.Score.ToString("0.000", CultureInfo.InvariantCulture)}";

        if (usedAutoTemplate)
        {
            message += " (auto template)";
        }

        return new TemplateMatchResult(
            true,
            outcome,
            best.Score,
            pose,
            best.X,
            best.Y,
            templateWidth,
            templateHeight,
            searchRegion,
            message,
            usedAutoTemplate);
    }

    public static TemplateSearchRegion GetSearchRegion(ImageFrame frame, RoiDefinition? roi)
    {
        if (roi is null)
        {
            return new TemplateSearchRegion(0, 0, frame.Width, frame.Height);
        }

        var bounds = roi.Shape switch
        {
            RoiShapeKind.Circle => (X: roi.X - roi.Radius, Y: roi.Y - roi.Radius, Width: roi.Radius * 2, Height: roi.Radius * 2),
            RoiShapeKind.Polygon when roi.Points.Count > 0 => (
                X: roi.Points.Min(point => point.X),
                Y: roi.Points.Min(point => point.Y),
                Width: roi.Points.Max(point => point.X) - roi.Points.Min(point => point.X),
                Height: roi.Points.Max(point => point.Y) - roi.Points.Min(point => point.Y)),
            RoiShapeKind.RotatedRectangle => GetRotatedRectangleBounds(roi),
            _ => (roi.X, roi.Y, roi.Width, roi.Height)
        };

        return ClampRegion(frame, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static TemplateSearchRegion GetTemplateRegion(
        ImageFrame frame,
        TemplateSearchRegion searchRegion,
        IReadOnlyDictionary<string, string> parameters)
    {
        if (TryGetTemplateRoi(parameters, out var x, out var y, out var width, out var height) &&
            width >= MinimumTemplateSize &&
            height >= MinimumTemplateSize)
        {
            return ClampRegion(frame, x, y, width, height);
        }

        var templateWidth = Math.Clamp((int)Math.Round(searchRegion.Width * 0.22), 96, 280);
        var templateHeight = Math.Clamp((int)Math.Round(searchRegion.Height * 0.16), 72, 180);
        templateWidth = Math.Min(templateWidth, Math.Max(MinimumTemplateSize, searchRegion.Width));
        templateHeight = Math.Min(templateHeight, Math.Max(MinimumTemplateSize, searchRegion.Height));

        var centerX = searchRegion.X + searchRegion.Width / 2;
        var centerY = searchRegion.Y + searchRegion.Height / 2;
        var left = centerX - templateWidth / 2;
        var top = centerY - templateHeight / 2;

        left = Math.Clamp(left, searchRegion.X, Math.Max(searchRegion.X, searchRegion.Right - templateWidth));
        top = Math.Clamp(top, searchRegion.Y, Math.Max(searchRegion.Y, searchRegion.Bottom - templateHeight));
        return ClampRegion(frame, left, top, templateWidth, templateHeight);
    }

    private static bool TryGetTemplateRoi(
        IReadOnlyDictionary<string, string> parameters,
        out double x,
        out double y,
        out double width,
        out double height)
    {
        x = 0;
        y = 0;
        width = 0;
        height = 0;

        if (TryGetDouble(parameters, "templateRoiX", out x) &&
            TryGetDouble(parameters, "templateRoiY", out y) &&
            TryGetDouble(parameters, "templateRoiWidth", out width) &&
            TryGetDouble(parameters, "templateRoiHeight", out height))
        {
            return true;
        }

        return TryGetDouble(parameters, "templateX", out x) &&
               TryGetDouble(parameters, "templateY", out y) &&
               TryGetDouble(parameters, "templateWidth", out width) &&
               TryGetDouble(parameters, "templateHeight", out height);
    }

    private static (double X, double Y, double Width, double Height) GetRotatedRectangleBounds(RoiDefinition roi)
    {
        var radians = roi.Angle * Math.PI / 180.0;
        var width = Math.Abs(roi.Width * Math.Cos(radians)) + Math.Abs(roi.Height * Math.Sin(radians));
        var height = Math.Abs(roi.Width * Math.Sin(radians)) + Math.Abs(roi.Height * Math.Cos(radians));
        return (roi.X - width / 2.0, roi.Y - height / 2.0, width, height);
    }

    private static TemplateSearchRegion ClampRegion(ImageFrame frame, double x, double y, double width, double height)
    {
        var left = Math.Clamp((int)Math.Floor(x), 0, Math.Max(0, frame.Width - 1));
        var top = Math.Clamp((int)Math.Floor(y), 0, Math.Max(0, frame.Height - 1));
        var right = Math.Clamp((int)Math.Ceiling(x + width), left + 1, frame.Width);
        var bottom = Math.Clamp((int)Math.Ceiling(y + height), top + 1, frame.Height);

        return new TemplateSearchRegion(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static bool TryReadTemplate(
        IReadOnlyDictionary<string, string> parameters,
        out byte[] pixels,
        out int width,
        out int height)
    {
        pixels = Array.Empty<byte>();
        width = 0;
        height = 0;

        if (!TryGetInt(parameters, "templateWidth", out width) ||
            !TryGetInt(parameters, "templateHeight", out height) ||
            width < MinimumTemplateSize ||
            height < MinimumTemplateSize)
        {
            return false;
        }

        // Try file-based model first
        if (parameters.TryGetValue("modelPath", out var modelPath) &&
            !string.IsNullOrWhiteSpace(modelPath) &&
            File.Exists(modelPath))
        {
            try
            {
                var fileBytes = File.ReadAllBytes(modelPath);
                if (fileBytes.Length == width * height)
                {
                    pixels = fileBytes;
                    return true;
                }
            }
            catch
            {
                // Fall through to Base64
            }
        }

        // Backward-compatible Base64 template pixels
        if (parameters.TryGetValue("templatePixels", out var encoded) &&
            !string.IsNullOrWhiteSpace(encoded))
        {
            try
            {
                pixels = Convert.FromBase64String(encoded);
                return pixels.Length == width * height;
            }
            catch (FormatException)
            {
                pixels = Array.Empty<byte>();
                width = 0;
                height = 0;
                return false;
            }
        }

        return false;
    }

    private static List<TemplateSample> CreateSamples(byte[] templatePixels, int width, int height, int sampleStep)
    {
        var samples = new List<TemplateSample>((width / sampleStep + 1) * (height / sampleStep + 1));
        for (var y = 0; y < height; y += sampleStep)
        {
            for (var x = 0; x < width; x += sampleStep)
            {
                samples.Add(new TemplateSample(x, y, templatePixels[y * width + x]));
            }
        }

        return samples;
    }

    private static MatchCandidate FindBest(
        byte[] gray,
        int frameWidth,
        TemplateSearchRegion searchRegion,
        int templateWidth,
        int templateHeight,
        IReadOnlyList<TemplateSample> samples,
        double templateMean,
        double templateEnergy,
        int searchStep,
        CancellationToken cancellationToken)
    {
        var best = new MatchCandidate(searchRegion.X, searchRegion.Y, double.NegativeInfinity);
        var maxX = searchRegion.Right - templateWidth;
        var maxY = searchRegion.Bottom - templateHeight;

        for (var y = searchRegion.Y; y <= maxY; y += searchStep)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = searchRegion.X; x <= maxX; x += searchStep)
            {
                var score = ScoreCandidate(gray, frameWidth, x, y, samples, templateMean, templateEnergy);
                if (score > best.Score)
                {
                    best = new MatchCandidate(x, y, score);
                }
            }
        }

        return best;
    }

    private static MatchCandidate RefineBest(
        byte[] gray,
        int frameWidth,
        TemplateSearchRegion searchRegion,
        int templateWidth,
        int templateHeight,
        IReadOnlyList<TemplateSample> samples,
        double templateMean,
        double templateEnergy,
        MatchCandidate best,
        int searchStep,
        CancellationToken cancellationToken)
    {
        if (searchStep <= 1)
        {
            return best;
        }

        var maxX = searchRegion.Right - templateWidth;
        var maxY = searchRegion.Bottom - templateHeight;
        var startX = Math.Max(searchRegion.X, best.X - searchStep);
        var endX = Math.Min(maxX, best.X + searchStep);
        var startY = Math.Max(searchRegion.Y, best.Y - searchStep);
        var endY = Math.Min(maxY, best.Y + searchStep);

        for (var y = startY; y <= endY; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = startX; x <= endX; x++)
            {
                var score = ScoreCandidate(gray, frameWidth, x, y, samples, templateMean, templateEnergy);
                if (score > best.Score)
                {
                    best = new MatchCandidate(x, y, score);
                }
            }
        }

        return best;
    }

    private static double ScoreCandidate(
        byte[] gray,
        int frameWidth,
        int x,
        int y,
        IReadOnlyList<TemplateSample> samples,
        double templateMean,
        double templateEnergy)
    {
        var patchSum = 0.0;
        foreach (var sample in samples)
        {
            patchSum += gray[(y + sample.Y) * frameWidth + x + sample.X];
        }

        var patchMean = patchSum / samples.Count;
        var dot = 0.0;
        var patchEnergy = 0.0;
        foreach (var sample in samples)
        {
            var templateDelta = sample.Value - templateMean;
            var patchDelta = gray[(y + sample.Y) * frameWidth + x + sample.X] - patchMean;
            dot += templateDelta * patchDelta;
            patchEnergy += patchDelta * patchDelta;
        }

        return patchEnergy <= 0 ? -1 : dot / Math.Sqrt(templateEnergy * patchEnergy);
    }

    private static byte[] ToGrayBuffer(ImageFrame frame)
    {
        var gray = new byte[frame.Width * frame.Height];
        for (var y = 0; y < frame.Height; y++)
        {
            var targetOffset = y * frame.Width;
            var sourceOffset = y * frame.Stride;
            switch (frame.Format)
            {
                case PixelFormatKind.Gray8:
                    Buffer.BlockCopy(frame.Pixels, sourceOffset, gray, targetOffset, Math.Min(frame.Width, frame.Pixels.Length - sourceOffset));
                    break;
                case PixelFormatKind.Bgr24:
                    for (var x = 0; x < frame.Width; x++)
                    {
                        var offset = sourceOffset + x * 3;
                        if (offset + 2 >= frame.Pixels.Length)
                        {
                            break;
                        }

                        gray[targetOffset + x] = ToGray(frame.Pixels[offset], frame.Pixels[offset + 1], frame.Pixels[offset + 2]);
                    }

                    break;
                default:
                    for (var x = 0; x < frame.Width; x++)
                    {
                        var offset = sourceOffset + x * 4;
                        if (offset + 2 >= frame.Pixels.Length)
                        {
                            break;
                        }

                        gray[targetOffset + x] = ToGray(frame.Pixels[offset], frame.Pixels[offset + 1], frame.Pixels[offset + 2]);
                    }

                    break;
            }
        }

        return gray;
    }

    private static byte ToGray(byte b, byte g, byte r)
    {
        return (byte)Math.Clamp((int)(r * 0.299 + g * 0.587 + b * 0.114), 0, 255);
    }

    private static TemplateMatchResult CreateFailedResult(
        ImageFrame frame,
        string message,
        bool usedAutoTemplate,
        TemplateSearchRegion? searchRegion = null,
        int templateWidth = 0,
        int templateHeight = 0)
    {
        return new TemplateMatchResult(
            false,
            InspectionOutcome.Ng,
            0,
            new Pose2D(frame.Width / 2.0, frame.Height / 2.0, 0),
            0,
            0,
            templateWidth,
            templateHeight,
            searchRegion ?? new TemplateSearchRegion(0, 0, frame.Width, frame.Height),
            message,
            usedAutoTemplate);
    }

    private static int GetSampleStep(int width, int height, IReadOnlyDictionary<string, string> parameters)
    {
        if (TryGetInt(parameters, "templateSampleStep", out var configuredStep))
        {
            return Math.Clamp(configuredStep, 1, 16);
        }

        return Math.Clamp(Math.Min(width, height) / 28, 1, 6);
    }

    private static bool TryGetInt(IReadOnlyDictionary<string, string> parameters, string key, out int value)
    {
        value = 0;
        if (!parameters.TryGetValue(key, out var raw))
        {
            return false;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            value = Math.Max(0, (int)Math.Round(doubleValue));
            return true;
        }

        return false;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> parameters, string key, int fallback)
    {
        return TryGetInt(parameters, key, out var value) ? value : fallback;
    }

    private static double GetDouble(IReadOnlyDictionary<string, string> parameters, string key, double fallback)
    {
        return parameters.TryGetValue(key, out var raw) &&
               double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, string> parameters, string key, out double value)
    {
        value = 0;
        return parameters.TryGetValue(key, out var raw) &&
               double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> parameters, string key, bool fallback)
    {
        return parameters.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value)
            ? value
            : fallback;
    }

    private static bool ShouldUseOpenCv(IReadOnlyDictionary<string, string> parameters)
    {
        var engine = parameters.TryGetValue("engine", out var value) ? value : "OpenCv";
        return !engine.Equals("ManagedNcc", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record TemplateSample(int X, int Y, byte Value);

    private sealed record MatchCandidate(int X, int Y, double Score);
}
