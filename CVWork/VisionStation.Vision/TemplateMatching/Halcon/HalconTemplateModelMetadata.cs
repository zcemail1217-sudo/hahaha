using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionStation.Domain;

namespace VisionStation.Vision;

internal sealed record HalconSupportRun(int Row, int ColumnStart, int Length);

internal sealed class HalconFilledSupportRegion
{
    private readonly HalconSupportRun[] _runs;

    public HalconFilledSupportRegion(
        double originX,
        double originY,
        IReadOnlyList<HalconSupportRun> runs)
    {
        if (!double.IsFinite(originX))
        {
            throw new ArgumentOutOfRangeException(nameof(originX));
        }

        if (!double.IsFinite(originY))
        {
            throw new ArgumentOutOfRangeException(nameof(originY));
        }

        ArgumentNullException.ThrowIfNull(runs);
        if (runs.Count == 0)
        {
            throw new ArgumentException("Filled template support must contain at least one run.", nameof(runs));
        }

        HalconSupportRun[] snapshot = runs.ToArray();
        var area = 0;
        var previousRow = -1;
        var previousEnd = -1;
        foreach (HalconSupportRun run in snapshot)
        {
            ArgumentNullException.ThrowIfNull(run);
            if (run.Row < 0 || run.ColumnStart < 0 || run.Length <= 0)
            {
                throw new ArgumentException(
                    "Filled template support runs require non-negative coordinates and positive lengths.",
                    nameof(runs));
            }

            int runEnd = checked(run.ColumnStart + (run.Length - 1));
            if (run.Row < previousRow ||
                (run.Row == previousRow && run.ColumnStart <= previousEnd))
            {
                throw new ArgumentException(
                    "Filled template support runs must be sorted and non-overlapping.",
                    nameof(runs));
            }

            area = checked(area + run.Length);
            previousRow = run.Row;
            previousEnd = runEnd;
        }

        _runs = snapshot;
        OriginX = originX;
        OriginY = originY;
        Runs = new ReadOnlyCollection<HalconSupportRun>(_runs);
        Area = area;
        MinimumRow = snapshot.Min(run => run.Row);
        MaximumRow = snapshot.Max(run => run.Row);
        MinimumColumn = snapshot.Min(run => run.ColumnStart);
        MaximumColumn = snapshot.Max(run => checked(run.ColumnStart + (run.Length - 1)));
    }

    public double OriginX { get; }

    public double OriginY { get; }

    public IReadOnlyList<HalconSupportRun> Runs { get; }

    [JsonIgnore]
    public int Area { get; }

    internal int MinimumRow { get; }

    internal int MaximumRow { get; }

    internal int MinimumColumn { get; }

    internal int MaximumColumn { get; }

    internal bool Contains(double relativeX, double relativeY)
    {
        int column = (int)Math.Round(relativeX - OriginX, MidpointRounding.AwayFromZero);
        int row = (int)Math.Round(relativeY - OriginY, MidpointRounding.AwayFromZero);
        var lower = 0;
        var upper = _runs.Length;
        while (lower < upper)
        {
            int middle = lower + (upper - lower) / 2;
            if (_runs[middle].Row < row)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle;
            }
        }

