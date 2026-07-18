using System.Reflection;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class HalconTemplateMatchingFactoryTests
{
    [Fact]
    public async Task LegacyBackendsDoNotStartHalconProbeAndFactorySharesOneProbeForLearnAndMatch()
    {
        TemplateMatchingRuntime runtime = HalconTemplateMatchingFactory.Create(
            new UnusedTemplateModelStore(),
            new HalconRuntimeConfiguration(),
            NullTemplateMatchingDiagnosticSink.Instance);

        try
        {
            TemplateMatchingService service = Assert.IsType<TemplateMatchingService>(runtime.Service);
            IReadOnlyDictionary<TemplateMatchingEngine, ITemplateMatchingBackend> backends =
                ReadField<IReadOnlyDictionary<TemplateMatchingEngine, ITemplateMatchingBackend>>(
                    service,
                    "_backends");
            HalconTemplateMatchingBackend halcon = Assert.IsType<HalconTemplateMatchingBackend>(
                backends[TemplateMatchingEngine.Halcon]);
            IHalconRuntimeProbe matchProbe = ReadField<IHalconRuntimeProbe>(
                halcon,
                "_runtimeProbe");
            IHalconTemplateLearner learner = ReadField<IHalconTemplateLearner>(
                halcon,
                "_learner");
            IHalconRuntimeProbe learnProbe = ReadField<IHalconRuntimeProbe>(
                learner,
                "_runtimeProbe");
            Assert.Same(matchProbe, learnProbe);

            TemplateLearningResult openCv = await service.LearnAsync(
                LegacyLearningRequest("OpenCv"),
                default);
            TemplateLearningResult managed = await service.LearnAsync(
                LegacyLearningRequest("ManagedNcc"),
                default);

            Assert.True(openCv.Success, openCv.Diagnostic?.TechnicalDetails);
            Assert.True(managed.Success, managed.Diagnostic?.TechnicalDetails);
            Assert.Null(ReadField<object?>(matchProbe, "_sharedProbe"));
        }
        finally
        {
            await runtime.Service.DisposeAsync();
        }
    }

    private static TemplateLearningRequest LegacyLearningRequest(string engine)
    {
        return new TemplateLearningRequest(
            new TemplateModelOwner("recipe", "flow", "tool"),
            PatternFrame(),
            new RoiDefinition
            {
                Id = "template-roi",
                Shape = RoiShapeKind.Rectangle,
                X = 8,
                Y = 8,
                Width = 16,
                Height = 16
            },
            null,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TemplateMatchingParameterCatalog.Engine] = engine,
                [TemplateMatchingParameterCatalog.MatchMode] = "GrayNcc",
                ["minScore"] = "0.5",
                ["searchStep"] = "1"
            });
    }

    private static ImageFrame PatternFrame()
    {
        const int width = 40;
        const int height = 40;
        var pixels = Enumerable.Repeat((byte)230, width * height).ToArray();
        for (var y = 8; y < 24; y++)
        {
            for (var x = 8; x < 24; x++)
            {
                pixels[y * width + x] = (byte)(
                    (x + y) % 5 == 0 || x == 9 || y == 21
                        ? 20
                        : 170);
            }
        }

        return new ImageFrame(
            "factory-pattern",
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UnixEpoch,
            "test");
    }

    private static T ReadField<T>(object instance, string fieldName)
    {
        FieldInfo field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"Field '{fieldName}' was not found on '{instance.GetType().Name}'.");
        return (T)field.GetValue(instance)!;
    }

    private sealed class UnusedTemplateModelStore : ITemplateModelStore
    {
        public Task<TemplateModelWriteSession> BeginWriteAsync(
            TemplateModelOwner owner,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplateModelReference> CommitAsync(
            TemplateModelWriteSession session,
            ReadOnlyMemory<byte> metadataJson,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ResolvedTemplateModel> ResolveAsync(
            TemplateModelOwner owner,
            TemplateModelReference reference,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplateModelReference> CopyGenerationAsync(
            TemplateModelOwner sourceOwner,
            TemplateModelReference sourceReference,
            TemplateModelOwner targetOwner,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteGenerationAsync(
            TemplateModelOwner owner,
            TemplateModelReference reference,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DeleteOwnerResourcesAsync(
            TemplateModelOwner owner,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
