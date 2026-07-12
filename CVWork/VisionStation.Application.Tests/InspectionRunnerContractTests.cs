using System.Reflection;
using VisionStation.Application;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Application.Tests;

public sealed class InspectionExecutionContractTests
{
    [Theory]
    [InlineData("IInspectionExecution")]
    [InlineData("IInspectionSession")]
    [InlineData("InspectionRunMode")]
    [InlineData("InspectionRunModes")]
    [InlineData("InspectionRunIntent")]
    [InlineData("ActiveInspectionRun")]
    [InlineData("InspectionExecutionChangedEventArgs")]
    [InlineData("RunRejectionReason")]
    [InlineData("RunRejection")]
    [InlineData("RunAdmission")]
    public void Application_exposes_public_inspection_execution_type(string typeName)
    {
        var type = GetRequiredApplicationType(typeName);

        Assert.True(type.IsPublic, $"{type.FullName} must be public.");
        Assert.False(type.IsNested);
    }

    [Fact]
    public void IInspectionExecution_exposes_expected_interface()
    {
        var type = GetRequiredApplicationType("IInspectionExecution");

        Assert.True(type.IsInterface);

        var current = GetRequiredProperty(type, "Current");
        Assert.Equal(typeof(ActiveInspectionRun), current.PropertyType);
        Assert.True(current.CanRead);
        Assert.False(current.CanWrite);

        Assert.Equal(
            typeof(EventHandler<InspectionExecutionChangedEventArgs>),
            GetRequiredEvent(type, "Changed").EventHandlerType);
        Assert.Equal(
            typeof(EventHandler<InspectionRunResult>),
            GetRequiredEvent(type, "RunCompleted").EventHandlerType);

        var tryBegin = GetRequiredMethod(type, "TryBegin");
        Assert.Equal(typeof(RunAdmission), tryBegin.ReturnType);
        Assert.Collection(
            tryBegin.GetParameters(),
            parameter =>
            {
                Assert.Equal("intent", parameter.Name);
                Assert.Equal(typeof(InspectionRunIntent), parameter.ParameterType);
                Assert.False(parameter.IsOptional);
            });
    }

    [Fact]
    public void IInspectionSession_exposes_expected_interface()
    {
        var type = GetRequiredApplicationType("IInspectionSession");

        Assert.True(type.IsInterface);
        Assert.Contains(typeof(IAsyncDisposable), type.GetInterfaces());

        var run = GetRequiredProperty(type, "Run");
        Assert.Equal(typeof(ActiveInspectionRun), run.PropertyType);
        Assert.True(run.CanRead);
        Assert.False(run.CanWrite);

        var executeAsync = GetRequiredMethod(type, "ExecuteAsync");
        Assert.Equal(typeof(Task<InspectionRunResult>), executeAsync.ReturnType);
        Assert.Collection(
            executeAsync.GetParameters(),
            request =>
            {
                Assert.Equal("request", request.Name);
                Assert.Equal(typeof(InspectionRequest), request.ParameterType);
                Assert.False(request.IsOptional);
            },
            cancellationToken =>
            {
                Assert.Equal("cancellationToken", cancellationToken.Name);
                Assert.Equal(typeof(CancellationToken), cancellationToken.ParameterType);
                Assert.True(cancellationToken.IsOptional);
                Assert.True(cancellationToken.HasDefaultValue);
            });
    }

    [Fact]
    public void InspectionRequest_exposes_optional_recipe_snapshot()
    {
        var property = typeof(InspectionRequest).GetProperty("RecipeSnapshot");

        Assert.NotNull(property);
        Assert.Equal(typeof(Recipe), property.PropertyType);
        Assert.True(property.CanRead);
        Assert.NotNull(property.SetMethod);
        Assert.Null(property.GetValue(new InspectionRequest()));
    }

