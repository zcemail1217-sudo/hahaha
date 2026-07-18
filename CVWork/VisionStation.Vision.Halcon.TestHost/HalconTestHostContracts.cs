using System.Globalization;
using System.Text.Json.Serialization;

namespace VisionStation.Vision.Halcon.TestHost;

public static class HalconTestHostCommands
{
    public const string Benchmark = "benchmark";

    public const string Probe = "probe";

    public const string LicenseSmoke = "license-smoke";

    public const string ModelRoundtrip = "model-roundtrip";

    public const string Timeout = "timeout";
}

public static class HalconTestHostExitCodes
{
    public const int Success = 0;

    public const int CommandFailed = 1;

    public const int InvalidArguments = 2;

    public const int UnexpectedFailure = 3;
}

public sealed record HalconTestHostCommand(
    string Name,
    string RuntimeRoot,
    string? ExpectedVersion,
    string? WorkingDirectory,
    int? Milliseconds)
{
    public int? Iterations { get; init; }

    public string? OutputPath { get; init; }

    public string? SecondRuntimeRoot { get; init; }

    public bool CorruptModel { get; init; }

    public int? CancelAfterMilliseconds { get; init; }
}

public sealed record HalconTestHostReport(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("stage")] string Stage,
    [property: JsonPropertyName("runtimeVersion")] string? RuntimeVersion,
    [property: JsonPropertyName("technicalSummary")] string TechnicalSummary);

public static class HalconTestHostCommandLine
{
    private const int MinimumBenchmarkIterations = 1;
    private const int MaximumBenchmarkIterations = 1000;
    private const int MinimumTimeoutMilliseconds = 100;
    private const int MaximumTimeoutMilliseconds = 60000;

    public static bool TryParse(
        IReadOnlyList<string> args,
        out HalconTestHostCommand? command,
        out HalconTestHostReport? failure)
    {
        ArgumentNullException.ThrowIfNull(args);
        command = null;
        failure = null;
        if (args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            failure = Invalid("A HALCON TestHost command is required.");
            return false;
        }

        string name = args[0];
        if (!IsKnownCommand(name))
        {
            failure = Invalid($"Unknown HALCON TestHost command '{name}'.");
            return false;
        }

        if (!TryReadOptions(args, out Dictionary<string, string>? options, out string? optionFailure))
        {
            failure = Invalid(optionFailure!);
            return false;
        }

        if (!TryReadRequired(options!, "--root", out string runtimeRoot))
        {
            failure = Invalid("Option '--root' is required.");
            return false;
        }

        string? expectedVersion = null;
        string? workingDirectory = null;
        int? milliseconds = null;
        string? secondRuntimeRoot = null;
        var corruptModel = false;
        int? cancelAfterMilliseconds = null;
        int? iterations = null;
        string? outputPath = null;
        HashSet<string> allowedOptions = new(StringComparer.Ordinal)
        {
            "--root"
        };

        switch (name)
        {
            case HalconTestHostCommands.Benchmark:
                allowedOptions.Add("--iterations");
                allowedOptions.Add("--output");
                if (!TryReadRequired(options!, "--iterations", out string rawIterations) ||
                    !int.TryParse(
                        rawIterations,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int parsedIterations) ||
                    parsedIterations < MinimumBenchmarkIterations ||
                    parsedIterations > MaximumBenchmarkIterations)
                {
                    failure = Invalid(
                        $"Option '--iterations' must be an integer from {MinimumBenchmarkIterations} to {MaximumBenchmarkIterations}.");
                    return false;
                }

                if (!TryReadRequired(options!, "--output", out outputPath))
                {
                    failure = Invalid("Option '--output' is required for 'benchmark'.");
                    return false;
                }

                if (!HasJsonExtension(outputPath))
                {
                    failure = Invalid("Option '--output' must name a '.json' file.");
                    return false;
                }

                iterations = parsedIterations;
                break;

            case HalconTestHostCommands.Probe:
                allowedOptions.Add("--expected-version");
                allowedOptions.Add("--second-root");
                if (!TryReadRequired(options!, "--expected-version", out expectedVersion))
                {
                    failure = Invalid("Option '--expected-version' is required for 'probe'.");
                    return false;
                }

                if (options!.ContainsKey("--second-root") &&
                    !TryReadRequired(options, "--second-root", out secondRuntimeRoot))
                {
                    failure = Invalid("Option '--second-root' cannot be empty.");
                    return false;
                }

                break;

            case HalconTestHostCommands.ModelRoundtrip:
                allowedOptions.Add("--working-directory");
                allowedOptions.Add("--corrupt-model");
                if (!TryReadRequired(options!, "--working-directory", out workingDirectory))
                {
                    failure = Invalid("Option '--working-directory' is required for 'model-roundtrip'.");
                    return false;
                }

                if (options!.TryGetValue("--corrupt-model", out string? rawCorruptModel) &&
                    !bool.TryParse(rawCorruptModel, out corruptModel))
                {
                    failure = Invalid("Option '--corrupt-model' must be 'true' or 'false'.");
                    return false;
                }

                break;

            case HalconTestHostCommands.Timeout:
                allowedOptions.Add("--milliseconds");
                allowedOptions.Add("--cancel-after-milliseconds");
                if (!TryReadRequired(options!, "--milliseconds", out string rawMilliseconds) ||
                    !int.TryParse(
                        rawMilliseconds,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out int parsedMilliseconds) ||
                    parsedMilliseconds < MinimumTimeoutMilliseconds ||
                    parsedMilliseconds > MaximumTimeoutMilliseconds)
                {
                    failure = Invalid(
                        $"Option '--milliseconds' must be an integer from {MinimumTimeoutMilliseconds} to {MaximumTimeoutMilliseconds}.");
                    return false;
                }

                milliseconds = parsedMilliseconds;
                if (options!.TryGetValue(
                        "--cancel-after-milliseconds",
                        out string? rawCancelAfterMilliseconds) &&
                    (!int.TryParse(
                         rawCancelAfterMilliseconds,
                         NumberStyles.None,
                         CultureInfo.InvariantCulture,
                         out int parsedCancelAfterMilliseconds) ||
                     parsedCancelAfterMilliseconds < 1 ||
                     parsedCancelAfterMilliseconds > MaximumTimeoutMilliseconds))
                {
                    failure = Invalid(
                        $"Option '--cancel-after-milliseconds' must be an integer from 1 to {MaximumTimeoutMilliseconds}.");
                    return false;
                }

                if (rawCancelAfterMilliseconds is not null)
                {
                    cancelAfterMilliseconds = int.Parse(
                        rawCancelAfterMilliseconds,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture);
                }

                break;
        }

        string? unknownOption = options!.Keys.FirstOrDefault(option => !allowedOptions.Contains(option));
        if (unknownOption is not null)
        {
            failure = Invalid($"Option '{unknownOption}' is not valid for '{name}'.");
            return false;
        }

        command = new HalconTestHostCommand(
            name,
            runtimeRoot,
            expectedVersion,
            workingDirectory,
            milliseconds)
        {
            Iterations = iterations,
            OutputPath = outputPath,
            SecondRuntimeRoot = secondRuntimeRoot,
            CorruptModel = corruptModel,
            CancelAfterMilliseconds = cancelAfterMilliseconds
        };
        return true;
    }

