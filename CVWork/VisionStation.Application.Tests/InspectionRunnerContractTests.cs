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
}
