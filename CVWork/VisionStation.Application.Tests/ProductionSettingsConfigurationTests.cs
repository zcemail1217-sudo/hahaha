using System.Text.Json;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class ProductionSettingsConfigurationTests
{
    [Fact]
    public async Task GetAsync_WhenLegacyConfigurationHasNoHalconNode_ReturnsEmptyHalconConfiguration()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "VisionStationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new RuntimePaths(baseDirectory);
            await File.WriteAllTextAsync(paths.DeviceConfigPath, "{}");
            var repository = new JsonDeviceConfigurationRepository(paths);

            var configuration = await repository.GetAsync();

            Assert.NotNull(configuration.SystemSettings.Halcon);
            Assert.Equal(string.Empty, configuration.SystemSettings.Halcon.RuntimeRoot);
        }
        finally
        {
            if (Directory.Exists(baseDirectory))
            {
                Directory.Delete(baseDirectory, true);
            }
        }
    }

    [Fact]
    public async Task GetAsync_WhenHalconNodeIsNull_ReturnsEmptyHalconConfiguration()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "VisionStationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new RuntimePaths(baseDirectory);
            await File.WriteAllTextAsync(
                paths.DeviceConfigPath,
                """
                {
                  "systemSettings": {
                    "halcon": null
                  }
                }
                """);
            var repository = new JsonDeviceConfigurationRepository(paths);

            var configuration = await repository.GetAsync();

            Assert.NotNull(configuration.SystemSettings.Halcon);
            Assert.Equal(string.Empty, configuration.SystemSettings.Halcon.RuntimeRoot);
        }
        finally
        {
            if (Directory.Exists(baseDirectory))
            {
                Directory.Delete(baseDirectory, true);
            }
        }
    }

    [Fact]
    public async Task GetAsync_WhenHalconRuntimeRootHasOuterWhitespace_ReturnsTrimmedPath()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "VisionStationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new RuntimePaths(baseDirectory);
            await File.WriteAllTextAsync(
                paths.DeviceConfigPath,
                """
                {
                  "systemSettings": {
                    "halcon": {
                      "runtimeRoot": "  C:\\MVTec\\HALCON-26.05-Progress  "
                    }
                  }
                }
                """);
            var repository = new JsonDeviceConfigurationRepository(paths);

            var configuration = await repository.GetAsync();

            Assert.Equal(@"C:\MVTec\HALCON-26.05-Progress", configuration.SystemSettings.Halcon.RuntimeRoot);
        }
        finally
        {
            if (Directory.Exists(baseDirectory))
            {
                Directory.Delete(baseDirectory, true);
            }
        }
    }

    [Fact]
    public async Task SaveAsync_WhenHalconRuntimeRootHasOuterWhitespace_PersistsTrimmedPath()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "VisionStationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new RuntimePaths(baseDirectory);
            var repository = new JsonDeviceConfigurationRepository(paths);

            await repository.SaveAsync(new DeviceConfiguration
            {
                SystemSettings = new SystemSettingsConfiguration
                {
                    Halcon = new HalconRuntimeConfiguration
                    {
                        RuntimeRoot = @"  C:\MVTec\HALCON-26.05-Progress  "
                    }
                }
            });

            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(paths.DeviceConfigPath));
            string? persistedRoot = json.RootElement
                .GetProperty("systemSettings")
                .GetProperty("halcon")
                .GetProperty("runtimeRoot")
                .GetString();
            Assert.Equal(@"C:\MVTec\HALCON-26.05-Progress", persistedRoot);
        }
        finally
        {
            if (Directory.Exists(baseDirectory))
            {
                Directory.Delete(baseDirectory, true);
            }
        }
    }

    [Fact]
    public async Task GetAsync_WhenProductionSettingsAreInvalid_NormalizesToSafeDefaults()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "VisionStationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new RuntimePaths(baseDirectory);
            var repository = new JsonDeviceConfigurationRepository(paths);

            await repository.SaveAsync(new DeviceConfiguration
            {
                SystemSettings = new SystemSettingsConfiguration
                {
                    Production = new ProductionSettingsConfiguration
                    {
                        CycleDelayMs = -1,
                        MaxConsecutiveFailures = 0,
                        CleanupTimeoutMs = 0,
                        AutoStopOnAlarm = false
                    },
                    Logging = new AppLoggingSettingsConfiguration
                    {
                        RetentionDays = 0,
                        MaxRecentEntries = -1,
                        IncludeThreadId = false
                    }
                }
            });

            var configuration = await repository.GetAsync();

            Assert.Equal(900, configuration.SystemSettings.Production.CycleDelayMs);
            Assert.Equal(1, configuration.SystemSettings.Production.MaxConsecutiveFailures);
            Assert.Equal(2000, configuration.SystemSettings.Production.CleanupTimeoutMs);
            Assert.False(configuration.SystemSettings.Production.AutoStopOnAlarm);
            Assert.Equal(30, configuration.SystemSettings.Logging.RetentionDays);
            Assert.Equal(300, configuration.SystemSettings.Logging.MaxRecentEntries);
            Assert.False(configuration.SystemSettings.Logging.IncludeThreadId);
        }
        finally
        {
            if (Directory.Exists(baseDirectory))
            {
                Directory.Delete(baseDirectory, true);
            }
        }
    }
}
