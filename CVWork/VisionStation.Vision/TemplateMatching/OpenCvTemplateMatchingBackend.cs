using System.Globalization;
using System.Text.Json;
using VisionStation.Domain;

namespace VisionStation.Vision;

internal sealed class OpenCvTemplateMatchingBackend : ITemplateMatchingBackend
{
    public TemplateMatchingEngine Engine => TemplateMatchingEngine.OpenCv;

    public Task<TemplateLearningResult> LearnAsync(
        TemplateLearningRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runtimeParameters = LegacyTemplateMatchingAdapterSupport.WithTemplateRoi(request);
        var learned = OpenCvTemplateMatcher.Learn(
            request.Frame,
            request.SearchRoi,
            runtimeParameters);
        var parameters = LegacyTemplateMatchingAdapterSupport.MergeLearningParameters(
            runtimeParameters,
            learned,
            runtimeParameters["templateSourceRoiId"],
            Engine);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TemplateLearningResult(
            Engine,
            true,
            parameters,
            "OpenCV template learned.",
            null)
        {
            Geometry = TemplateReferencePoseCodec.ReadActive(parameters)
        });
    }

    public Task<TemplateMatchBatchResult> MatchAsync(
        TemplateMatchingRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = request.Cardinality switch
        {
            TemplateMatchCardinality.Single => FromSingle(
                OpenCvTemplateMatcher.MatchWithEffectiveParameters(
                    request.Frame,
                    request.SearchRoi,
                    request.Parameters,
                    cancellationToken)),
            TemplateMatchCardinality.ExactCount => FromMulti(
                request,
                MultiTargetMatcher.MatchOpenCv(
                    request.Frame,
                    request.SearchRoi,
                    request.Parameters,
                    request.ExpectedCount + 1,
                    cancellationToken)),
            _ => throw new TemplateMatchingConfigurationException(
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                    $"Unsupported template match cardinality '{request.Cardinality}'."))
        };
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private static TemplateMatchBatchResult FromSingle(
        OpenCvTemplateMatchExecution execution)
    {
        var source = execution.Result;
        var diagnostic = LegacyTemplateMatchingAdapterSupport.ReadDiagnostic(
            source.FailureCode,
            source.FailureStage,
            source.Message,
            source.TechnicalDetails);
        if (!source.HasMatch)
        {
            return new TemplateMatchBatchResult(
                TemplateMatchingEngine.OpenCv,
                source.Outcome,
                false,
                Array.Empty<TemplateMatchBatchCandidate>(),
                source.SearchRegion,
                source.Message,
                source.UsedAutoTemplate,
                diagnostic);
        }

        if (!LegacyTemplateMatchingAdapterSupport.TryReadTemplateDimensions(
                execution.EffectiveParameters,
                out var templateWidth,
                out var templateHeight))
        {
            return LegacyTemplateMatchingAdapterSupport.ContractFailure(
                TemplateMatchingEngine.OpenCv,
                source.SearchRegion,
                source.UsedAutoTemplate,
                "OpenCV single-target result has no learned reference template dimensions.");
        }

        IReadOnlyList<IReadOnlyList<Point2D>> templateRoiContours;
        if (source.MatchedTemplateRoiContours is { Count: > 0 })
        {
            templateRoiContours = source.MatchedTemplateRoiContours;
        }
        else
        {
            return LegacyTemplateMatchingAdapterSupport.ContractFailure(
                TemplateMatchingEngine.OpenCv,
                source.SearchRegion,
                source.UsedAutoTemplate,
                "OpenCV single-target matcher returned no transformed template ROI contour.");
        }

        var candidate = new TemplateMatchBatchCandidate(
            source.Pose,
            source.Score,
            templateWidth,
            templateHeight,
            source.ShapeContours ?? Array.Empty<IReadOnlyList<Point2D>>(),
            templateRoiContours)
        {
            ShapeCoverage = source.ShapeCoverage,
            ShapeReverseScore = source.ShapeReverseScore
        };
        if (!LegacyTemplateMatchingAdapterSupport.TryValidateCandidate(candidate, out var failure))
        {
            return LegacyTemplateMatchingAdapterSupport.ContractFailure(
                TemplateMatchingEngine.OpenCv,
                source.SearchRegion,
                source.UsedAutoTemplate,
                failure);
        }

        return new TemplateMatchBatchResult(
            TemplateMatchingEngine.OpenCv,
            source.Outcome,
            source.HasMatch,
            [candidate],
            source.SearchRegion,
            source.Message,
            source.UsedAutoTemplate,
            diagnostic);
    }

    private static TemplateMatchBatchResult FromMulti(
        TemplateMatchingRequest request,
        MultiTargetMatchResult source)
    {
        var candidates = new List<TemplateMatchBatchCandidate>(source.Matches.Count);
        foreach (var match in source.Matches)
        {
            if (!LegacyTemplateMatchingAdapterSupport.TryCreateTemplateRoiContours(
                    request.Parameters,
                    match.Pose,
                    out var templateRoiContours,
                    out var templateWidth,
                    out var templateHeight,
                    out var contourFailure))
            {
                return LegacyTemplateMatchingAdapterSupport.ContractFailure(
                    TemplateMatchingEngine.OpenCv,
                    source.SearchRegion,
                    source.UsedAutoTemplate,
                    contourFailure);
            }

            var candidate = new TemplateMatchBatchCandidate(
                match.Pose,
                match.Score,
                templateWidth,
                templateHeight,
                Array.Empty<IReadOnlyList<Point2D>>(),
                templateRoiContours)
            {
                Shape = match.Shape,
                Radius = match.Radius
            };
            if (!LegacyTemplateMatchingAdapterSupport.TryValidateCandidate(candidate, out var failure))
            {
                return LegacyTemplateMatchingAdapterSupport.ContractFailure(
                    TemplateMatchingEngine.OpenCv,
                    source.SearchRegion,
                    source.UsedAutoTemplate,
                    failure);
            }

            candidates.Add(candidate);
        }

        var diagnostic = LegacyTemplateMatchingAdapterSupport.ReadDiagnostic(
            source.FailureCode,
            source.FailureStage,
            source.Message,
            source.TechnicalDetails);
        var outcome = ResolveExactCountOutcome(
            source.Outcome,
            candidates.Count,
            request.ExpectedCount);
        var message = source.Outcome == InspectionOutcome.Ok && outcome == InspectionOutcome.Ng
            ? $"Template exact-count NG, found {candidates.Count}, required {request.ExpectedCount}."
            : source.Message;
        return new TemplateMatchBatchResult(
            TemplateMatchingEngine.OpenCv,
            outcome,
            candidates.Count > 0,
            candidates,
            source.SearchRegion,
            message,
            source.UsedAutoTemplate,
            diagnostic);
    }

    internal static InspectionOutcome ResolveExactCountOutcome(
        InspectionOutcome sourceOutcome,
        int actualCount,
        int expectedCount)
    {
        return sourceOutcome == InspectionOutcome.Ok && actualCount == expectedCount
            ? InspectionOutcome.Ok
            : InspectionOutcome.Ng;
    }
}

