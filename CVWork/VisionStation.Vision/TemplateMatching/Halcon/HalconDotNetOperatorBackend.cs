using HalconDotNet;

namespace VisionStation.Vision;

/// <summary>
/// The only adapter that translates managed HALCON DTOs into HalconDotNet objects.
/// </summary>
internal sealed class HalconDotNetOperatorBackend : IHalconOperatorBackend
{
    public void CreateAndWriteShapeModel(
        HalconShapeModelCreationRequest request,
        string stagingModelPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingModelPath);
        using HShapeModel model = CreateShapeModel(request);
        model.WriteShapeModel(stagingModelPath);
    }

    public IHalconRawModelHandle LoadShapeModelAndValidate(string modelPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        HShapeModel? model = null;
        try
        {
            model = new HShapeModel(modelPath);
            ValidateLevelOneContours(model);
            var handle = new HalconDotNetModelHandle(model);
            model = null;
            return handle;
        }
        finally
        {
            model?.Dispose();
        }
    }

    public HalconShapeModelFindResult FindScaledShapeModel(
        IHalconModelBorrow model,
        HalconShapeModelFindRequest request)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(request);
        if (model is not HalconDotNetModelHandle ownedModel)
        {
            throw new ArgumentException(
                "The borrowed HALCON model was not created by this operator backend.",
                nameof(model));
        }

        HShapeModel shapeModel = ownedModel.BorrowModel();
        HalconTemplateMatchingParameters parameters = request.Parameters;
        TemplateModelGenerationParameters generation =
            TemplateModelGenerationParameters.From(parameters);
        HalconNativeAngleInterval angles = HalconAngleConvention.ToNativeInterval(generation);
        using HImage source = CreateCopiedImage(request.SearchImage);
        using HRegion domain = CreateRegion(request.SearchDomain);
        using HImage searchImage = source.ReduceDomain(domain);
        using var timeoutName = new HTuple("timeout");
        using var timeoutValue = new HTuple(parameters.OperatorTimeoutMs);
        shapeModel.SetShapeModelParam(timeoutName, timeoutValue);
        shapeModel.FindScaledShapeModel(
            searchImage,
            angles.StartRadians,
            angles.ExtentRadians,
            parameters.ScaleMin,
            parameters.ScaleMax,
            parameters.CandidateMinScore,
            parameters.CandidateLimit,
            parameters.CandidateMaxOverlap,
            parameters.SubPixel,
            parameters.NumLevels,
            parameters.Greediness,
            out HTuple rows,
            out HTuple columns,
            out HTuple foundAngles,
            out HTuple scales,
            out HTuple scores);
        using (rows)
        using (columns)
        using (foundAngles)
        using (scales)
        using (scores)
        {
            double[] rowValues = rows.ToDArr();
            double[] columnValues = columns.ToDArr();
            double[] angleValues = foundAngles.ToDArr();
            double[] scaleValues = scales.ToDArr();
            double[] scoreValues = scores.ToDArr();
            if (columnValues.Length != rowValues.Length ||
                angleValues.Length != rowValues.Length ||
                scaleValues.Length != rowValues.Length ||
                scoreValues.Length != rowValues.Length)
            {
                throw new InvalidDataException(
                    "HALCON FindScaledShapeModel returned tuple arrays with different lengths.");
            }

            var candidates = new HalconNativeCandidate[rowValues.Length];
            for (var index = 0; index < candidates.Length; index++)
            {
                candidates[index] = new HalconNativeCandidate(
                    rowValues[index],
                    columnValues[index],
                    angleValues[index],
                    scaleValues[index],
                    scoreValues[index]);
            }

            return new HalconShapeModelFindResult(candidates);
        }
    }

    public void VerifyMatchingLicense()
    {
        const int width = 64;
        const int height = 64;
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)235);
        for (var row = 9; row <= 54; row++)
        {
            for (var column = 13; column <= 22; column++)
            {
                pixels[row * width + column] = 25;
            }
        }

        for (var row = 43; row <= 54; row++)
        {
            for (var column = 13; column <= 49; column++)
            {
                pixels[row * width + column] = 25;
            }
        }

        var runs = Enumerable.Range(4, 56)
            .Select(row => new HalconSupportRun(row, 4, 56))
            .ToArray();
        var request = new HalconShapeModelCreationRequest(
            new TightGray8Image(width, height, pixels),
            new HalconModelDomain(width, height, runs),
            new TemplateModelGenerationParameters(-5, 10, 0.95, 1.05, 0),
            31.5,
            31.5);
        using HShapeModel model = CreateShapeModel(request);
    }

    private static HShapeModel CreateShapeModel(HalconShapeModelCreationRequest request)
    {
        using HImage source = CreateCopiedImage(request.TemplateImage);
        using HRegion domain = CreateRegion(request.ModelDomain);
        using HImage templateImage = source.ReduceDomain(domain);
        var model = new HShapeModel();
        try
        {
            TemplateModelGenerationParameters parameters = request.GenerationParameters;
            using var numLevels = parameters.NumLevels == 0
                ? new HTuple("auto")
                : new HTuple(parameters.NumLevels);
            using var angleStep = new HTuple("auto");
            using var scaleStep = new HTuple("auto");
            using var optimization = new HTuple("auto");
            using var contrast = new HTuple("auto");
            using var minContrast = new HTuple("auto");
            HalconNativeAngleInterval nativeAngles =
                HalconAngleConvention.ToNativeInterval(parameters);
            model.CreateScaledShapeModel(
                templateImage,
                numLevels,
                nativeAngles.StartRadians,
                nativeAngles.ExtentRadians,
                angleStep,
                parameters.ScaleMin,
                parameters.ScaleMax,
                scaleStep,
                optimization,
                request.Metric,
                contrast,
                minContrast);
            using var borderName = new HTuple("border_shape_models");
            using var borderValue = new HTuple(request.AllowBorderShapeModels ? "true" : "false");
            model.SetShapeModelParam(borderName, borderValue);
            model.SetShapeModelOrigin(request.OriginRow, request.OriginColumn);
            return model;
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }

    private static HImage CreateCopiedImage(TightGray8Image image)
    {
        return image.UsePinnedPixels(
            pointer => new HImage(
                "byte",
                image.Width,
                image.Height,
                pointer));
    }

    private static HRegion CreateRegion(HalconModelDomain domain)
    {
        int[] rows = domain.Runs.Select(run => run.Row).ToArray();
        int[] starts = domain.Runs.Select(run => run.ColumnStart).ToArray();
        int[] ends = domain.Runs
            .Select(run => checked(run.ColumnStart + (run.Length - 1)))
            .ToArray();
        using var rowTuple = new HTuple(rows);
        using var startTuple = new HTuple(starts);
        using var endTuple = new HTuple(ends);
        var region = new HRegion();
        try
        {
            region.GenRegionRuns(rowTuple, startTuple, endTuple);
            return region;
        }
        catch
        {
            region.Dispose();
            throw;
        }
    }

    private static void ValidateLevelOneContours(HShapeModel model)
    {
        using HXLDCont contours = model.GetShapeModelContours(1);
        int count = contours.CountObj();
        if (count <= 0)
        {
            throw new InvalidDataException("The HALCON shape model contains no level-one contours.");
        }

        var samples = new List<HalconContourSamples>(count);
        for (var index = 1; index <= count; index++)
        {
            using HXLDCont contour = contours.SelectObj(index);
            contour.GetContourXld(out HTuple rows, out HTuple columns);
            using (rows)
            using (columns)
            {
                double[] rowValues = rows.ToDArr();
                double[] columnValues = columns.ToDArr();
                samples.Add(new HalconContourSamples(rowValues, columnValues));
            }
        }

        HalconModelHealthValidator.Validate(samples);
    }

    private sealed class HalconDotNetModelHandle(HShapeModel model) : IHalconRawModelHandle
    {
        private HShapeModel? _model = model ?? throw new ArgumentNullException(nameof(model));

        public HShapeModel BorrowModel()
        {
            return Volatile.Read(ref _model)
                ?? throw new ObjectDisposedException(nameof(HalconDotNetModelHandle));
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _model, null)?.Dispose();
        }
    }
}
