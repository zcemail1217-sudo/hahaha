using VisionStation.Vision.UI.Models;
using VisionStation.Vision.UI.Services;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class VisionResultOverlayProjectorTests
{
    [Fact]
    public void ProjectKeepsInfoRoiAndLocateLabelButHidesConfigurationRoi()
    {
        var locateLabel = "匹配 S=0.923 C=0.887";
        var overlays = new[]
        {
            new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Rectangle,
                State = VisionOverlayState.Neutral,
                Label = "配置 ROI"
            },
            new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Polyline,
                State = VisionOverlayState.Info,
                Label = "模板 ROI"
            },
            new VisionOverlayItem
            {
                Kind = VisionOverlayKind.Cross,
                State = VisionOverlayState.Ok,
                Label = locateLabel,
                PreserveLabelInResult = true
            },
            new VisionOverlayItem
            {
                Kind = VisionOverlayKind.DirectionAxis,
                State = VisionOverlayState.Ok,
                Label = "方向"
            }
        };

        var result = VisionResultOverlayProjector.Project(overlays);

        Assert.DoesNotContain(result, item => item.State == VisionOverlayState.Neutral);
        Assert.DoesNotContain(result, item => item.Kind == VisionOverlayKind.DirectionAxis);

        var info = Assert.Single(result, item => item.State == VisionOverlayState.Info);
        Assert.Empty(info.Label);

        var cross = Assert.Single(result, item => item.Kind == VisionOverlayKind.Cross);
        Assert.Equal(locateLabel, cross.Label);
        Assert.All(result.Where(item => item.Kind != VisionOverlayKind.Cross), item => Assert.Empty(item.Label));
    }

    [Fact]
    public void ProjectClearsLabelFromUnmarkedCross()
    {
        var overlay = new VisionOverlayItem
        {
            Kind = VisionOverlayKind.Cross,
            State = VisionOverlayState.Ok,
            Label = "交点"
        };

        var result = VisionResultOverlayProjector.Project([overlay]);

        var cross = Assert.Single(result);
        Assert.Empty(cross.Label);
    }
}
