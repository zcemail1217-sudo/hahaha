using VisionStation.Domain;
using VisionStation.Vision.UI.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class CalibrationViewModelTests
{
    [Theory]
    [InlineData(PlaneCalibrationModel.Homography, 3, false, "Homography 至少需要 4 个有效点")]
    [InlineData(PlaneCalibrationModel.Affine, 3, true, "")]
    [InlineData(PlaneCalibrationModel.Auto, 2, false, "至少需要 3 个有效点")]
    public void CanCalculate_ValidatesRequiredPointCount(
        PlaneCalibrationModel model,
        int pointCount,
        bool expectedResult,
        string expectedMessage)
    {
        var canCalculate = CalibrationPlaneValidation.CanCalculate(model, pointCount, out var message);

        Assert.Equal(expectedResult, canCalculate);
        Assert.Equal(expectedMessage, message);
    }

    [Theory]
    [InlineData(0.05, CalibrationQualityLevel.Good, "可用")]
    [InlineData(0.0501, CalibrationQualityLevel.Warning, "警告")]
    [InlineData(0.2, CalibrationQualityLevel.Warning, "警告")]
    [InlineData(0.2001, CalibrationQualityLevel.Bad, "不建议保存")]
    public void FromPlane_MapsRmsToQualityLevel(double rms, CalibrationQualityLevel expectedLevel, string expectedLabel)
    {
        var result = CreatePlaneResult(rms, maxError: rms);

        var summary = CalibrationQualitySummary.FromPlane(result);

        Assert.Equal(expectedLevel, summary.Level);
        Assert.Equal(expectedLabel, summary.Label);
    }

    [Theory]
    [InlineData(0.5, CalibrationQualityLevel.Good, "可用")]
    [InlineData(0.5001, CalibrationQualityLevel.Warning, "警告")]
    [InlineData(1.0, CalibrationQualityLevel.Warning, "警告")]
    [InlineData(1.0001, CalibrationQualityLevel.Bad, "不建议保存")]
    public void FromCamera_MapsRmsToQualityLevel(double rms, CalibrationQualityLevel expectedLevel, string expectedLabel)
    {
        var result = CreateCameraResult(rms);

        var summary = CalibrationQualitySummary.FromCamera(result);

        Assert.Equal(expectedLevel, summary.Level);
        Assert.Equal(expectedLabel, summary.Label);
    }

    [Fact]
    public void FromPlane_BuildsSummaryAndDerivesMaxErrorPointIndex()
    {
        var result = CreatePlaneResult(
            rms: 0.1234,
            maxError: 0.31,
            pointCount: 9,
            inlierCount: 8,
            pointErrors:
            [
                new PlaneCalibrationPointError { Error = 0.12 },
                new PlaneCalibrationPointError { Error = 0.31 },
                new PlaneCalibrationPointError { Error = 0.08 }
            ]);

        var summary = CalibrationQualitySummary.FromPlane(result);

        Assert.Contains("RMS=0.123 mm", summary.Details);
        Assert.Contains("Max=0.31 mm", summary.Details);
        Assert.Contains("点数=9", summary.Details);
        Assert.Contains("内点=8", summary.Details);
        Assert.Equal("最大误差点：#2 (0.31 mm)", summary.MaxErrorPointText);
    }

    [Fact]
    public void FromCamera_BuildsSummaryWithRmsImageSizeAndViewCount()
    {
        var result = CreateCameraResult(rms: 0.876, imageWidth: 1280, imageHeight: 960, viewCount: 4);

        var summary = CalibrationQualitySummary.FromCamera(result);

        Assert.Contains("RMS=0.876 px", summary.Details);
        Assert.Contains("图像=1280x960", summary.Details);
        Assert.Contains("视图=4", summary.Details);
    }

    [Fact]
    public void Empty_UsesCustomDetails()
    {
        var summary = CalibrationQualitySummary.Empty("Homography 至少需要 4 个有效点");

        Assert.Equal(CalibrationQualityLevel.None, summary.Level);
        Assert.Equal("-", summary.Label);
        Assert.Equal("Homography 至少需要 4 个有效点", summary.Details);
        Assert.Equal("-", summary.MaxErrorPointText);
    }

    private static PlaneCalibrationResult CreatePlaneResult(
        double rms,
        double maxError,
        int pointCount = 9,
        int inlierCount = 9,
        IReadOnlyList<PlaneCalibrationPointError>? pointErrors = null)
    {
        return new PlaneCalibrationResult
        {
            RmsError = rms,
            MaxError = maxError,
            PointCount = pointCount,
            InlierCount = inlierCount,
            Unit = "mm",
            PointErrors = pointErrors ?? []
        };
    }

    private static CameraCalibrationResult CreateCameraResult(
        double rms,
        int imageWidth = 2448,
        int imageHeight = 2048,
        int viewCount = 3)
    {
        return new CameraCalibrationResult
        {
            RmsReprojectionError = rms,
            ImageWidth = imageWidth,
            ImageHeight = imageHeight,
            Views = Enumerable.Range(1, viewCount)
                .Select(index => new CameraCalibrationViewResult { FrameId = $"view-{index}" })
                .ToArray()
        };
    }
}