        for (var index = lower; index < _runs.Length && _runs[index].Row == row; index++)
        {
            HalconSupportRun run = _runs[index];
            if (column < run.ColumnStart)
            {
                return false;
            }

            if (column - run.ColumnStart < run.Length)
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed record HalconTemplateValidationDefaults(
    double CandidateMinScore,
    double OuterCoverageMin,
    double InnerCoverageMin,
    double EdgeTolerancePx,
    double PolarityAgreementMin,
    double CandidateMaxOverlap,
    double MaxOverlap,
    double Greediness,
    string SubPixel,
    int CandidateLimit,
    int OperatorTimeoutMs,
    int ExpectedCount)
{
    public static HalconTemplateValidationDefaults From(HalconTemplateMatchingParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new HalconTemplateValidationDefaults(
            parameters.CandidateMinScore,
            parameters.OuterCoverageMin,
            parameters.InnerCoverageMin,
            parameters.EdgeTolerancePx,
            parameters.PolarityAgreementMin,
            parameters.CandidateMaxOverlap,
            parameters.MaxOverlap,
            parameters.Greediness,
            parameters.SubPixel,
            parameters.CandidateLimit,
            parameters.OperatorTimeoutMs,
            parameters.ExpectedCount);
    }
}

internal sealed class HalconTemplateModelMetadata
{
    public const int CurrentSchemaVersion = 1;
    public const string CurrentEngine = "Halcon";
    public const string CurrentModelVersion = "halcon-scaled-shape-v1";
    public const string CurrentManagedPackageVersion = "26050.0.0";
    public const string CurrentManagedAssemblyVersion = "26050.0.0.0";
    public const string CurrentNativeRuntimeVersion = "26.05.0.0";

    public HalconTemplateModelMetadata(
        TemplateModelOwner owner,
        string generation,
        string modelFileName,
        string modelChecksum,
        TemplateLearnedGeometry geometry,
        double referenceRow,
        double referenceColumn,
        double modelDomainCentroidRow,
        double modelDomainCentroidColumn,
        bool isDarkForeground,
        IReadOnlyList<Point2D> outerContour,
        IReadOnlyList<IReadOnlyList<Point2D>> innerFeatureGroups,
        int minimumValidInnerGroupCount,
        HalconFilledSupportRegion filledSupport,
        TemplateModelGenerationParameters generationParameters,
        string generationParameterFingerprint,
        HalconTemplateValidationDefaults validationDefaultsAtLearn)
        : this(
            CurrentSchemaVersion,
            CurrentEngine,
            TemplateModelParameterCodec.HalconScaledShapeModelFormat,
            CurrentModelVersion,
            CurrentManagedPackageVersion,
            CurrentManagedAssemblyVersion,
            CurrentNativeRuntimeVersion,
            owner,
            generation,
            modelFileName,
            modelChecksum,
            geometry,
            referenceRow,
            referenceColumn,
            modelDomainCentroidRow,
            modelDomainCentroidColumn,
            isDarkForeground,
            outerContour,
            innerFeatureGroups,
            minimumValidInnerGroupCount,
            filledSupport,
            generationParameters,
            generationParameterFingerprint,
            validationDefaultsAtLearn)
    {
    }

    [JsonConstructor]
    public HalconTemplateModelMetadata(
        int schemaVersion,
        string engine,
        string modelFormat,
        string modelVersion,
        string managedPackageVersion,
        string managedAssemblyVersion,
        string nativeRuntimeVersion,
        TemplateModelOwner owner,
        string generation,
        string modelFileName,
        string modelChecksum,
        TemplateLearnedGeometry geometry,
        double referenceRow,
        double referenceColumn,
        double modelDomainCentroidRow,
        double modelDomainCentroidColumn,
        bool isDarkForeground,
        IReadOnlyList<Point2D> outerContour,
        IReadOnlyList<IReadOnlyList<Point2D>> innerFeatureGroups,
        int minimumValidInnerGroupCount,
        HalconFilledSupportRegion filledSupport,
        TemplateModelGenerationParameters generationParameters,
        string generationParameterFingerprint,
        HalconTemplateValidationDefaults validationDefaultsAtLearn)
    {
        ValidateSerializedHeader(
            schemaVersion,
            engine,
            modelFormat,
            modelVersion,
            managedPackageVersion,
            managedAssemblyVersion,
            nativeRuntimeVersion);
        ArgumentNullException.ThrowIfNull(owner);
        ValidateRequired(owner.RecipeId, nameof(owner.RecipeId));
        ValidateRequired(owner.FlowId, nameof(owner.FlowId));
        ValidateRequired(owner.ToolId, nameof(owner.ToolId));
        ValidateRequired(generation, nameof(generation));
        ValidateRequired(modelFileName, nameof(modelFileName));
        ValidateSha256(modelChecksum, nameof(modelChecksum));
        ArgumentNullException.ThrowIfNull(geometry);
        ValidateGeometry(geometry);
        ValidateFinite(referenceRow, nameof(referenceRow));
        ValidateFinite(referenceColumn, nameof(referenceColumn));
        ValidateFinite(modelDomainCentroidRow, nameof(modelDomainCentroidRow));
        ValidateFinite(modelDomainCentroidColumn, nameof(modelDomainCentroidColumn));
        if (!isDarkForeground)
        {
            throw new ArgumentException("HALCON scaled-shape templates require dark foreground polarity.", nameof(isDarkForeground));
        }

        IReadOnlyList<Point2D> outerSnapshot = HalconMetadataSnapshots.Points(outerContour, nameof(outerContour));
        IReadOnlyList<IReadOnlyList<Point2D>> innerSnapshot =
            HalconMetadataSnapshots.Groups(innerFeatureGroups, nameof(innerFeatureGroups));
        if (outerSnapshot.Count < 100)
        {
            throw new ArgumentException(
                "The learned outer template contour must contain at least 100 points.",
                nameof(outerContour));
        }

        if (innerSnapshot.Count < 3)
        {
            throw new ArgumentException(
                "At least three learned inner feature groups are required.",
                nameof(innerFeatureGroups));
        }

        int expectedMinimumValidInnerGroupCount = Math.Max(
            2,
            (int)Math.Ceiling(innerSnapshot.Count * 0.67));
        if (minimumValidInnerGroupCount != expectedMinimumValidInnerGroupCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumValidInnerGroupCount),
                $"The learned inner-group quorum must equal {expectedMinimumValidInnerGroupCount}.");
        }

        ArgumentNullException.ThrowIfNull(filledSupport);
        ArgumentNullException.ThrowIfNull(generationParameters);
        ValidateGenerationParameters(generationParameters);
        ValidateSha256(generationParameterFingerprint, nameof(generationParameterFingerprint));
        string expectedGenerationParameterFingerprint =
            TemplateModelGenerationFingerprint.Compute(generationParameters);
        if (!string.Equals(
                generationParameterFingerprint,
                expectedGenerationParameterFingerprint,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The generation-parameter fingerprint does not match the immutable generation parameters.",
                nameof(generationParameterFingerprint));
        }

        ArgumentNullException.ThrowIfNull(validationDefaultsAtLearn);
        ValidateValidationDefaults(validationDefaultsAtLearn);

        SchemaVersion = schemaVersion;
        Engine = engine;
        ModelFormat = modelFormat;
        ModelVersion = modelVersion;
        ManagedPackageVersion = managedPackageVersion;
        ManagedAssemblyVersion = managedAssemblyVersion;
        NativeRuntimeVersion = nativeRuntimeVersion;
        Owner = new TemplateModelOwner(owner.RecipeId, owner.FlowId, owner.ToolId);
        Generation = generation;
        ModelFileName = modelFileName;
        ModelChecksum = modelChecksum.ToLowerInvariant();
        Geometry = new TemplateLearnedGeometry(
            new Pose2D(
                geometry.StandardPose.X,
                geometry.StandardPose.Y,
                geometry.StandardPose.Angle)
            {
                Scale = geometry.StandardPose.Scale
            },
            geometry.TemplateWidth,
            geometry.TemplateHeight);
        ReferenceRow = referenceRow;
        ReferenceColumn = referenceColumn;
        ModelDomainCentroidRow = modelDomainCentroidRow;
        ModelDomainCentroidColumn = modelDomainCentroidColumn;
        IsDarkForeground = true;
        OuterContour = outerSnapshot;
        InnerFeatureGroups = innerSnapshot;
        MinimumValidInnerGroupCount = minimumValidInnerGroupCount;
        FilledSupport = new HalconFilledSupportRegion(
            filledSupport.OriginX,
            filledSupport.OriginY,
            filledSupport.Runs);
        GenerationParameters = generationParameters with { };
        GenerationParameterFingerprint = generationParameterFingerprint.ToLowerInvariant();
        ValidationDefaultsAtLearn = validationDefaultsAtLearn with { };
    }

