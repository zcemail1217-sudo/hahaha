namespace VisionStation.Vision;

internal readonly record struct HalconNativeAngleInterval(
    double StartRadians,
    double ExtentRadians);

internal static class HalconAngleConvention
{
    /// <summary>
    /// Converts the UI/image convention (clockwise-positive degrees) into HALCON's
    /// mathematical convention (counter-clockwise-positive radians) without changing the
    /// persisted UI generation parameters.
    /// </summary>
    public static HalconNativeAngleInterval ToNativeInterval(
        TemplateModelGenerationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        double uiEnd = parameters.AngleStartDeg + parameters.AngleExtentDeg;
        if (!double.IsFinite(parameters.AngleStartDeg) ||
            !double.IsFinite(parameters.AngleExtentDeg) ||
            !double.IsFinite(uiEnd) ||
            parameters.AngleExtentDeg <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters));
        }

        return new HalconNativeAngleInterval(
            DegreesToRadians(-uiEnd),
            DegreesToRadians(parameters.AngleExtentDeg));
    }

    public static double ToUiDegrees(double nativeRadians)
    {
        if (!double.IsFinite(nativeRadians))
        {
            throw new ArgumentOutOfRangeException(nameof(nativeRadians));
        }

        return -nativeRadians * 180.0 / Math.PI;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;
}

internal sealed class HalconShapeModelCreationRequest
{
    public HalconShapeModelCreationRequest(
        TightGray8Image templateImage,
        HalconModelDomain modelDomain,
        TemplateModelGenerationParameters generationParameters,
        double referenceRow,
        double referenceColumn)
    {
        ArgumentNullException.ThrowIfNull(templateImage);
        ArgumentNullException.ThrowIfNull(modelDomain);
        ArgumentNullException.ThrowIfNull(generationParameters);
        if (templateImage.Width != modelDomain.Width || templateImage.Height != modelDomain.Height)
        {
            throw new ArgumentException("The model domain must use the template-image coordinate system.");
        }

        if (!double.IsFinite(referenceRow) || !double.IsFinite(referenceColumn))
        {
            throw new ArgumentOutOfRangeException(nameof(referenceRow));
        }

        if (referenceRow < 0 || referenceRow > templateImage.Height - 1 ||
            referenceColumn < 0 || referenceColumn > templateImage.Width - 1 ||
            !double.IsFinite(generationParameters.ScaleMin) ||
            !double.IsFinite(generationParameters.ScaleMax) ||
            generationParameters.ScaleMin <= 0 ||
            generationParameters.ScaleMax < generationParameters.ScaleMin ||
            generationParameters.NumLevels < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(referenceRow));
        }

        TemplateImage = templateImage;
        ModelDomain = modelDomain;
        GenerationParameters = generationParameters with { };
        OriginRow = referenceRow - modelDomain.CentroidRow;
        OriginColumn = referenceColumn - modelDomain.CentroidColumn;
    }

    public TightGray8Image TemplateImage { get; }

    public HalconModelDomain ModelDomain { get; }

    public TemplateModelGenerationParameters GenerationParameters { get; }

    public string Metric => "use_polarity";

    public bool AllowBorderShapeModels => false;

    public double OriginRow { get; }

    public double OriginColumn { get; }
}

internal sealed class HalconOperatorFailure : Exception
{
    public HalconOperatorFailure(string code, string? technicalDetails = null, Exception? innerException = null)
        : base(TemplateMatchingDiagnostics.Create(code).UserMessage, innerException)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A stable HALCON operator failure code is required.", nameof(code));
        }

        Code = code;
        TechnicalDetails = technicalDetails;
    }

    public string Code { get; }

    public string? TechnicalDetails { get; }
}

internal sealed class HalconContourSamples
{
    public HalconContourSamples(
        IReadOnlyList<double> rows,
        IReadOnlyList<double> columns)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(columns);
        Rows = Array.AsReadOnly(rows.ToArray());
        Columns = Array.AsReadOnly(columns.ToArray());
    }

    public IReadOnlyList<double> Rows { get; }

    public IReadOnlyList<double> Columns { get; }
}

internal static class HalconModelHealthValidator
{
    public static void Validate(IReadOnlyList<HalconContourSamples> contours)
    {
        ArgumentNullException.ThrowIfNull(contours);
        if (contours.Count == 0)
        {
            throw new InvalidDataException("The HALCON shape model contains no level-one contours.");
        }

        foreach (HalconContourSamples contour in contours)
        {
            if (contour is null ||
                contour.Rows.Count == 0 ||
                contour.Rows.Count != contour.Columns.Count)
            {
                throw new InvalidDataException(
                    "Every HALCON level-one contour must contain paired row/column samples.");
            }

            for (var index = 0; index < contour.Rows.Count; index++)
            {
                if (!double.IsFinite(contour.Rows[index]) ||
                    !double.IsFinite(contour.Columns[index]))
                {
                    throw new InvalidDataException(
                        "Every HALCON level-one contour sample must be finite.");
                }
            }
        }
    }
}

internal interface IHalconOperatorBackend
{
    void CreateAndWriteShapeModel(
        HalconShapeModelCreationRequest request,
        string stagingModelPath);

    IHalconRawModelHandle LoadShapeModelAndValidate(string modelPath);

    void VerifyMatchingLicense();
}
