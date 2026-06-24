using VisionStation.Domain;
using VisionStation.Infrastructure;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class FileAppLogServiceTests
{
    [Fact]
    public void Write_AppendsLogFileAndKeepsRecentEntriesBounded()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "VisionStationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new RuntimePaths(baseDirectory);
            var service = new FileAppLogService(
                paths,
                new AppLoggingSettingsConfiguration
                {
                    MaxRecentEntries = 2,
                    RetentionDays = 30,
                    IncludeThreadId = false
                });

            service.Info("System", "first");
            service.Warning("System", "second");
            service.Error("System", "third\nnext-line");

            var recent = service.Recent(10);
            Assert.Equal(2, recent.Count);
            Assert.Equal("third\nnext-line", recent[0].Message);
            Assert.Equal("second", recent[1].Message);

            var logFile = Path.Combine(paths.LogDirectory, $"{DateTimeOffset.Now:yyyyMMdd}.log");
            var text = File.ReadAllText(logFile);
            Assert.Contains("[INFO] System first", text);
            Assert.Contains("[ERROR] System third", text);
            Assert.Contains("    next-line", text);
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
    public void Constructor_RemovesExpiredDailyLogFiles()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "VisionStationTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new RuntimePaths(baseDirectory);
            var oldFile = Path.Combine(paths.LogDirectory, $"{DateTimeOffset.Now.AddDays(-10):yyyyMMdd}.log");
            File.WriteAllText(oldFile, "old");

            _ = new FileAppLogService(
                paths,
                new AppLoggingSettingsConfiguration
                {
                    RetentionDays = 1,
                    MaxRecentEntries = 300
                });

            Assert.False(File.Exists(oldFile));
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
