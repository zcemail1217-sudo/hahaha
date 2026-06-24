using System.Text;
using VisionStation.Communication;

namespace VisionStation.Application.Inspection;

internal sealed record CommunicationSignalMatchResult(string? MatchedValue, IReadOnlyList<string> IgnoredValues)
{
    public static CommunicationSignalMatchResult NoMatch { get; } = new(null, Array.Empty<string>());
}

internal static class CommunicationSignalMatcher
{
    public static CommunicationSignalMatchResult MatchResponse(
        byte[] response,
        string expected,
        string matchMode)
    {
        var value = Encoding.UTF8.GetString(response);
        return SignalMatcher.MatchesSignal(value, expected, matchMode)
            ? new CommunicationSignalMatchResult(value, Array.Empty<string>())
            : new CommunicationSignalMatchResult(null, [value]);
    }

    public static CommunicationSignalMatchResult MatchFrames(
        List<byte> frameBuffer,
        byte[] incoming,
        CommunicationFrameOptions frameOptions,
        string expected,
        string matchMode)
    {
        var ignoredValues = new List<string>();
        foreach (var frame in CommunicationFrameCodec.DecodeFrames(frameBuffer, incoming, frameOptions))
        {
            if (frame.Payload.Length == 0 &&
                !string.Equals(frame.Label, "RX-CHUNK", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Encoding.UTF8.GetString(frame.Payload);
            if (SignalMatcher.MatchesSignal(value, expected, matchMode))
            {
                return new CommunicationSignalMatchResult(value, ignoredValues);
            }

            ignoredValues.Add(value);
        }

        return ignoredValues.Count == 0
            ? CommunicationSignalMatchResult.NoMatch
            : new CommunicationSignalMatchResult(null, ignoredValues);
    }
}