    public int SchemaVersion { get; }

    public string Engine { get; }

    public string ModelFormat { get; }

    public string ModelVersion { get; }

    public string ManagedPackageVersion { get; }

    public string ManagedAssemblyVersion { get; }

    public string NativeRuntimeVersion { get; }

    public TemplateModelOwner Owner { get; }

    public string Generation { get; }

    public string ModelFileName { get; }

    public string ModelChecksum { get; }

    public TemplateLearnedGeometry Geometry { get; }

    [JsonIgnore]
    public int TemplateWidth => Geometry.TemplateWidth;

    [JsonIgnore]
    public int TemplateHeight => Geometry.TemplateHeight;

    public double ReferenceRow { get; }

    public double ReferenceColumn { get; }

    public double ModelDomainCentroidRow { get; }

    public double ModelDomainCentroidColumn { get; }

    public bool IsDarkForeground { get; }

    public IReadOnlyList<Point2D> OuterContour { get; }

    public IReadOnlyList<IReadOnlyList<Point2D>> InnerFeatureGroups { get; }

    public int MinimumValidInnerGroupCount { get; }

    public HalconFilledSupportRegion FilledSupport { get; }

    public TemplateModelGenerationParameters GenerationParameters { get; }

    public string GenerationParameterFingerprint { get; }

