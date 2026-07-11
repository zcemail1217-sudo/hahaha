using VisionStation.Domain;
using VisionStation.Infrastructure;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class ProductionSettingsConfigurationTests
{
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
                        StopWaitTimeoutMs = 0,
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
            Assert.Equal(10000, configuration.SystemSettings.Production.StopWaitTimeoutMs);
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

    [Fact]
    public async Task GetAsync_WhenStopWaitTimeoutIsPositive_PreservesConfiguredValue()
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
                        StopWaitTimeoutMs = 12345
                    }
                }
            });

            var configuration = await repository.GetAsync();

            Assert.Equal(12345, configuration.SystemSettings.Production.StopWaitTimeoutMs);
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
