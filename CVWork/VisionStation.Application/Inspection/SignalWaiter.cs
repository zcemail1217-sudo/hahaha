using System.Diagnostics;

namespace VisionStation.Application.Inspection;

internal static class SignalWaiter
{
    public static async Task<string?> WaitUntilMatchedAsync(
        Func<CancellationToken, Task<string?>> readValueAsync,
        string expected,
        string matchMode,
        int timeoutMs,
        int pollIntervalMs,
        int debounceMs,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        long? firstMatchedAt = null;
        string lastMatchedValue = string.Empty;

        while (stopwatch.ElapsedMilliseconds <= timeoutMs)
        {
            var value = await readValueAsync(cancellationToken);
            if (value is not null && SignalMatcher.MatchesSignal(value, expected, matchMode))
            {
                firstMatchedAt ??= stopwatch.ElapsedMilliseconds;
                lastMatchedValue = value;
                if (debounceMs == 0 || stopwatch.ElapsedMilliseconds - firstMatchedAt.Value >= debounceMs)
                {
                    return lastMatchedValue;
                }
            }
            else
            {
                firstMatchedAt = null;
            }

            await Task.Delay(pollIntervalMs, cancellationToken);
        }

        return null;
    }
}
