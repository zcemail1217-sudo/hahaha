using System.Text.Json;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class Pose2DCompatibilityTests
{
    [Fact]
    public void LegacyPoseContractKeepsThreeValueConstructionAndDeconstruction()
    {
        var pose = new Pose2D(10, 20, 30);
        var (x, y, angle) = pose;

        Assert.Equal((10d, 20d, 30d, 1d), (x, y, angle, pose.Scale));
        Assert.Equal(1d, JsonSerializer.Deserialize<Pose2D>("{\"X\":10,\"Y\":20,\"Angle\":30}")!.Scale);
    }
}
