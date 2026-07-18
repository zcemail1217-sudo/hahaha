using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using VisionStation.Application;
using VisionStation.Application.Recipes;
using VisionStation.Client;
using VisionStation.Client.Services;
using VisionStation.Client.ViewModels;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;
using VisionStation.Vision.UI.Services;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class TemplateMatchingCompositionTests
{
    [Fact]
    public async Task CompositionCanCompleteOpenCvLearnAndMatchWithoutTouchingInvalidHalconRuntime()
    {
        string root = CreateTemporaryRoot();
        string missingHalconRoot = Path.Combine(root, "missing-halcon-runtime");
        var composition = new TemplateMatchingComposition(
            new RuntimePaths(root),
            new HalconRuntimeConfiguration
            {
                RuntimeRoot = missingHalconRoot
            },
            new NullAppLogService());

        try
        {
            var owner = new TemplateModelOwner("recipe", "flow", "tool");
            ImageFrame frame = CreatePatternFrame();
            var templateRoi = new RoiDefinition
            {
                Id = "template-roi",
                Shape = RoiShapeKind.Rectangle,
                X = 8,
                Y = 8,
                Width = 16,
                Height = 16
            };
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [TemplateMatchingParameterCatalog.Engine] = "OpenCv",
                [TemplateMatchingParameterCatalog.MatchMode] = "GrayNcc",
                ["minScore"] = "0.5",
                ["searchStep"] = "1"
            };

            TemplateLearningResult learned = await composition.Service.LearnAsync(
                new TemplateLearningRequest(owner, frame, templateRoi, null, parameters),
                default);
            TemplateMatchBatchResult matched = await composition.Service.MatchAsync(
                new TemplateMatchingRequest(
                    owner,
                    frame,
                    null,
                    learned.Parameters,
                    TemplateMatchCardinality.Single,
                    1),
                default);

            Assert.True(learned.Success, learned.Diagnostic?.TechnicalDetails ?? learned.Message);
            Assert.Equal(TemplateMatchingEngine.OpenCv, learned.Engine);
            Assert.NotEqual(TemplateMatchingDiagnosticCodes.RuntimeNotFound, learned.Diagnostic?.Code);
            Assert.True(matched.HasMatch, matched.Diagnostic?.TechnicalDetails ?? matched.Message);
            Assert.Equal(TemplateMatchingEngine.OpenCv, matched.Engine);
            Assert.NotEqual(TemplateMatchingDiagnosticCodes.RuntimeNotFound, matched.Diagnostic?.Code);
            Assert.Null(ReadHalconSharedProbe(composition.Service));
        }
        finally
        {
            await composition.Service.DisposeAsync();
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task CompositionExposesOnlyStableNeutralServicesAndOwnsOneServiceLifetime()
    {
        string root = CreateTemporaryRoot();
        var composition = new TemplateMatchingComposition(
            new RuntimePaths(root),
            new HalconRuntimeConfiguration(),
            new NullAppLogService());

        try
        {
            Assert.IsAssignableFrom<ITemplateMatchingService>(composition.Service);
            Assert.IsType<FileTemplateModelStore>(composition.Store);
            Assert.IsAssignableFrom<ITemplateModelResourceManager>(composition.Resources);
            Assert.Same(composition.Service, composition.Service);
            Assert.Same(composition.Store, composition.Store);
            Assert.Same(composition.Resources, composition.Resources);

            string[] publicProperties = typeof(TemplateMatchingRuntime)
                .GetProperties()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(["Resources", "Service"], publicProperties);
            Assert.Empty(typeof(TemplateMatchingRuntime).GetConstructors());
            Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(TemplateMatchingRuntime)));
            Assert.False(typeof(IAsyncDisposable).IsAssignableFrom(typeof(TemplateMatchingRuntime)));
        }
        finally
        {
            await composition.Service.DisposeAsync();
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void RecipeManagementDependsOnRecipeTemplateLifecycleInsteadOfFileStore()
    {
        Type[] parameterTypes = GetSingleConstructorParameterTypes(typeof(RecipeManagementViewModel));

        Assert.Contains(typeof(IRecipeTemplateLifecycleService), parameterTypes);
        Assert.DoesNotContain(typeof(FileTemplateModelStore), parameterTypes);
    }

    [Fact]
    public void RecipeManagementDependsOnInspectionRunLifetime()
    {
        Type inspectionRunLifetime = GetInspectionRunLifetimeType();
        Type[] parameterTypes = GetSingleConstructorParameterTypes(typeof(RecipeManagementViewModel));

        Assert.Contains(inspectionRunLifetime, parameterTypes);
    }

    [Fact]
    public void ProductionCoordinatorDependsOnInspectionRunLifetime()
    {
        Type inspectionRunLifetime = GetInspectionRunLifetimeType();
        Type[] parameterTypes = GetSingleConstructorParameterTypes(typeof(ProductionCoordinator));

        Assert.Contains(inspectionRunLifetime, parameterTypes);
    }

    [Fact]
    public void WpfToolParameterDialogDependsOnExactlyTheCompositionTemplatePorts()
    {
        Type[] compositionPorts = GetTemplatePortTypes(
            typeof(TemplateMatchingComposition)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(property => property.PropertyType));
        Type[] dialogPorts = GetTemplatePortTypes(
            GetSingleConstructorParameterTypes(typeof(WpfToolParameterDialogService)));

        Assert.Equal(
            [
                typeof(ITemplateMatchingService),
                typeof(ITemplateModelResourceManager),
                typeof(ITemplateModelStore)
            ],
            compositionPorts);
        Assert.Equal(compositionPorts, dialogPorts);
    }

    [Fact]
    public async Task AppShutdownPublishesOneTaskBeforeInspectionCancellationCanReenter()
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        using var startupCancellation = new CancellationTokenSource();
        var lifetime = new InspectionRunLifetime();
        var matching = new RecordingTemplateMatchingService();
        var communication = new RecordingCommunicationRuntime();
        SetPrivateField(app, "_shutdownSyncRoot", new object());
        SetPrivateField(app, "_startupCancellation", startupCancellation);
        SetPrivateField(app, "_inspectionRunLifetime", lifetime);
        SetPrivateField(app, "_templateMatchingService", matching);
        SetPrivateField(app, "_communicationRuntime", communication);

        Task? reentrantShutdown = null;
        var runStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeRun = lifetime.RunTrackedAsync<int>(async cancellationToken =>
        {
            using var registration = cancellationToken.Register(
                () => reentrantShutdown = app.ShutdownRuntimeAsync());
            runStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 1;
        });
        await runStarted.Task;

        Task firstShutdown = app.ShutdownRuntimeAsync();
        await firstShutdown;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activeRun);

        Assert.Same(firstShutdown, reentrantShutdown);
        Assert.Equal(1, matching.DisposeCount);
        Assert.Equal(1, communication.DisposeCount);
    }

    private static object? ReadHalconSharedProbe(ITemplateMatchingService service)
    {
        var concrete = Assert.IsType<TemplateMatchingService>(service);
        object backends = ReadPrivateField(concrete, "_backendsInDisposalOrder")
            ?? throw new InvalidOperationException("The matching backend collection is null.");
        object halconBackend = Assert.Single(
            Assert.IsAssignableFrom<IEnumerable>(backends)
                .Cast<object>(),
            backend => Equals(
                    backend.GetType().GetProperty("Engine")?.GetValue(backend),
                    TemplateMatchingEngine.Halcon));
        object runtimeProbe = ReadPrivateField(halconBackend, "_runtimeProbe")
            ?? throw new InvalidOperationException("The HALCON runtime probe is null.");
        return ReadPrivateField(runtimeProbe, "_sharedProbe");
    }

    private static object? ReadPrivateField(object instance, string fieldName)
    {
        FieldInfo field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"Field '{fieldName}' was not found on '{instance.GetType().Name}'.");
        return field.GetValue(instance);
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        FieldInfo field = instance.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"Field '{fieldName}' was not found on '{instance.GetType().Name}'.");
        field.SetValue(instance, value);
    }

    private static Type GetInspectionRunLifetimeType()
    {
        Type? lifetimeType = typeof(ProductionCoordinator).Assembly
            .GetTypes()
            .SingleOrDefault(type => type.Name == "IInspectionRunLifetime");
        Assert.True(
            lifetimeType is not null,
            "VisionStation.Application must expose the shared IInspectionRunLifetime port.");
        Assert.True(lifetimeType.IsInterface, "IInspectionRunLifetime must remain a neutral interface port.");
        return lifetimeType;
    }

    private static Type[] GetSingleConstructorParameterTypes(Type type)
    {
        return Assert.Single(type.GetConstructors())
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();
    }

    private static Type[] GetTemplatePortTypes(IEnumerable<Type> types)
    {
        return types
            .Where(IsTemplatePortType)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsTemplatePortType(Type type)
    {
        return typeof(ITemplateMatchingService).IsAssignableFrom(type) ||
               typeof(ITemplateModelStore).IsAssignableFrom(type) ||
               typeof(ITemplateModelResourceManager).IsAssignableFrom(type) ||
               type.Name.Contains("TemplateMatching", StringComparison.Ordinal) ||
               type.Name.Contains("TemplateModel", StringComparison.Ordinal);
    }

    private static string CreateTemporaryRoot()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "VisionStation-TemplateMatchingCompositionTests",
            Guid.NewGuid().ToString("N"));
    }

    private static ImageFrame CreatePatternFrame()
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
            "composition-pattern",
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UnixEpoch,
            "test");
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class NullAppLogService : IAppLogService
    {
        public event EventHandler<AppLogEntry>? LogWritten
        {
            add { }
            remove { }
        }

        public void Info(string source, string message)
        {
        }

        public void Warning(string source, string message)
        {
        }

        public void Error(string source, string message)
        {
        }

        public void Critical(string source, string message)
        {
        }

        public IReadOnlyList<AppLogEntry> Recent(int count)
        {
            return Array.Empty<AppLogEntry>();
        }
    }

    private sealed class RecordingTemplateMatchingService : ITemplateMatchingService
    {
        public int DisposeCount { get; private set; }

        public Task<TemplateLearningResult> LearnAsync(
            TemplateLearningRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<TemplateMatchBatchResult> MatchAsync(
            TemplateMatchingRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCommunicationRuntime : ICommunicationChannelRuntime
    {
        public event EventHandler<CommunicationChannelRuntimeFrame>? FrameReceived
        {
            add { }
            remove { }
        }

        public int DisposeCount { get; private set; }

        public Task ConnectAsync(
            string connectionPolicy,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DisconnectAsync(
            string connectionPolicy,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CommunicationChannelRuntimeSnapshot> GetTcpSnapshotAsync(
            TcpCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CommunicationChannelRuntimeSnapshot> GetSerialSnapshotAsync(
            SerialCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CommunicationChannelRuntimeSnapshot> ReconnectTcpAsync(
            TcpCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CommunicationChannelRuntimeSnapshot> ReconnectSerialAsync(
            SerialCommunicationChannelSettings channel,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<byte[]?> TryExchangeTcpAsync(
            TcpCommunicationChannelSettings channel,
            byte[] payload,
            int timeoutMs,
            bool waitResponse,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> TrySendTcpAsync(
            TcpCommunicationChannelSettings channel,
            byte[] payload,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<byte[]?> TryExchangeSerialAsync(
            SerialCommunicationChannelSettings channel,
            byte[] payload,
            int timeoutMs,
            bool waitResponse,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> TrySendSerialAsync(
            SerialCommunicationChannelSettings channel,
            byte[] payload,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
