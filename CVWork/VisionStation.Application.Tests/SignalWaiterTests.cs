using VisionStation.Application.Inspection;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class SignalWaiterTests
{
    [Fact]
    public async Task WaitUntilMatchedAsync_ReturnsFirstMatchedValue()
    {
        var values = new Queue<string?>(new[] { null, "WAIT", "OK" });

        var actual = await SignalWaiter.WaitUntilMatchedAsync(
            _ => Task.FromResult(values.Count > 0 ? values.Dequeue() : null),
            "OK",
            "Equals",
            timeoutMs: 100,
            pollIntervalMs: 1,
            debounceMs: 0,
            CancellationToken.None);

        Assert.Equal("OK", actual);
    }

    [Fact]
    public async Task WaitUntilMatchedAsync_ReturnsNullWhenTimedOut()
    {
        var actual = await SignalWaiter.WaitUntilMatchedAsync(
            _ => Task.FromResult<string?>("WAIT"),
            "OK",
            "Equals",
            timeoutMs: 10,
            pollIntervalMs: 1,
            debounceMs: 0,
            CancellationToken.None);

        Assert.Null(actual);
    }
}
