using VisionStation.Application;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class InspectionRunnerContractTests
{
    [Fact]
    public void IInspectionRunner_ExposesRunCompletedEvent()
    {
        Assert.NotNull(typeof(IInspectionRunner).GetEvent("RunCompleted"));
    }

    [Fact]
    public void IInspectionRunner_PublicContractRemainsRunAsyncAndRunCompletedOnly()
    {
        Assert.Equal(
            ["RunAsync"],
            typeof(IInspectionRunner).GetMethods()
                .Where(method => !method.IsSpecialName)
                .Select(method => method.Name)
                .Order()
                .ToArray());
        Assert.Equal(
            ["RunCompleted"],
            typeof(IInspectionRunner).GetEvents().Select(@event => @event.Name).Order().ToArray());
    }
}
