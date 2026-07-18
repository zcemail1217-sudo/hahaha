using HalconDotNet;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class HalconExceptionClassifierTests
{
    [Fact]
    public void TimeoutErrorCodeMapsToMatchTimeout()
    {
        var exception = new HOperatorException(9400, "fake-timeout");

        bool classified = HalconExceptionClassifier.TryClassify(
            exception,
            "FindScaledShapeModel",
            out TemplateMatchingDiagnostic? diagnostic);

        Assert.True(classified);
        Assert.NotNull(diagnostic);
        Assert.Equal(TemplateMatchingDiagnosticCodes.MatchTimeout, diagnostic.Code);
        Assert.Equal(TemplateMatchingFailureStages.Match, diagnostic.FailureStage);
        Assert.Contains("FindScaledShapeModel", diagnostic.TechnicalDetails, StringComparison.Ordinal);
        Assert.Contains("9400", diagnostic.TechnicalDetails, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2003)]
    [InlineData(2199)]
    [InlineData(2384)]
    public void LicenseErrorCodeRangeMapsToLicenseUnavailableWithoutLeakingMessage(int errorCode)
    {
        const string sensitiveMessage = "fake-sensitive-license-message";
        var exception = new HOperatorException(errorCode, sensitiveMessage);

        bool classified = HalconExceptionClassifier.TryClassify(
            exception,
            "CreateScaledShapeModel",
            out TemplateMatchingDiagnostic? diagnostic);

        Assert.True(classified);
        Assert.NotNull(diagnostic);
        Assert.Equal(TemplateMatchingDiagnosticCodes.LicenseUnavailable, diagnostic.Code);
        Assert.Equal(TemplateMatchingFailureStages.Runtime, diagnostic.FailureStage);
        Assert.Contains("CreateScaledShapeModel", diagnostic.TechnicalDetails, StringComparison.Ordinal);
        Assert.Contains(nameof(HOperatorException), diagnostic.TechnicalDetails, StringComparison.Ordinal);
        Assert.Contains(errorCode.ToString(), diagnostic.TechnicalDetails, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveMessage, diagnostic.TechnicalDetails, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RuntimeExceptionMappings))]
    public void RuntimeExceptionsMapToStableDiagnostics(Exception exception, string expectedCode)
    {
        bool classified = HalconExceptionClassifier.TryClassify(
            exception,
            "InitializeHalcon",
            out TemplateMatchingDiagnostic? diagnostic);

        Assert.True(classified);
        Assert.NotNull(diagnostic);
        Assert.Equal(expectedCode, diagnostic.Code);
        Assert.Equal(TemplateMatchingFailureStages.Runtime, diagnostic.FailureStage);
        Assert.Contains("InitializeHalcon", diagnostic.TechnicalDetails, StringComparison.Ordinal);
        Assert.Contains(exception.GetType().Name, diagnostic.TechnicalDetails, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherHalconErrorMapsToMatchOperatorFailed()
    {
        var exception = new HalconException(2385, "fake-operator-failure");

        bool classified = HalconExceptionClassifier.TryClassify(
            exception,
            "FindScaledShapeModel",
            out TemplateMatchingDiagnostic? diagnostic);

        Assert.True(classified);
        Assert.NotNull(diagnostic);
        Assert.Equal(TemplateMatchingDiagnosticCodes.MatchOperatorFailed, diagnostic.Code);
        Assert.Equal(TemplateMatchingFailureStages.Match, diagnostic.FailureStage);
        Assert.Contains("2385", diagnostic.TechnicalDetails, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingOperationUsesStableUnknownTechnicalDetail(string? operation)
    {
        const string sensitiveMessage = "fake-sensitive-license-message";
        var exception = new HOperatorException(2003, sensitiveMessage);

        bool classified = HalconExceptionClassifier.TryClassify(
            exception,
            operation,
            out TemplateMatchingDiagnostic? diagnostic);

        Assert.True(classified);
        Assert.NotNull(diagnostic);
        Assert.Contains("Operation=Unknown", diagnostic.TechnicalDetails, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveMessage, diagnostic.TechnicalDetails, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(UnclassifiedExceptions))]
    public void CancellationAndUnknownExceptionsAreNotClassified(Exception exception)
    {
        bool classified = HalconExceptionClassifier.TryClassify(
            exception,
            "FindScaledShapeModel",
            out TemplateMatchingDiagnostic? diagnostic);

        Assert.False(classified);
        Assert.Null(diagnostic);
    }

    public static TheoryData<Exception, string> RuntimeExceptionMappings()
    {
        return new TheoryData<Exception, string>
        {
            { new DllNotFoundException("fake-runtime-path"), TemplateMatchingDiagnosticCodes.RuntimeNotFound },
            { new BadImageFormatException("fake-architecture"), TemplateMatchingDiagnosticCodes.RuntimeArchMismatch },
            { new EntryPointNotFoundException("fake-entry-point"), TemplateMatchingDiagnosticCodes.RuntimeVersionMismatch }
        };
    }

    public static TheoryData<Exception> UnclassifiedExceptions()
    {
        return new TheoryData<Exception>
        {
            new OperationCanceledException("fake-cancelled"),
            new InvalidOperationException("fake-unknown")
        };
    }
}