internal static class LegacyTemplateMatchingAdapterSupport
{
    public static Dictionary<string, string> WithTemplateRoi(TemplateLearningRequest request)
    {
        if (!TryValidateTemplateRoi(request.TemplateRoi, out var validationFailure))
        {
            throw new TemplateMatchingConfigurationException(
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                    validationFailure));
        }

        var normalizedRoi = NormalizeTemplateRoi(request.TemplateRoi);
        var parameters = new Dictionary<string, string>(request.Parameters, StringComparer.OrdinalIgnoreCase);
        var bounds = GetBounds(normalizedRoi);
        if (!double.IsFinite(bounds.X) ||
            !double.IsFinite(bounds.Y) ||
            !IsPositiveFinite(bounds.Width) ||
            !IsPositiveFinite(bounds.Height))
        {
            throw new TemplateMatchingConfigurationException(
                TemplateMatchingDiagnostics.Create(
                    TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                    "The explicit template ROI has invalid bounds."));
        }

        parameters["templateRoiJson"] = JsonSerializer.Serialize(normalizedRoi);
        parameters["templateRoiShape"] = normalizedRoi.Shape.ToString();
        parameters["templateRoiX"] = bounds.X.ToString("R", CultureInfo.InvariantCulture);
        parameters["templateRoiY"] = bounds.Y.ToString("R", CultureInfo.InvariantCulture);
        parameters["templateRoiWidth"] = bounds.Width.ToString("R", CultureInfo.InvariantCulture);
        parameters["templateRoiHeight"] = bounds.Height.ToString("R", CultureInfo.InvariantCulture);
        parameters["templateRoiAngle"] = normalizedRoi.Angle.ToString("R", CultureInfo.InvariantCulture);
        parameters["templateSourceRoiId"] = normalizedRoi.Id;
        return parameters;
    }

    private static bool TryValidateTemplateRoi(RoiDefinition roi, out string technicalDetails)
    {
        switch (roi.Shape)
        {
            case RoiShapeKind.Rectangle:
                if (double.IsFinite(roi.X) &&
                    double.IsFinite(roi.Y) &&
                    IsPositiveFinite(roi.Width) &&
                    IsPositiveFinite(roi.Height))
                {
                    technicalDetails = string.Empty;
                    return true;
                }

                technicalDetails =
                    "Rectangle template ROI requires finite X/Y and positive finite Width/Height.";
                return false;

            case RoiShapeKind.Circle:
                if (double.IsFinite(roi.X) &&
                    double.IsFinite(roi.Y) &&
                    IsPositiveFinite(roi.Radius))
                {
                    technicalDetails = string.Empty;
                    return true;
                }

                technicalDetails =
                    "Circle template ROI requires a finite center and a positive finite Radius.";
                return false;

            case RoiShapeKind.RotatedRectangle:
                if (double.IsFinite(roi.X) &&
                    double.IsFinite(roi.Y) &&
                    double.IsFinite(roi.Angle) &&
                    IsPositiveFinite(roi.Width) &&
                    IsPositiveFinite(roi.Height))
                {
                    technicalDetails = string.Empty;
                    return true;
                }

                technicalDetails =
                    "RotatedRectangle template ROI requires a finite center/Angle and positive finite Width/Height.";
                return false;

            case RoiShapeKind.Polygon:
                if (roi.Points.Count < 3)
                {
                    technicalDetails = "Polygon template ROI requires at least three points.";
                    return false;
                }

                if (roi.Points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                {
                    technicalDetails = "Polygon template ROI points must all be finite.";
                    return false;
                }

                var twiceArea = 0d;
                for (var index = 0; index < roi.Points.Count; index++)
                {
                    var current = roi.Points[index];
                    var next = roi.Points[(index + 1) % roi.Points.Count];
                    twiceArea += current.X * next.Y - next.X * current.Y;
                }

                if (!double.IsFinite(twiceArea) || Math.Abs(twiceArea) <= double.Epsilon)
                {
                    technicalDetails = "Polygon template ROI must enclose a non-zero finite area.";
                    return false;
                }

                technicalDetails = string.Empty;
                return true;

            default:
                technicalDetails = $"Template ROI shape '{roi.Shape}' is unsupported.";
                return false;
        }
    }

    private static RoiDefinition NormalizeTemplateRoi(RoiDefinition roi)
    {
        var id = roi.Id ?? string.Empty;
        var name = roi.Name ?? string.Empty;
        return roi.Shape switch
        {
            RoiShapeKind.Rectangle => new RoiDefinition
            {
                Id = id,
                Name = name,
                Shape = RoiShapeKind.Rectangle,
                X = roi.X,
                Y = roi.Y,
                Width = roi.Width,
                Height = roi.Height,
                Points = Array.Empty<Point2D>()
            },
            RoiShapeKind.Circle => new RoiDefinition
            {
                Id = id,
                Name = name,
                Shape = RoiShapeKind.Circle,
                X = roi.X,
                Y = roi.Y,
                Radius = roi.Radius,
                Points = Array.Empty<Point2D>()
            },
            RoiShapeKind.RotatedRectangle => new RoiDefinition
            {
                Id = id,
                Name = name,
                Shape = RoiShapeKind.RotatedRectangle,
                X = roi.X,
                Y = roi.Y,
                Width = roi.Width,
                Height = roi.Height,
                Angle = roi.Angle,
                Points = Array.Empty<Point2D>()
            },
            RoiShapeKind.Polygon => new RoiDefinition
            {
                Id = id,
                Name = name,
                Shape = RoiShapeKind.Polygon,
                Points = roi.Points.ToArray()
            },
            _ => throw new InvalidOperationException($"Template ROI shape '{roi.Shape}' cannot be normalized.")
        };
    }

    public static Dictionary<string, string> MergeLearningParameters(
        IReadOnlyDictionary<string, string> runtimeParameters,
        IReadOnlyDictionary<string, string> learned,
        string templateRoiId,
        TemplateMatchingEngine engine)
    {
        var result = new Dictionary<string, string>(runtimeParameters, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in learned)
        {
            result[pair.Key] = pair.Value;
        }

        result[TemplateMatchingParameterCatalog.Engine] = engine.ToString();
        result["templateSourceRoiId"] = templateRoiId ?? string.Empty;
        return result;
    }

    public static bool TryCreateTemplateRoiContours(
        IReadOnlyDictionary<string, string> parameters,
        Pose2D pose,
        out IReadOnlyList<IReadOnlyList<Point2D>> contours,
        out int templateWidth,
        out int templateHeight,
        out string technicalDetails)
    {
        contours = Array.Empty<IReadOnlyList<Point2D>>();
        technicalDetails = string.Empty;
        if (!TryReadTemplateDimensions(parameters, out templateWidth, out templateHeight) ||
            !TryReadFinite(parameters, "templateX", out var templateX) ||
            !TryReadFinite(parameters, "templateY", out var templateY))
        {
            technicalDetails = "Legacy template reference dimensions or origin are missing.";
            return false;
        }

        if (!TryReadTemplateRoi(parameters, templateX, templateY, templateWidth, templateHeight, out var roi, out technicalDetails))
        {
            return false;
        }

        var sourceContour = CreateRoiContour(roi);
        if (sourceContour.Count < 3)
        {
            technicalDetails = "Legacy template reference ROI has fewer than three points.";
            return false;
        }

        var referenceX = templateX + templateWidth / 2d;
        var referenceY = templateY + templateHeight / 2d;
        var radians = pose.Angle * Math.PI / 180d;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var transformed = sourceContour
            .Select(point =>
            {
                var dx = (point.X - referenceX) * pose.Scale;
                var dy = (point.Y - referenceY) * pose.Scale;
                return new Point2D(
                    pose.X + dx * cos - dy * sin,
                    pose.Y + dx * sin + dy * cos);
            })
            .ToArray();
        contours = [transformed];
        return true;
    }

    public static IReadOnlyList<IReadOnlyList<Point2D>> CreateRectangleContours(
        Pose2D pose,
        int templateWidth,
        int templateHeight)
    {
        var halfWidth = templateWidth / 2d;
        var halfHeight = templateHeight / 2d;
        var radians = pose.Angle * Math.PI / 180d;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var local = new[]
        {
            new Point2D(-halfWidth, -halfHeight),
            new Point2D(halfWidth, -halfHeight),
            new Point2D(halfWidth, halfHeight),
            new Point2D(-halfWidth, halfHeight)
        };
        return
        [
            local.Select(point => new Point2D(
                pose.X + pose.Scale * (point.X * cos - point.Y * sin),
                pose.Y + pose.Scale * (point.X * sin + point.Y * cos))).ToArray()
        ];
    }

    public static bool TryReadTemplateDimensions(
        IReadOnlyDictionary<string, string> parameters,
        out int templateWidth,
        out int templateHeight)
    {
        templateWidth = 0;
        templateHeight = 0;
        return TryReadPositiveInt(parameters, "templateWidth", out templateWidth) &&
               TryReadPositiveInt(parameters, "templateHeight", out templateHeight);
    }

    public static bool TryValidateCandidate(
        TemplateMatchBatchCandidate candidate,
        out string technicalDetails)
    {
        try
        {
            TemplateMatchResultProjector.ValidateCandidate(candidate);
            technicalDetails = string.Empty;
            return true;
        }
        catch (InvalidOperationException exception)
        {
            technicalDetails = exception.Message;
            return false;
        }
    }

    public static TemplateMatchBatchResult ContractFailure(
        TemplateMatchingEngine engine,
        TemplateSearchRegion searchRegion,
        bool usedAutoTemplate,
        string technicalDetails)
    {
        var diagnostic = TemplateMatchingDiagnostics.Create(
            TemplateMatchingDiagnosticCodes.MatchOperatorFailed,
            technicalDetails);
        return new TemplateMatchBatchResult(
            engine,
            InspectionOutcome.Ng,
            false,
            Array.Empty<TemplateMatchBatchCandidate>(),
            searchRegion,
            diagnostic.UserMessage,
            usedAutoTemplate,
            diagnostic);
    }

    public static TemplateMatchingDiagnostic? ReadDiagnostic(
        string? code,
        string? failureStage,
        string message,
        string? technicalDetails)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return new TemplateMatchingDiagnostic(
            code,
            message,
            string.IsNullOrWhiteSpace(failureStage)
                ? TemplateMatchingDiagnostics.Create(code).FailureStage
                : failureStage,
            technicalDetails);
    }

    private static (double X, double Y, double Width, double Height) GetBounds(RoiDefinition roi)
    {
        return roi.Shape switch
        {
            RoiShapeKind.Circle => (roi.X - roi.Radius, roi.Y - roi.Radius, roi.Radius * 2, roi.Radius * 2),
            RoiShapeKind.Polygon when roi.Points.Count >= 3 => (
                roi.Points.Min(point => point.X),
                roi.Points.Min(point => point.Y),
                roi.Points.Max(point => point.X) - roi.Points.Min(point => point.X),
                roi.Points.Max(point => point.Y) - roi.Points.Min(point => point.Y)),
            RoiShapeKind.RotatedRectangle => RotatedBounds(roi),
            RoiShapeKind.Rectangle => (roi.X, roi.Y, roi.Width, roi.Height),
            _ => (double.NaN, double.NaN, double.NaN, double.NaN)
        };
    }

    private static (double X, double Y, double Width, double Height) RotatedBounds(RoiDefinition roi)
    {
        var radians = roi.Angle * Math.PI / 180d;
        var width = Math.Abs(roi.Width * Math.Cos(radians)) + Math.Abs(roi.Height * Math.Sin(radians));
        var height = Math.Abs(roi.Width * Math.Sin(radians)) + Math.Abs(roi.Height * Math.Cos(radians));
        return (roi.X - width / 2d, roi.Y - height / 2d, width, height);
    }

    private static bool TryReadTemplateRoi(
        IReadOnlyDictionary<string, string> parameters,
        double templateX,
        double templateY,
        int templateWidth,
        int templateHeight,
        out RoiDefinition roi,
        out string technicalDetails)
    {
        technicalDetails = string.Empty;
        if (parameters.TryGetValue("templateRoiJson", out var json) && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                roi = JsonSerializer.Deserialize<RoiDefinition>(json)!;
                if (roi is not null)
                {
                    return true;
                }
            }
            catch (JsonException)
            {
            }

            roi = default!;
            technicalDetails = "Legacy templateRoiJson is invalid.";
            return false;
        }

        var keys = new[] { "templateRoiX", "templateRoiY", "templateRoiWidth", "templateRoiHeight" };
        var hasAnyRoiBounds = keys.Any(parameters.ContainsKey);
        if (hasAnyRoiBounds)
        {
            if (!TryReadFinite(parameters, keys[0], out var x) ||
                !TryReadFinite(parameters, keys[1], out var y) ||
                !TryReadPositiveFinite(parameters, keys[2], out var width) ||
                !TryReadPositiveFinite(parameters, keys[3], out var height))
            {
                roi = default!;
                technicalDetails = "Legacy template ROI bounds are incomplete or invalid.";
                return false;
            }

            var shape = RoiShapeKind.Rectangle;
            if (parameters.TryGetValue("templateRoiShape", out var shapeText) &&
                !Enum.TryParse(shapeText, true, out shape))
            {
                roi = default!;
                technicalDetails = $"Legacy template ROI shape '{shapeText}' is invalid.";
                return false;
            }

            if (shape == RoiShapeKind.Polygon)
            {
                roi = default!;
                technicalDetails = "Polygon template ROI requires templateRoiJson.";
                return false;
            }

            var angle = 0d;
            if (parameters.TryGetValue("templateRoiAngle", out _) &&
                !TryReadFinite(parameters, "templateRoiAngle", out angle))
            {
                roi = default!;
                technicalDetails = "Legacy template ROI angle is invalid.";
                return false;
            }

            roi = new RoiDefinition
            {
                Shape = shape,
                X = shape is RoiShapeKind.Circle or RoiShapeKind.RotatedRectangle ? x + width / 2d : x,
                Y = shape is RoiShapeKind.Circle or RoiShapeKind.RotatedRectangle ? y + height / 2d : y,
                Width = width,
                Height = height,
                Radius = Math.Min(width, height) / 2d,
                Angle = angle
            };
            return true;
        }

        roi = new RoiDefinition
        {
            Shape = RoiShapeKind.Rectangle,
            X = templateX,
            Y = templateY,
            Width = templateWidth,
            Height = templateHeight
        };
        return true;
    }

    private static IReadOnlyList<Point2D> CreateRoiContour(RoiDefinition roi)
    {
        return roi.Shape switch
        {
            RoiShapeKind.Rectangle when IsPositiveFinite(roi.Width) && IsPositiveFinite(roi.Height) =>
            [
                new Point2D(roi.X, roi.Y),
                new Point2D(roi.X + roi.Width, roi.Y),
                new Point2D(roi.X + roi.Width, roi.Y + roi.Height),
                new Point2D(roi.X, roi.Y + roi.Height)
            ],
            RoiShapeKind.Polygon when roi.Points.Count >= 3 => roi.Points,
            RoiShapeKind.Circle when IsPositiveFinite(roi.Radius) => Enumerable.Range(0, 64)
                .Select(index =>
                {
                    var radians = index * Math.PI * 2d / 64d;
                    return new Point2D(
                        roi.X + roi.Radius * Math.Cos(radians),
                        roi.Y + roi.Radius * Math.Sin(radians));
                })
                .ToArray(),
            RoiShapeKind.RotatedRectangle when IsPositiveFinite(roi.Width) && IsPositiveFinite(roi.Height) =>
                CreateRotatedRectangleContour(roi),
            _ => Array.Empty<Point2D>()
        };
    }

    private static IReadOnlyList<Point2D> CreateRotatedRectangleContour(RoiDefinition roi)
    {
        var radians = roi.Angle * Math.PI / 180d;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        var halfWidth = roi.Width / 2d;
        var halfHeight = roi.Height / 2d;
        return
        [
            Transform(-halfWidth, -halfHeight),
            Transform(halfWidth, -halfHeight),
            Transform(halfWidth, halfHeight),
            Transform(-halfWidth, halfHeight)
        ];

        Point2D Transform(double x, double y)
        {
            return new Point2D(
                roi.X + x * cos - y * sin,
                roi.Y + x * sin + y * cos);
        }
    }

    private static bool TryReadPositiveInt(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out int value)
    {
        value = 0;
        return parameters.TryGetValue(key, out var raw) &&
               int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
               value > 0;
    }

    private static bool TryReadFinite(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out double value)
    {
        value = 0;
        return parameters.TryGetValue(key, out var raw) &&
               double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value);
    }

    private static bool TryReadPositiveFinite(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        out double value)
    {
        return TryReadFinite(parameters, key, out value) && value > 0;
    }

    private static bool IsPositiveFinite(double value)
    {
        return double.IsFinite(value) && value > 0;
    }
}
