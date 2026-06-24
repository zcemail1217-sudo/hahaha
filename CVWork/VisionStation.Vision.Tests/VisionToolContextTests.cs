using VisionStation.Domain;
using VisionStation.Vision;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class VisionToolContextTests
{
    [Fact]
    public void GetGrayMatReusesConversionForSameFrame()
    {
        var frame = CreateBgraFrame();
        using var context = new VisionToolContext(new Recipe(), frame);

        var first = context.GetGrayMat(frame);
        var second = context.GetGrayMat(frame);

        Assert.Same(first, second);
        Assert.Equal(frame.Width, first.Width);
        Assert.Equal(frame.Height, first.Height);
    }

    [Fact]
    public void ResolveTextTokensUsesRecipeParametersAndVariables()
    {
        var frame = CreateBgraFrame();
        var recipe = new Recipe
        {
            ProductCode = "P-24",
            ProductParameters =
            [
                new ProductParameterDefinition { Id = "param-lot", Name = "LotNo", Value = "L001" }
            ],
            Variables =
            [
                new RecipeVariableDefinition { Key = "Shift", Name = "班次", CurrentValue = "Day" }
            ]
        };
        using var context = new VisionToolContext(recipe, frame);

        var text = context.ResolveTextTokens("MODEL=${ProductCode};LOT=${LotNo};SHIFT={{Shift}}");

        Assert.Equal("MODEL=P-24;LOT=L001;SHIFT=Day", text);
    }

    [Fact]
    public void ResolveTextTokensUsesPreviousToolResultData()
    {
        var frame = CreateBgraFrame();
        using var context = new VisionToolContext(new Recipe(), frame);
        context.ToolResults.Add(new ToolResult
        {
            ToolId = "locate-1",
            ToolName = "定位",
            Kind = VisionToolKind.TemplateLocate,
            Outcome = InspectionOutcome.Ok,
            Data = new Dictionary<string, string>
            {
                ["x"] = "12.34",
                ["y"] = "56.78"
            }
        });

        var text = context.ResolveTextTokens("X=${定位.x};Y=${locate-1:y};OK=${TemplateLocate.Outcome}");

        Assert.Equal("X=12.34;Y=56.78;OK=Ok", text);
    }

    [Fact]
    public void ResolveTextTokensUsesPortOutputs()
    {
        var frame = CreateBgraFrame();
        using var context = new VisionToolContext(new Recipe(), frame);
        var tool = new VisionToolDefinition
        {
            Id = "tcp-1",
            Name = "TCP通讯",
            Kind = VisionToolKind.TcpCommunication
        };
        context.SetPortOutput(tool, "ResponseOutput", "ACK-001");
        context.ToolResults.Add(new ToolResult
        {
            ToolId = tool.Id,
            ToolName = tool.Name,
            Kind = tool.Kind,
            Outcome = InspectionOutcome.Ok
        });

        var text = context.ResolveTextTokens("RESP=${TCP通讯.ResponseOutput}");

        Assert.Equal("RESP=ACK-001", text);
    }

    private static ImageFrame CreateBgraFrame()
    {
        var width = 2;
        var height = 1;
        var stride = width * 4;
        byte[] pixels =
        [
            0, 0, 255, 255,
            0, 255, 0, 255
        ];

        return new ImageFrame(
            "frame-1",
            width,
            height,
            stride,
            PixelFormatKind.Bgra32,
            pixels,
            DateTimeOffset.UtcNow,
            "test");
    }
}