    public HalconTemplateValidationDefaults ValidationDefaultsAtLearn { get; }

    private static void ValidateGeometry(TemplateLearnedGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry.StandardPose);
        ValidateFinite(geometry.StandardPose.X, "StandardPose.X");
        ValidateFinite(geometry.StandardPose.Y, "StandardPose.Y");
        ValidateFinite(geometry.StandardPose.Angle, "StandardPose.Angle");
        if (!double.IsFinite(geometry.StandardPose.Scale) || geometry.StandardPose.Scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(geometry), "Standard pose scale must be positive and finite.");
        }

        if (geometry.TemplateWidth <= 0 || geometry.TemplateHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(geometry), "Template dimensions must be positive.");
        }
    }

    private static void ValidateSerializedHeader(
        int schemaVersion,
        string? engine,
        string? modelFormat,
        string? modelVersion,
        string? managedPackageVersion,
        string? managedAssemblyVersion,
        string? nativeRuntimeVersion)
    {
        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        ValidateRequired(engine, nameof(engine));
        ValidateRequired(modelFormat, nameof(modelFormat));
        ValidateRequired(modelVersion, nameof(modelVersion));
        ValidateRequired(managedPackageVersion, nameof(managedPackageVersion));
        ValidateRequired(managedAssemblyVersion, nameof(managedAssemblyVersion));
        ValidateRequired(nativeRuntimeVersion, nameof(nativeRuntimeVersion));
    }

    private static void ValidateGenerationParameters(TemplateModelGenerationParameters parameters)
    {
        ValidateFinite(parameters.AngleStartDeg, nameof(parameters.AngleStartDeg));
        ValidateFinite(parameters.AngleExtentDeg, nameof(parameters.AngleExtentDeg));
        ValidateFinite(parameters.ScaleMin, nameof(parameters.ScaleMin));
        ValidateFinite(parameters.ScaleMax, nameof(parameters.ScaleMax));
        double angleEndDeg = parameters.AngleStartDeg + parameters.AngleExtentDeg;
        if (parameters.AngleExtentDeg <= 0 ||
            !double.IsFinite(angleEndDeg) ||
            angleEndDeg <= parameters.AngleStartDeg ||
            parameters.ScaleMin <= 0 ||
            parameters.ScaleMax < parameters.ScaleMin ||
            parameters.NumLevels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters));
        }
    }

    private static void ValidateValidationDefaults(HalconTemplateValidationDefaults defaults)
    {
        ValidateUnitInterval(defaults.CandidateMinScore, nameof(defaults.CandidateMinScore));
        ValidateUnitInterval(defaults.OuterCoverageMin, nameof(defaults.OuterCoverageMin));
        ValidateUnitInterval(defaults.InnerCoverageMin, nameof(defaults.InnerCoverageMin));
        ValidateUnitInterval(defaults.PolarityAgreementMin, nameof(defaults.PolarityAgreementMin));
        ValidateUnitInterval(defaults.CandidateMaxOverlap, nameof(defaults.CandidateMaxOverlap));
        ValidateUnitInterval(defaults.MaxOverlap, nameof(defaults.MaxOverlap));
        ValidateUnitInterval(defaults.Greediness, nameof(defaults.Greediness));
        if (!double.IsFinite(defaults.EdgeTolerancePx) ||
            defaults.EdgeTolerancePx <= 0 ||
            defaults.EdgeTolerancePx > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(defaults.EdgeTolerancePx));
        }

        if (defaults.MaxOverlap > defaults.CandidateMaxOverlap)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaults.MaxOverlap),
                "Runtime duplicate overlap cannot exceed candidate-source overlap.");
        }

        if (!string.Equals(defaults.SubPixel, "least_squares", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "HALCON validation audit requires least_squares sub-pixel refinement.",
                nameof(defaults.SubPixel));
        }

        if (defaults.CandidateLimit is < 2 or > 512)
        {
            throw new ArgumentOutOfRangeException(nameof(defaults.CandidateLimit));
        }

        if (defaults.OperatorTimeoutMs is < 100 or > 60000)
        {
            throw new ArgumentOutOfRangeException(nameof(defaults.OperatorTimeoutMs));
        }

        if (defaults.ExpectedCount is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(defaults.ExpectedCount));
        }

        if (defaults.CandidateLimit <= defaults.ExpectedCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaults.CandidateLimit),
                "Candidate limit must exceed the expected match count.");
        }
    }

    private static void ValidateUnitInterval(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0 || value > 1)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private static void ValidateRequired(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A non-empty trimmed value is required.", parameterName);
        }
    }

    private static void ValidateSha256(string? value, string parameterName)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A 64-character SHA-256 value is required.", parameterName);
        }
    }

    private static void ValidateFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}

internal static class HalconTemplateModelMetadataJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] Serialize(HalconTemplateModelMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return JsonSerializer.SerializeToUtf8Bytes(metadata, SerializerOptions);
    }

    public static HalconTemplateModelMetadata Deserialize(ReadOnlySpan<byte> json)
    {
        byte[] snapshot = json.ToArray();
        using (JsonDocument document = JsonDocument.Parse(snapshot))
        {
            RejectCaseInsensitiveDuplicateProperties(document.RootElement);
            RejectNonPersistentDerivedProperties(document.RootElement);
        }

        return JsonSerializer.Deserialize<HalconTemplateModelMetadata>(snapshot, SerializerOptions) ??
               throw new JsonException("HALCON template metadata must be a JSON object.");
    }

    private static void RejectNonPersistentDerivedProperties(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        RejectProperty(root, "templateWidth");
        RejectProperty(root, "templateHeight");
        if (root.TryGetProperty("filledSupport", out JsonElement filledSupport) &&
            filledSupport.ValueKind == JsonValueKind.Object)
        {
            RejectProperty(filledSupport, "area");
        }
    }

    private static void RejectProperty(JsonElement owner, string propertyName)
    {
        if (owner.TryGetProperty(propertyName, out _))
        {
            throw new JsonException(
                $"JSON property '{propertyName}' is derived and cannot be persisted.");
        }
    }

    private static void RejectCaseInsensitiveDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException(
                        $"Duplicate JSON property '{property.Name}' is not allowed.");
                }

                RejectCaseInsensitiveDuplicateProperties(property.Value);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement item in element.EnumerateArray())
        {
            RejectCaseInsensitiveDuplicateProperties(item);
        }
    }
}

internal static class HalconMetadataSnapshots
{
    public static IReadOnlyList<Point2D> Points(IReadOnlyList<Point2D> source, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        Point2D[] snapshot = source.ToArray();
        foreach (Point2D point in snapshot)
        {
            ArgumentNullException.ThrowIfNull(point);
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
            {
                throw new ArgumentException("Template geometry points must be finite.", parameterName);
            }
        }

        return new ReadOnlyCollection<Point2D>(snapshot);
    }

    public static IReadOnlyList<IReadOnlyList<Point2D>> Groups(
        IReadOnlyList<IReadOnlyList<Point2D>> source,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        var snapshot = new IReadOnlyList<Point2D>[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            snapshot[index] = Points(
                source[index] ?? throw new ArgumentException(
                    "Inner feature groups cannot contain null entries.",
                    parameterName),
                parameterName);
            if (snapshot[index].Count == 0)
            {
                throw new ArgumentException("Inner feature groups cannot be empty.", parameterName);
            }
        }

        return new ReadOnlyCollection<IReadOnlyList<Point2D>>(snapshot);
    }
}
