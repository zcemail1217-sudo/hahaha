using System.Diagnostics;
using VisionStation.Domain;

namespace VisionStation.Vision.Tools;

public sealed class MultiTargetMatchTool : IVisionTool
{
    public VisionToolKind Kind => VisionToolKind.MultiTargetMatch;

    public Task<ToolResult> ExecuteAsync(VisionToolDefinition definition, VisionToolContext context, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (!context.TryGetInputImage(definition, out var frame))
        {
            stopwatch.Stop();
            return Task.FromResult(GeometryToolSupport.CreateMissingImageInputResult(definition, Kind, stopwatch.Elapsed));
        }

        var sourceRoi = GeometryToolSupport.FindBoundRoi(context.Recipe, definition);
        var roi = sourceRoi is null
            ? null
            : GeometryToolSupport.MapRoiForPositionInput(context, definition, sourceRoi);
        var result = MultiTargetMatcher.Match(frame, roi, definition.Parameters, context.GetGrayMat(frame), cancellationToken);
        stopwatch.Stop();

        var matches = result.Matches;
        var best = matches.FirstOrDefault();
        if (best is not null)
        {
            var bestPose = new Pose2D(best.X, best.Y, best.Angle);
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
        context.SetPortOutput(definition, "AllPositionsOutput", matches.Select(match => new Pose2D(match.X, match.Y, match.Angle)).ToArray());
        context.SetPortOutput(definition, "ScoresOutput", matches.Select(match => match.Score).ToArray());

        var data = new Dictionary<string, string>
        {
            ["count"] = matches.Count.ToString(),
            ["score"] = best?.Score.ToInvariant() ?? "0",
            ["bestScore"] = best?.Score.ToInvariant() ?? "0",
            ["x"] = best?.X.ToInvariant() ?? "0",
            ["y"] = best?.Y.ToInvariant() ?? "0",
            ["angle"] = best?.Angle.ToInvariant() ?? "0",
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

    private static string FormatMatches(IReadOnlyList<MultiTargetMatchCandidate> matches)
    {
        return string.Join(
            ";",
            matches.Select(match =>
                $"{match.X.ToInvariant()},{match.Y.ToInvariant()},{match.Angle.ToInvariant()},{match.Score.ToInvariant()},{match.Width},{match.Height},{match.Shape},{match.Radius.ToInvariant()}"));
    }
}
