using System.Diagnostics;
using OpenCvSharp;
using VisionStation.Domain;

namespace VisionStation.Vision.Tools;

public sealed class ImageProcessTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.ImageProcess;

    public Task<ToolResult> ExecuteAsync(VisionToolDefinition definition, VisionToolContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!context.TryGetInputImage(definition, out var frame))
        {
            stopwatch.Stop();
            return Task.FromResult(GeometryToolSupport.CreateMissingImageInputResult(definition, Kind, stopwatch.Elapsed));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var operation = NormalizeOperation(definition.Parameters.GetValueOrDefault("operation"));
        using var processed = Process(frame, context, operation, definition.Parameters);
        var outputFrame = CreateFrame(frame, processed, definition.Id, operation);
        context.SetImageOutput(definition, outputFrame);
        stopwatch.Stop();

        return Task.FromResult(new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = Kind,
            Outcome = InspectionOutcome.Ok,
            Duration = stopwatch.Elapsed,
            Message = $"Image process {operation} completed {outputFrame.Width}x{outputFrame.Height}",
            Data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["operation"] = operation,
                ["inputFrameId"] = frame.Id,
                ["outputFrameId"] = outputFrame.Id,
                ["outputWidth"] = outputFrame.Width.ToString(),
                ["outputHeight"] = outputFrame.Height.ToString(),
                ["outputFormat"] = outputFrame.Format.ToString()
            }
        });
    }

    private static Mat Process(
        ImageFrame frame,
        VisionToolContext context,
        string operation,
        IReadOnlyDictionary<string, string> parameters)
    {
        return operation switch
        {
            "Grayscale" => context.GetGrayMat(frame).Clone(),
            "Threshold" => ProcessThreshold(frame, context, parameters),
            "Filter" => ProcessFilter(frame, parameters),
            "Morphology" => ProcessMorphology(frame, context, parameters),
            "Enhance" => ProcessEnhance(frame, context, parameters),
            "Geometry" => ProcessGeometry(frame, parameters),
            _ => context.GetGrayMat(frame).Clone()
        };
    }

    private static Mat ProcessThreshold(
        ImageFrame frame,
        VisionToolContext context,
        IReadOnlyDictionary<string, string> parameters)
    {
        var gray = context.GetGrayMat(frame);
        var binary = new Mat();
        var mode = NormalizeThresholdMode(parameters.GetValueOrDefault("thresholdMode"));
        var darkTarget = IsDarkTarget(parameters.GetValueOrDefault("polarity"));
        var thresholdType = darkTarget ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;

        switch (mode)
        {
            case "Range":
                var lower = NormalizeThreshold(parameters.GetDouble("grayMin", parameters.GetDouble("grayLower", 0)));
                var upper = NormalizeThreshold(parameters.GetDouble("grayMax", parameters.GetDouble("grayUpper", 255)));
                if (upper < lower)
                {
                    (lower, upper) = (upper, lower);
                }

                Cv2.InRange(gray, new Scalar(lower), new Scalar(upper), binary);
                break;
            case "Adaptive":
                var blockSize = NormalizeOddKernel(parameters.GetDouble("adaptiveBlockSize", 41), 3, 501);
                var constant = parameters.GetDouble("adaptiveC", 5);
                Cv2.AdaptiveThreshold(gray, binary, 255, AdaptiveThresholdTypes.GaussianC, thresholdType, blockSize, constant);
                break;
            case "Triangle":
                Cv2.Threshold(gray, binary, 0, 255, thresholdType | ThresholdTypes.Triangle);
                break;
            case "Otsu":
                Cv2.Threshold(gray, binary, 0, 255, thresholdType | ThresholdTypes.Otsu);
                break;
            default:
                var threshold = NormalizeThreshold(parameters.GetDouble("threshold", 128));
                Cv2.Threshold(gray, binary, threshold, 255, thresholdType);
                break;
        }

        return binary;
    }

    private static Mat ProcessFilter(ImageFrame frame, IReadOnlyDictionary<string, string> parameters)
    {
        using var source = CreateSourceMat(frame);
        var filterType = NormalizeFilterType(parameters.GetValueOrDefault("filterType"));
        var kernelSize = NormalizeOddKernel(parameters.GetDouble("kernelSize", 3), 1, 99);
        var result = new Mat();

        switch (filterType)
        {
            case "Mean":
                Cv2.Blur(source, result, new Size(kernelSize, kernelSize));
                break;
            case "Median":
                Cv2.MedianBlur(source, result, Math.Max(3, kernelSize));
                break;
            case "Bilateral":
                using (var filterSource = EnsureBilateralSource(source))
                {
                    var sigmaColor = Math.Max(1, parameters.GetDouble("sigmaColor", 45));
                    var sigmaSpace = Math.Max(1, parameters.GetDouble("sigmaSpace", 45));
                    Cv2.BilateralFilter(filterSource, result, Math.Max(3, kernelSize), sigmaColor, sigmaSpace);
                }

                break;
            case "Sharpen":
                using (var kernel = Mat.FromArray(new float[,]
                       {
                           { 0, -1, 0 },
                           { -1, 5, -1 },
                           { 0, -1, 0 }
                       }))
                {
                    Cv2.Filter2D(source, result, source.Depth(), kernel);
                }

                break;
            default:
                Cv2.GaussianBlur(source, result, new Size(Math.Max(3, kernelSize), Math.Max(3, kernelSize)), 0);
                break;
        }

        return result;
    }

    private static Mat ProcessMorphology(
        ImageFrame frame,
        VisionToolContext context,
        IReadOnlyDictionary<string, string> parameters)
    {
        var gray = context.GetGrayMat(frame);
        var morphType = NormalizeMorphType(parameters.GetValueOrDefault("morphType"));
        var kernelSize = NormalizeOddKernel(parameters.GetDouble("kernelSize", parameters.GetDouble("morphSize", 3)), 1, 99);
        var iterations = Math.Clamp((int)Math.Round(parameters.GetDouble("iterations", 1)), 1, 32);
        var shape = NormalizeMorphShape(parameters.GetValueOrDefault("kernelShape"));
        using var kernel = Cv2.GetStructuringElement(shape, new Size(kernelSize, kernelSize));
        var result = new Mat();

        switch (morphType)
        {
            case "Erode":
                Cv2.Erode(gray, result, kernel, iterations: iterations);
                break;
            case "Dilate":
                Cv2.Dilate(gray, result, kernel, iterations: iterations);
                break;
            case "Close":
                Cv2.MorphologyEx(gray, result, MorphTypes.Close, kernel, iterations: iterations);
                break;
            case "Gradient":
                Cv2.MorphologyEx(gray, result, MorphTypes.Gradient, kernel, iterations: iterations);
                break;
            case "TopHat":
                Cv2.MorphologyEx(gray, result, MorphTypes.TopHat, kernel, iterations: iterations);
                break;
            case "BlackHat":
                Cv2.MorphologyEx(gray, result, MorphTypes.BlackHat, kernel, iterations: iterations);
                break;
            default:
                Cv2.MorphologyEx(gray, result, MorphTypes.Open, kernel, iterations: iterations);
                break;
        }

        return result;
    }

    private static Mat ProcessEnhance(
        ImageFrame frame,
        VisionToolContext context,
        IReadOnlyDictionary<string, string> parameters)
    {
        var enhanceType = NormalizeEnhanceType(parameters.GetValueOrDefault("enhanceType"));
        if (enhanceType is "Equalize" or "Clahe")
        {
            var gray = context.GetGrayMat(frame);
            var result = new Mat();
            if (enhanceType == "Clahe")
            {
                using var clahe = Cv2.CreateCLAHE(
                    Math.Max(0.1, parameters.GetDouble("clipLimit", 2.0)),
                    new Size(
                        Math.Clamp((int)Math.Round(parameters.GetDouble("tileGridWidth", 8)), 1, 64),
                        Math.Clamp((int)Math.Round(parameters.GetDouble("tileGridHeight", 8)), 1, 64)));
                clahe.Apply(gray, result);
            }
            else
            {
                Cv2.EqualizeHist(gray, result);
            }

            return result;
        }

        using var source = CreateSourceMat(frame);
        var enhanced = new Mat();
        switch (enhanceType)
        {
            case "Gamma":
                ApplyGamma(source, enhanced, Math.Max(0.05, parameters.GetDouble("gamma", 1)));
                break;
            case "Invert":
                Cv2.BitwiseNot(source, enhanced);
                break;
            default:
                var alpha = Math.Max(0, parameters.GetDouble("contrast", parameters.GetDouble("alpha", 1)));
                var beta = parameters.GetDouble("brightness", parameters.GetDouble("beta", 0));
                source.ConvertTo(enhanced, source.Type(), alpha, beta);
                break;
        }

        return enhanced;
    }

    private static Mat ProcessGeometry(ImageFrame frame, IReadOnlyDictionary<string, string> parameters)
    {
        using var source = CreateSourceMat(frame);
        var geometryType = NormalizeGeometryType(parameters.GetValueOrDefault("geometryType"));
        var result = new Mat();

        switch (geometryType)
        {
            case "Rotate":
                Rotate(source, result, parameters.GetDouble("angle", 0), GetBool(parameters, "keepBounds", true));
                break;
            case "Resize":
                Resize(source, result, parameters);
                break;
            default:
                Cv2.Flip(source, result, GetFlipMode(parameters.GetValueOrDefault("flipMode")));
                break;
        }

        return result;
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

    private static Mat EnsureBilateralSource(Mat source)
    {
        if (source.Channels() != 4)
        {
            return source.Clone();
        }

        var bgr = new Mat();
        Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
        return bgr;
    }

    private static ImageFrame CreateFrame(ImageFrame input, Mat processed, string toolId, string operation)
    {
        using var normalized = NormalizeOutputMat(processed);
        var channels = normalized.Channels();
        var format = channels switch
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
            $"{input.Source}|{toolId}:{operation}");
    }

    private static Mat NormalizeOutputMat(Mat source)
    {
        var normalized = source;
        if (source.Channels() is not (1 or 3 or 4))
        {
            normalized = new Mat();
            Cv2.CvtColor(source, normalized, ColorConversionCodes.BGR2GRAY);
        }
        else if (!source.IsContinuous())
        {
            normalized = source.Clone();
        }

        return ReferenceEquals(normalized, source) ? source.Clone() : normalized;
    }

    private static void ApplyGamma(Mat source, Mat destination, double gamma)
    {
        using var lookup = new Mat(1, 256, MatType.CV_8UC1);
        for (var index = 0; index < 256; index++)
        {
            var value = Math.Pow(index / 255.0, 1.0 / gamma) * 255.0;
            lookup.Set(0, index, (byte)Math.Clamp((int)Math.Round(value), 0, 255));
        }

        Cv2.LUT(source, lookup, destination);
    }

    private static void Rotate(Mat source, Mat destination, double angle, bool keepBounds)
    {
        if (Math.Abs(angle) < 0.001)
        {
            source.CopyTo(destination);
            return;
        }

        var center = new Point2f((source.Width - 1) / 2.0f, (source.Height - 1) / 2.0f);
        using var matrix = Cv2.GetRotationMatrix2D(center, angle, 1.0);
        var size = source.Size();
        if (keepBounds)
        {
            var radians = angle * Math.PI / 180.0;
            var sin = Math.Abs(Math.Sin(radians));
            var cos = Math.Abs(Math.Cos(radians));
            var width = Math.Max(1, (int)Math.Ceiling(source.Width * cos + source.Height * sin));
            var height = Math.Max(1, (int)Math.Ceiling(source.Width * sin + source.Height * cos));
            matrix.Set(0, 2, matrix.At<double>(0, 2) + width / 2.0 - center.X);
            matrix.Set(1, 2, matrix.At<double>(1, 2) + height / 2.0 - center.Y);
            size = new Size(width, height);
        }

        Cv2.WarpAffine(source, destination, matrix, size, InterpolationFlags.Linear, BorderTypes.Constant, Scalar.Black);
    }

    private static void Resize(Mat source, Mat destination, IReadOnlyDictionary<string, string> parameters)
    {
        var scale = Math.Max(0.01, parameters.GetDouble("scale", 1));
        var width = Math.Max(1, (int)Math.Round(parameters.GetDouble("width", source.Width * scale)));
        var height = Math.Max(1, (int)Math.Round(parameters.GetDouble("height", source.Height * scale)));
        Cv2.Resize(source, destination, new Size(width, height), 0, 0, GetInterpolation(parameters.GetValueOrDefault("interpolation")));
    }

    private static string NormalizeOperation(string? value)
    {
        return value?.Trim() switch
        {
            "Gray" or "Grey" or "Grayscale" or "ToGray" => "Grayscale",
            "Binary" or "Binarize" or "Threshold" => "Threshold",
            "Blur" or "Filter" or "Denoise" => "Filter",
            "Morph" or "Morphology" => "Morphology",
            "Brightness" or "Contrast" or "Enhance" => "Enhance",
            "Transform" or "Geometry" => "Geometry",
            _ => "Threshold"
        };
    }

    private static string NormalizeThresholdMode(string? value)
    {
        return value?.Trim() switch
        {
            "Range" or "GrayRange" => "Range",
            "Adaptive" => "Adaptive",
            "Triangle" => "Triangle",
            "Otsu" => "Otsu",
            _ => "Fixed"
        };
    }

    private static string NormalizeFilterType(string? value)
    {
        return value?.Trim() switch
        {
            "Mean" or "Average" or "Box" => "Mean",
            "Median" => "Median",
            "Bilateral" => "Bilateral",
            "Sharpen" or "HighPass" => "Sharpen",
            _ => "Gaussian"
        };
    }

    private static string NormalizeMorphType(string? value)
    {
        return value?.Trim() switch
        {
            "Erode" => "Erode",
            "Dilate" => "Dilate",
            "Close" => "Close",
            "Gradient" => "Gradient",
            "TopHat" or "WhiteTopHat" => "TopHat",
            "BlackHat" => "BlackHat",
            _ => "Open"
        };
    }

    private static MorphShapes NormalizeMorphShape(string? value)
    {
        return value?.Trim() switch
        {
            "Ellipse" or "Circle" => MorphShapes.Ellipse,
            "Cross" => MorphShapes.Cross,
            _ => MorphShapes.Rect
        };
    }

    private static string NormalizeEnhanceType(string? value)
    {
        return value?.Trim() switch
        {
            "Gamma" => "Gamma",
            "Equalize" or "HistogramEqualization" => "Equalize",
            "CLAHE" or "Clahe" => "Clahe",
            "Invert" or "Negative" => "Invert",
            _ => "BrightnessContrast"
        };
    }

    private static string NormalizeGeometryType(string? value)
    {
        return value?.Trim() switch
        {
            "Rotate" => "Rotate",
            "Resize" or "Scale" => "Resize",
            _ => "Flip"
        };
    }

    private static bool IsDarkTarget(string? value)
    {
        return value?.Trim() switch
        {
            "Bright" or "Light" => false,
            _ => true
        };
    }

    private static double NormalizeThreshold(double threshold)
    {
        return Math.Clamp(threshold <= 1 ? threshold * 255.0 : threshold, 0, 255);
    }

    private static int NormalizeOddKernel(double value, int min, int max)
    {
        var kernel = Math.Clamp((int)Math.Round(value), min, max);
        return kernel % 2 == 0 ? Math.Min(max, kernel + 1) : kernel;
    }

    private static FlipMode GetFlipMode(string? value)
    {
        return value?.Trim() switch
        {
            "Vertical" or "Y" => FlipMode.X,
            "Both" or "XY" => FlipMode.XY,
            _ => FlipMode.Y
        };
    }

    private static InterpolationFlags GetInterpolation(string? value)
    {
        return value?.Trim() switch
        {
            "Nearest" => InterpolationFlags.Nearest,
            "Cubic" => InterpolationFlags.Cubic,
            "Lanczos" => InterpolationFlags.Lanczos4,
            _ => InterpolationFlags.Linear
        };
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> parameters, string key, bool fallback)
    {
        return parameters.TryGetValue(key, out var raw) && bool.TryParse(raw, out var value)
            ? value
            : fallback;
    }
}
