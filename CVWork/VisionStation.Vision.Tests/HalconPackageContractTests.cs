using HalconDotNet;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class HalconPackageContractTests
{
    [Fact]
    public void ManagedAssemblyAndTestHostArePinnedToApprovedVersions()
    {
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(
            new Version(26050, 0, 0, 0),
            typeof(HShapeModel).Assembly.GetName().Version);
    }
}
