using OpenCvSharp;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;
using VisionStation.Vision.Halcon.TestHost;
using Xunit;
using Xunit.Abstractions;

namespace VisionStation.Vision.Halcon.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalHalconDatasetCollection
{
    public const string Name = "Local HALCON Dataset";
}

public sealed class LocalHalconDatasetFactAttribute : FactAttribute
{
    private const string DatasetEnvironmentVariable = "VISIONSTATION_HALCON_DATASET";

    public LocalHalconDatasetFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(DatasetEnvironmentVariable) is null)
        {
            Skip = $"Set {DatasetEnvironmentVariable} to run the local HALCON dataset acceptance test.";
        }
    }
}

[Collection(LocalHalconDatasetCollection.Name)]
public sealed class LocalHalconDatasetAcceptanceTests
{
    private const string DatasetEnvironmentVariable = "VISIONSTATION_HALCON_DATASET";

    private static readonly TemplateModelOwner Owner =
        new("local-dataset-recipe", "acceptance-flow", "whole-product-match");

    private readonly ITestOutputHelper _output;

    public LocalHalconDatasetAcceptanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [LocalHalconDatasetFact]
    [Trait("Category", "LocalDataset")]
    public async Task LicensedHalcon_AcceptsEveryFrontAndRejectsEveryNegativeCase()
    {
        string datasetRoot = Environment.GetEnvironmentVariable(DatasetEnvironmentVariable)!;
        Assert.True(
            Directory.Exists(datasetRoot),
            $"{DatasetEnvironmentVariable} is set but the directory does not exist: '{datasetRoot}'.");
        LocalHalconDatasetManifest manifest = LocalHalconDatasetManifest.Load(datasetRoot);
        ImageFrame templateFrame = LoadGray8(manifest.Template.ImagePath);
        RoiDefinition templateRoi = CreateTemplateRoi(manifest.Template, templateFrame);
        string workingDirectory = SyntheticHalconProductFactory.CreateWorkingDirectory();
        try
        {
            var paths = new RuntimePaths(workingDirectory);
            TemplateMatchingRuntime runtime = HalconTemplateMatchingFactory.Create(
                new FileTemplateModelStore(paths),
                SyntheticHalconProductFactory.CreateRuntimeConfiguration(),
                LocalDatasetDiagnosticSink.Instance);
            try
            {
                Dictionary<string, string> parameters =
                    TemplateMatchingParameterCatalog.CreateStrictDefaults(
                        TemplateMatchCardinality.ExactCount);
                TemplateLearningResult learning = await runtime.Service.LearnAsync(
                    new TemplateLearningRequest(
                        Owner,
                        templateFrame,
                        templateRoi,
                        SearchRoi: null,
                        parameters),
                    CancellationToken.None);
                Assert.True(learning.Success, DescribeLearningFailure(learning));
                Assert.Equal(TemplateMatchingEngine.Halcon, learning.Engine);

                var misses = new List<string>();
                var falseAcceptances = new List<string>();
                foreach (LocalHalconDatasetCase datasetCase in manifest.Cases)
                {
                    ImageFrame frame = LoadGray8(datasetCase.ImagePath);
                    TemplateMatchBatchResult result = await runtime.Service.MatchAsync(
                        new TemplateMatchingRequest(
                            Owner,
                            frame,
                            SearchRoi: null,
                            learning.Parameters,
                            TemplateMatchCardinality.ExactCount,
                            ExpectedCount: 1),
                        CancellationToken.None);
                    string caseSummary = FormatCaseResult(datasetCase, result);
                    _output.WriteLine(caseSummary);

                    if (datasetCase.Label == LocalHalconDatasetLabel.PositiveFront)
                    {
                        if (!result.HasMatch ||
                            result.Outcome != InspectionOutcome.Ok ||
                            result.Matches.Count != 1)
                        {
                            misses.Add(caseSummary);
                        }

                        continue;
                    }

                    if (result.HasMatch || result.Matches.Count != 0)
                    {
                        falseAcceptances.Add(caseSummary);
                    }
                }

                _output.WriteLine(
                    $"SUMMARY cases={manifest.Cases.Count} " +
                    $"falseAcceptances={falseAcceptances.Count} misses={misses.Count}");

                Assert.True(
                    falseAcceptances.Count == 0,
                    "Negative HALCON dataset cases must have zero accepted matches:" +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, falseAcceptances));
                Assert.True(
                    misses.Count == 0,
                    "Positive/front HALCON dataset cases were missed:" +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, misses));
            }
            finally
            {
                await runtime.Service.DisposeAsync();
            }
        }
        finally
        {
            SyntheticHalconProductFactory.DeleteWorkingDirectory(workingDirectory);
        }
    }

    private static ImageFrame LoadGray8(string path)
    {
        using Mat gray = Cv2.ImRead(path, ImreadModes.Grayscale);
        if (gray.Empty())
        {
            throw new InvalidDataException(
                $"Unable to decode local HALCON dataset image '{Path.GetFileName(path)}'.");
        }

        using Mat tight = gray.Clone();
        tight.GetArray(out byte[] pixels);
        int expectedLength = checked(tight.Width * tight.Height);
        if (pixels.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"Local HALCON dataset image '{Path.GetFileName(path)}' did not decode to a tight Gray8 buffer. " +
                $"ExpectedBytes={expectedLength}; ActualBytes={pixels.Length}.");
        }

        return new ImageFrame(
            Guid.NewGuid().ToString("N"),
            tight.Width,
            tight.Height,
            tight.Width,
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UtcNow,
            path);
    }

    private static RoiDefinition CreateTemplateRoi(
        LocalHalconDatasetTemplate template,
        ImageFrame frame)
    {
        LocalHalconDatasetRoi roi = template.Roi;
        Assert.True(
            roi.X + roi.Width <= frame.Width && roi.Y + roi.Height <= frame.Height,
            $"Manifest template ROI ({roi.X:R},{roi.Y:R},{roi.Width:R},{roi.Height:R}) " +
            $"is outside template image {frame.Width}x{frame.Height}.");
        return new RoiDefinition
        {
            Id = "local-dataset-template",
            Name = "Local dataset whole product",
            Shape = RoiShapeKind.Rectangle,
            X = roi.X,
            Y = roi.Y,
            Width = roi.Width,
            Height = roi.Height
        };
    }

    private static string FormatCaseResult(
        LocalHalconDatasetCase datasetCase,
        TemplateMatchBatchResult result)
    {
        return $"CASE id={datasetCase.Id} label={datasetCase.CanonicalLabel} " +
               $"image={datasetCase.RelativeImagePath} accepted={result.HasMatch} " +
               $"outcome={result.Outcome} candidates={result.Matches.Count} " +
               $"code={result.Diagnostic?.Code ?? "<none>"}";
    }

    private static string DescribeLearningFailure(TemplateLearningResult result)
    {
        return $"Local HALCON dataset learning failed. Engine={result.Engine}; " +
               $"Message={result.Message}; Code={result.Diagnostic?.Code ?? "<none>"}; " +
               $"Stage={result.Diagnostic?.FailureStage ?? "<none>"}; " +
               $"Technical={result.Diagnostic?.TechnicalDetails ?? "<none>"}.";
    }

    private sealed class LocalDatasetDiagnosticSink : ITemplateMatchingDiagnosticSink
    {
        public static LocalDatasetDiagnosticSink Instance { get; } = new();

        public void Warning(string source, string message)
        {
        }

        public void Error(string source, string message)
        {
        }
    }
}
