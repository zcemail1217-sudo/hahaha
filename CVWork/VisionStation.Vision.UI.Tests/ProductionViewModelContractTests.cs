using Prism.Commands;
using VisionStation.Application;
using VisionStation.Client.ViewModels;
using Xunit;

namespace VisionStation.Vision.UI.Tests;

public sealed class ProductionViewModelContractTests
{
    [Fact]
    public void ProductionDashboard_HasSingleConstructorWithInspectionExecution()
    {
        var constructor = Assert.Single(typeof(ProductionDashboardViewModel).GetConstructors());

        Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IInspectionExecution));
    }

    [Theory]
    [InlineData(nameof(ProductionDashboardViewModel.RunSingleCommand))]
    [InlineData(nameof(ProductionDashboardViewModel.StartCommand))]
    [InlineData(nameof(ProductionDashboardViewModel.StopCommand))]
    public void ProductionCommands_AreAwaitableAsyncCommands(string propertyName)
    {
        var property = typeof(ProductionDashboardViewModel).GetProperty(propertyName);

        Assert.NotNull(property);
        Assert.Equal(typeof(AsyncDelegateCommand), property.PropertyType);
    }

    [Fact]
    public void Shell_HasSingleConstructorWithInspectionExecution()
    {
        var constructor = Assert.Single(typeof(ShellWindowViewModel).GetConstructors());

        Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IInspectionExecution));
    }
}
