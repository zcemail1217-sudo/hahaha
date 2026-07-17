using System.Diagnostics;
using VisionStation.Domain;

namespace VisionStation.Vision.Tools;

public sealed class MultiTargetMatchTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.MultiTargetMatch;

    public Task<ToolResult> ExecuteAsync(VisionToolDefinition definition, VisionToolContext context, CancellationToken cancellationToken = default)
    {
        RemoveOutputs(context, definition);
        var stopwatch = Stopwatch.StartNew();
        if (!context.TryGetInputImage(definition, out var frame))
        {
            stopwatch.Stop();
            return Task.FromResult(GeometryToolSupport.CreateMissingImageInputResult(definition, Kind, stopwatch.Elapsed));
        }

        var sourceRoi = GeometryToolSupport.FindBoundRoi(context.Recipe, definition);
        RoiDefinition? roi = sourceRoi;
        if (sourceRoi is null &&
            !GeometryToolSupport.TryValidatePositionInputMapping(context, definition, out var missingRoiMappingFailure))
        {
            stopwatch.Stop();
            return Task.FromResult(GeometryToolSupport.CreatePositionInputMappingFailureResult(
                definition,
                Kind,
                stopwatch.Elapsed,
                frame,
                missingRoiMappingFailure!));
        }

        if (sourceRoi is not null &&
            !GeometryToolSupport.TryMapRoiForPositionInput(
                context,
                definition,
                sourceRoi,
                out roi,
                out var mappingFailure))
        {
            stopwatch.Stop();
            return Task.FromResult(GeometryToolSupport.CreatePositionInputMappingFailureResult(
                definition,
                Kind,
                stopwatch.Elapsed,
                frame,
                mappingFailure!));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var result = MultiTargetMatcher.Match(frame, roi, definition.Parameters, context.GetGrayMat(frame), cancellationToken);
        stopwatch.Stop();
        cancellationToken.ThrowIfCancellationRequested();

        var matches = result.Matches;
        var best = matches.FirstOrDefault();
        if (best is not null)
        {
            var bestPose = best.Pose;
            context.Properties["pose"] = bestPose;
            context.SetPortOutput(definition, "PositionOutput", bestPose);
            context.SetPortOutput(definition, "OriginOutput", bestPose);
            context.SetPortOutput(definition, "BestPositionOutput", bestPose);
            context.SetPortOutput(definition, "ScoreOutput", best.Score);
            context.SetPortOutput(definition, "XOutput", best.X);
            context.SetPortOutput(definition, "YOutput", best.Y);
            context.SetPortOutput(definition, "AngleOutput", best.Angle);
        }

        context.SetPortOutput(definition, "CountOutput", matches.Count);
        context.SetPortOutput(
            definition,
            "AllPositionsOutput",
            matches.Select(match => match.Pose).ToArray());
        context.SetPortOutput(definition, "ScoresOutput", matches.Select(match => match.Score).ToArray());

        var data = new Dictionary<string, string>
        {
            ["count"] = matches.Count.ToString(),
            ["score"] = best?.Score.ToInvariant() ?? "0",
            ["bestScore"] = best?.Score.ToInvariant() ?? "0",
            ["x"] = best?.X.ToInvariant() ?? "0",
            ["y"] = best?.Y.ToInvariant() ?? "0",
            ["angle"] = best?.Angle.ToInvariant() ?? "0",
            ["scale"] = best?.Scale.ToRoundTripScaleInvariant() ?? "1",
            ["inputFrameId"] = frame.Id,
            ["templateWidth"] = best?.Width.ToString() ?? definition.Parameters.GetValueOrDefault("templateWidth") ?? "0",
            ["templateHeight"] = best?.Height.ToString() ?? definition.Parameters.GetValueOrDefault("templateHeight") ?? "0",
            ["searchX"] = result.SearchRegion.X.ToString(),
            ["searchY"] = result.SearchRegion.Y.ToString(),
            ["searchWidth"] = result.SearchRegion.Width.ToString(),
            ["searchHeight"] = result.SearchRegion.Height.ToString(),
            ["autoTemplate"] = result.UsedAutoTemplate.ToString(),
            ["engine"] = definition.Parameters.GetValueOrDefault("engine") ?? "OpenCv",
            ["matchMode"] = definition.Parameters.GetValueOrDefault("matchMode") ?? definition.Parameters.GetValueOrDefault("multiMatchMode") ?? "Shape",
            ["matches"] = FormatMatches(matches)
        };

        if (roi is not null)
        {
            GeometryToolSupport.AddSearchRoiData(data, roi);
        }

        return Task.FromResult(new ToolResult
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Kind = Kind,
            Outcome = result.Outcome,
            Duration = stopwatch.Elapsed,
            Message = result.Message,
            Data = data
        });
    }

    private static void RemoveOutputs(VisionToolContext context, VisionToolDefinition definition)
    {
        context.Properties.Remove("pose");
        context.RemovePortOutput(definition, "PositionOutput");
        context.RemovePortOutput(definition, "OriginOutput");
        context.RemovePortOutput(definition, "BestPositionOutput");
        context.RemovePortOutput(definition, "ScoreOutput");
        context.RemovePortOutput(definition, "XOutput");
        context.RemovePortOutput(definition, "YOutput");
        context.RemovePortOutput(definition, "AngleOutput");
        context.RemovePortOutput(definition, "CountOutput");
        context.RemovePortOutput(definition, "AllPositionsOutput");
        context.RemovePortOutput(definition, "ScoresOutput");
    }

    private static string FormatMatches(IReadOnlyList<MultiTargetMatchCandidate> matches)
    {
        return string.Join(
            ";",
            matches.Select(match =>
                $"{match.X.ToInvariant()},{match.Y.ToInvariant()},{match.Angle.ToInvariant()},{match.Score.ToInvariant()},{match.Width},{match.Height},{match.Shape},{match.Radius.ToInvariant()}"));
    }
}
