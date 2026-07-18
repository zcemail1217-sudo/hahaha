using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionStation.Vision.Halcon.TestHost;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static async Task<int> Main(string[] args)
    {
        TextWriter jsonOutput = Console.Out;
        Console.SetOut(TextWriter.Null);

        HalconTestHostReport report;
        int exitCode;
        try
        {
            if (!HalconTestHostCommandLine.TryParse(
                    args,
                    out HalconTestHostCommand? command,
                    out HalconTestHostReport? parseFailure))
            {
                report = parseFailure ?? new HalconTestHostReport(
                    false,
                    "COMMAND_INVALID",
                    "arguments",
                    null,
                    "HALCON TestHost arguments are invalid.");
                exitCode = HalconTestHostExitCodes.InvalidArguments;
            }
            else
            {
                report = await new HalconTestHostRunner()
                    .ExecuteAsync(command!, CancellationToken.None)
                    .ConfigureAwait(false);
                exitCode = report.Success
                    ? HalconTestHostExitCodes.Success
                    : HalconTestHostExitCodes.CommandFailed;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            report = new HalconTestHostReport(
                false,
                "TESTHOST_UNEXPECTED",
                "testhost",
                null,
                $"Unhandled TestHost failure; ExceptionType={exception.GetType().Name}.");
            exitCode = HalconTestHostExitCodes.UnexpectedFailure;
        }
        finally
        {
            Console.SetOut(jsonOutput);
        }

        jsonOutput.Write(JsonSerializer.Serialize(report, JsonOptions));
        return exitCode;
    }
}
