using System.IO;
using VisionStation.Client.Services;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class TemplateMatchingCompositionTests
{
    [Fact]
    public async Task CompositionExposesOnlyStableNeutralServicesAndOwnsOneServiceLifetime()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "VisionStation-TemplateMatchingCompositionTests",
            Guid.NewGuid().ToString("N"));
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
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
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
}