    [Fact]
    public void RunAdmission_exposes_public_payload_cases()
    {
        var admission = GetRequiredApplicationType("RunAdmission");
        Assert.True(admission.IsAbstract);

        var acquired = GetRequiredNestedType(admission, "Acquired");
        Assert.True(acquired.IsNestedPublic);
        Assert.Equal(admission, acquired.BaseType);
        AssertRecordShape(
            acquired,
            ("Session", "Session", typeof(IInspectionSession)));

        var rejected = GetRequiredNestedType(admission, "Rejected");
        Assert.True(rejected.IsNestedPublic);
        Assert.Equal(admission, rejected.BaseType);
        AssertRecordShape(
            rejected,
            ("Rejection", "Rejection", typeof(RunRejection)));
    }

    [Fact]
    public void RunRejectionReason_exposes_exact_values()
    {
        var type = GetRequiredApplicationType("RunRejectionReason");

        Assert.True(type.IsEnum);
        Assert.Equal(
            new[] { "Busy", "AlreadyRunning", "NotOwner" },
            Enum.GetNames(type));
        Assert.Equal(
            new[] { 0, 1, 2 },
            Enum.GetValues<RunRejectionReason>().Select(static value => (int)value));
    }

    [Fact]
    public void Inspection_execution_records_expose_expected_components()
    {
        Assert.True(typeof(InspectionRunMode).IsValueType);
        AssertRecordShape(
            typeof(InspectionRunMode),
            ("key", "Key", typeof(string)),
            ("displayName", "DisplayName", typeof(string)));
        AssertRecordShape(
            typeof(InspectionRunIntent),
            ("Mode", "Mode", typeof(InspectionRunMode)),
            ("EntryPoint", "EntryPoint", typeof(string)));
        AssertRecordShape(
            typeof(ActiveInspectionRun),
            ("SessionId", "SessionId", typeof(Guid)),
            ("Intent", "Intent", typeof(InspectionRunIntent)),
            ("StartedAt", "StartedAt", typeof(DateTimeOffset)));
        AssertRecordShape(
            typeof(RunRejection),
            ("Reason", "Reason", typeof(RunRejectionReason)),
            ("Active", "Active", typeof(ActiveInspectionRun)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Production.Manual")]
    [InlineData("production manual")]
    [InlineData("production/manual")]
    public void InspectionRunMode_rejects_invalid_key(string key)
    {
        Assert.Throws<ArgumentException>(() => new InspectionRunMode(key, "测试"));
    }

    [Fact]
    public void InspectionRunMode_rejects_empty_display_name()
    {
        Assert.Throws<ArgumentException>(
            () => new InspectionRunMode("custom.test", " "));
    }

    [Fact]
    public void InspectionRunMode_accepts_extension_mode_without_registration()
    {
        var mode = new InspectionRunMode("calibration.test", "标定试运行");

        Assert.Equal("calibration.test", mode.Key);
        Assert.Equal("标定试运行", mode.DisplayName);
    }

    private static Type GetRequiredApplicationType(string typeName)
    {
        var type = typeof(InspectionRunResult).Assembly.GetType(
            $"VisionStation.Application.{typeName}");

        Assert.NotNull(type);
        return type;
    }

    private static PropertyInfo GetRequiredProperty(Type type, string name)
    {
        var property = type.GetProperty(name);

        Assert.NotNull(property);
        return property;
    }

    private static EventInfo GetRequiredEvent(Type type, string name)
    {
        var eventInfo = type.GetEvent(name);

        Assert.NotNull(eventInfo);
        return eventInfo;
    }

    private static MethodInfo GetRequiredMethod(Type type, string name)
    {
        return Assert.Single(type.GetMethods(), method => method.Name == name);
    }

    private static Type GetRequiredNestedType(Type declaringType, string name)
    {
        var nestedType = declaringType.GetNestedType(name, BindingFlags.Public);

        Assert.NotNull(nestedType);
        return nestedType;
    }

    private static void AssertRecordShape(
        Type type,
        params (string ParameterName, string PropertyName, Type Type)[] components)
    {
        var constructor = type.GetConstructor(components.Select(component => component.Type).ToArray());
        Assert.NotNull(constructor);
        Assert.Equal(
            components.Select(component => component.ParameterName),
            constructor.GetParameters().Select(parameter => parameter.Name!));

        foreach (var component in components)
        {
            Assert.Equal(component.Type, GetRequiredProperty(type, component.PropertyName).PropertyType);
        }
    }
}
