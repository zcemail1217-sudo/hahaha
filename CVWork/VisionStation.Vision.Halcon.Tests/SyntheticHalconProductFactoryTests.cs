using Xunit;

namespace VisionStation.Vision.Halcon.Tests;

public sealed class SyntheticHalconProductFactoryTests
{
    private const string ConfiguredRootVariable = "VISIONSTATION_HALCON_ROOT";
    private const string HalconRootVariable = "HALCONROOT";

    [Fact]
    public void RuntimeConfigurationWithoutExplicitRootDoesNotUseDeveloperMachinePath()
    {
        string? originalConfiguredRoot = Environment.GetEnvironmentVariable(ConfiguredRootVariable);
        string? originalHalconRoot = Environment.GetEnvironmentVariable(HalconRootVariable);
        try
        {
            Environment.SetEnvironmentVariable(ConfiguredRootVariable, null);
            Environment.SetEnvironmentVariable(HalconRootVariable, null);

            Assert.Equal(
                string.Empty,
                SyntheticHalconProductFactory.CreateRuntimeConfiguration().RuntimeRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfiguredRootVariable, originalConfiguredRoot);
            Environment.SetEnvironmentVariable(HalconRootVariable, originalHalconRoot);
        }
    }

    [Fact]
    public void DeleteWorkingDirectoryRejectsDirectoryOutsideOwnedRoot()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "VisionStation-HalconFactoryGuardTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            Assert.Throws<InvalidOperationException>(
                () => SyntheticHalconProductFactory.DeleteWorkingDirectory(path));
            Assert.True(Directory.Exists(path));
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    [Fact]
    public void DeleteWorkingDirectoryRemovesFactoryOwnedDirectory()
    {
        string path = SyntheticHalconProductFactory.CreateWorkingDirectory();
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "owned.txt"), "owned");

        SyntheticHalconProductFactory.DeleteWorkingDirectory(path);

        Assert.False(Directory.Exists(path));
    }
}