    private static bool IsKnownCommand(string name)
    {
        return string.Equals(name, HalconTestHostCommands.Benchmark, StringComparison.Ordinal) ||
               string.Equals(name, HalconTestHostCommands.Probe, StringComparison.Ordinal) ||
               string.Equals(name, HalconTestHostCommands.LicenseSmoke, StringComparison.Ordinal) ||
               string.Equals(name, HalconTestHostCommands.ModelRoundtrip, StringComparison.Ordinal) ||
               string.Equals(name, HalconTestHostCommands.Timeout, StringComparison.Ordinal);
    }

    private static bool TryReadOptions(
        IReadOnlyList<string> args,
        out Dictionary<string, string>? options,
        out string? failure)
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        failure = null;
        for (int index = 1; index < args.Count; index += 2)
        {
            string option = args[index];
            if (!option.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Count)
            {
                options = null;
                failure = $"Option '{option}' must be followed by one value.";
                return false;
            }

            string value = args[index + 1];
            if (string.IsNullOrWhiteSpace(value))
            {
                options = null;
                failure = $"Option '{option}' cannot be empty.";
                return false;
            }

            if (!options.TryAdd(option, value))
            {
                options = null;
                failure = $"Option '{option}' cannot be specified more than once.";
                return false;
            }
        }

        return true;
    }

    private static bool TryReadRequired(
        IReadOnlyDictionary<string, string> options,
        string key,
        out string value)
    {
        if (options.TryGetValue(key, out string? raw) && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool HasJsonExtension(string path)
    {
        try
        {
            return string.Equals(
                Path.GetExtension(path),
                ".json",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static HalconTestHostReport Invalid(string summary)
    {
        return new HalconTestHostReport(
            false,
            "COMMAND_INVALID",
            "arguments",
            null,
            summary);
    }
}
