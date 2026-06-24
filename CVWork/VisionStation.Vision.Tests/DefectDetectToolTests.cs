using VisionStation.Domain;
using VisionStation.Vision.Tools;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class DefectDetectToolTests
{
    [Fact]
    public async Task RangeModeDetectsDarkBlobsAndSelectsLargest()
    {
        var frame = CreateGrayFrame(
            80,
            60,
            220,
            [new CircleBlob(20, 25, 5, 30), new CircleBlob(55, 35, 8, 30)]);

        var result = await RunBlobToolAsync(frame, BaseParameters());
        var blobs = BlobAnalysisResultCodec.ParseBlobs(result.Data.GetValueOrDefault("blobs"));

        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.Equal("2", result.Data["count"]);
        Assert.Equal(2, blobs.Count);
        Assert.InRange(blobs[0].X, 52, 58);
        Assert.InRange(blobs[0].Y, 32, 38);
        Assert.True(blobs[0].Area > blobs[1].Area);
    }

    [Fact]
    public async Task RoiMaskIgnoresBlobsOutsideSearchRegion()
    {
        var roi = new RoiDefinition
        {
            Id = "roi-1",
            Name = "Search",
            Shape = RoiShapeKind.Rectangle,
            X = 5,
            Y = 5,
            Width = 30,
            Height = 35
        };
        var parameters = BaseParameters();
        parameters["roiId"] = roi.Id;
        var frame = CreateGrayFrame(
            80,
            60,
            220,
            [new CircleBlob(20, 25, 6, 30), new CircleBlob(60, 25, 6, 30)]);

        var result = await RunBlobToolAsync(frame, parameters, roi);
        var blobs = BlobAnalysisResultCodec.ParseBlobs(result.Data.GetValueOrDefault("blobs"));

        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.Single(blobs);
        Assert.InRange(blobs[0].X, 17, 23);
        Assert.InRange(blobs[0].Y, 22, 28);
        Assert.Equal("Rectangle", result.Data["searchRoiShape"]);
    }

    [Fact]
    public async Task WidthAndHeightFiltersRejectOutOfRangeCandidates()
    {
        var parameters = BaseParameters();
        parameters["minWidth"] = "5";
        parameters["maxHeight"] = "12";
        var frame = CreateGrayFrame(
            80,
            60,
            220,
            [new RectangleBlob(8, 8, 3, 26, 30), new RectangleBlob(45, 20, 9, 9, 30)]);

        var result = await RunBlobToolAsync(frame, parameters);
        var blobs = BlobAnalysisResultCodec.ParseBlobs(result.Data.GetValueOrDefault("blobs"));

        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.Single(blobs);
        Assert.InRange(blobs[0].Width, 8, 10);
        Assert.InRange(blobs[0].Height, 8, 10);
        Assert.Equal("5-1000000", result.Data["criteriaWidth"]);
        Assert.Equal("0-12", result.Data["criteriaHeight"]);
    }

    [Fact]
    public async Task CountCriteriaReturnsNgWhenTooManyBlobsPass()
    {
        var parameters = BaseParameters();
        parameters["maxCount"] = "1";
        var frame = CreateGrayFrame(
            80,
            60,
            220,
            [new CircleBlob(20, 25, 5, 30), new CircleBlob(55, 35, 5, 30)]);

        var result = await RunBlobToolAsync(frame, parameters);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal("2", result.Data["count"]);
        Assert.Contains("要求 1-1 个", result.Message);
    }

    [Fact]
    public async Task BrightRangeDetectsBrightTargets()
    {
        var parameters = BaseParameters();
        parameters["grayMin"] = "200";
        parameters["grayMax"] = "255";
        parameters["polarity"] = "亮斑";
        var frame = CreateGrayFrame(
            80,
            60,
            25,
            [new CircleBlob(42, 30, 7, 245)]);

        var result = await RunBlobToolAsync(frame, parameters);
        var blobs = BlobAnalysisResultCodec.ParseBlobs(result.Data.GetValueOrDefault("blobs"));

        Assert.Equal(InspectionOutcome.Ok, result.Outcome);
        Assert.Single(blobs);
        Assert.InRange(blobs[0].X, 39, 45);
        Assert.InRange(blobs[0].Y, 27, 33);
    }

    [Fact]
    public void BlobResultCodecRoundTripsContourFields()
    {
        var source = new BlobAnalysisBlob(
            12.25,
            13.5,
            42,
            10,
            11,
            8,
            9,
            0.75,
            1.125,
            23,
            14,
            15,
            4.5,
            [new Point2D(10, 11), new Point2D(18, 11), new Point2D(18, 20)]);

        var encoded = BlobAnalysisResultCodec.FormatBlobs([source]);
        var parsed = BlobAnalysisResultCodec.ParseBlobs(encoded);

        Assert.Single(parsed);
        Assert.Equal(source.X, parsed[0].X);
        Assert.Equal(source.Circularity, parsed[0].Circularity);
        Assert.Equal(source.CircleRadius, parsed[0].CircleRadius);
        Assert.Equal(3, parsed[0].Contour.Count);
    }

    private static async Task<ToolResult> RunBlobToolAsync(
        ImageFrame frame,
        Dictionary<string, string> parameters,
        RoiDefinition? roi = null)
    {
        var sourceTool = new VisionToolDefinition
        {
            Id = "source",
            Name = "Source",
            Kind = VisionToolKind.AcquireImage
        };
        var definition = new VisionToolDefinition
        {
            Id = "blob",
            Name = "Blob",
            Kind = VisionToolKind.DefectDetect,
            Parameters = parameters
        };
        var recipe = new Recipe
        {
            Rois = roi is null ? Array.Empty<RoiDefinition>() : [roi],
            Tools = [sourceTool, definition]
        };
        using var context = new VisionToolContext(recipe, frame);
        context.SetImageOutput(sourceTool, frame);

        return await new DefectDetectTool().ExecuteAsync(definition, context);
    }

    private static Dictionary<string, string> BaseParameters()
    {
        var parameters = VisionToolDefaults.CreateBlobAnalysisParameters();
        parameters["inputImageToolId"] = "source";
        parameters["minArea"] = "15";
        parameters["morphOpen"] = "0";
        parameters["morphClose"] = "0";
        return parameters;
    }

    private static ImageFrame CreateGrayFrame(int width, int height, byte background, IReadOnlyList<SyntheticBlob> blobs)
    {
        var pixels = Enumerable.Repeat(background, width * height).ToArray();
        foreach (var blob in blobs)
        {
            blob.Paint(pixels, width, height);
        }

        return new ImageFrame(
            "test-frame",
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UtcNow,
            "Synthetic");
    }

    private abstract record SyntheticBlob(byte Value)
    {
        public abstract void Paint(byte[] pixels, int width, int height);

        protected static void SetPixel(byte[] pixels, int width, int height, int x, int y, byte value)
        {
            if (x >= 0 && y >= 0 && x < width && y < height)
            {
                pixels[y * width + x] = value;
            }
        }
    }

    private sealed record CircleBlob(int CenterX, int CenterY, int Radius, byte Value) : SyntheticBlob(Value)
    {
        public override void Paint(byte[] pixels, int width, int height)
        {
            var radiusSquared = Radius * Radius;
            for (var y = CenterY - Radius; y <= CenterY + Radius; y++)
            {
                for (var x = CenterX - Radius; x <= CenterX + Radius; x++)
                {
                    var dx = x - CenterX;
                    var dy = y - CenterY;
                    if (dx * dx + dy * dy <= radiusSquared)
                    {
                        SetPixel(pixels, width, height, x, y, Value);
                    }
                }
            }
        }
    }

    private sealed record RectangleBlob(int X, int Y, int Width, int Height, byte Value) : SyntheticBlob(Value)
    {
        public override void Paint(byte[] pixels, int width, int height)
        {
            for (var y = Y; y < Y + Height; y++)
            {
                for (var x = X; x < X + Width; x++)
                {
                    SetPixel(pixels, width, height, x, y, Value);
                }
            }
        }
    }
}
