using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class TemplateReferencePoseCodecTests
{
    [Fact]
    public void HalconReadsOnlyNamespacedReferenceGeometry()
    {
        var parameters = CompleteHalconGeometry();
        parameters["standardX"] = "900";
        parameters["standardY"] = "901";
        parameters["standardAngle"] = "90";
        parameters["standardScale"] = "9";
        parameters["templateWidth"] = "999";
        parameters["templateHeight"] = "998";

        var geometry = TemplateReferencePoseCodec.ReadActive(parameters);

        Assert.NotNull(geometry);
        Assert.Equal((10d, 20d, 30d, 1.25d),
            (geometry.StandardPose.X, geometry.StandardPose.Y, geometry.StandardPose.Angle, geometry.StandardPose.Scale));
        Assert.Equal((40, 50), (geometry.TemplateWidth, geometry.TemplateHeight));
    }

    [Fact]
    public void HalconReferenceWithoutScaleIsIncompleteAndDoesNotUseLegacyScale()
    {
        var parameters = CompleteHalconGeometry();
        parameters.Remove("halcon.standardScale");
        parameters["standardScale"] = "2";

        Assert.Null(TemplateReferencePoseCodec.ReadActive(parameters));
    }

    [Theory]
    [InlineData("halcon.standardX")]
    [InlineData("halcon.standardY")]
    [InlineData("halcon.standardAngle")]
    [InlineData("halcon.standardScale")]
    [InlineData("halcon.templateWidth")]
    [InlineData("halcon.templateHeight")]
    public void HalconMissingFieldNeverFallsBackAcrossNamespace(string missingKey)
    {
        var parameters = CompleteHalconGeometry();
        parameters.Remove(missingKey);
        AddCompleteLegacyGeometry(parameters);

        Assert.Null(TemplateReferencePoseCodec.ReadActive(parameters));
    }

    [Theory]
    [InlineData("OpenCv")]
    [InlineData("ManagedNcc")]
    public void LegacyEnginesReadOnlyCommonReferenceGeometry(string engine)
    {
        var parameters = CompleteHalconGeometry();
        parameters["engine"] = engine;
        AddCompleteLegacyGeometry(parameters);

        var geometry = TemplateReferencePoseCodec.ReadActive(parameters);

        Assert.NotNull(geometry);
        Assert.Equal((100d, 200d, 45d, 1.5d),
            (geometry.StandardPose.X, geometry.StandardPose.Y, geometry.StandardPose.Angle, geometry.StandardPose.Scale));
        Assert.Equal((60, 70), (geometry.TemplateWidth, geometry.TemplateHeight));
    }

    [Fact]
    public void LegacyEngineIgnoresInvalidInactiveHalconGeometryWithoutMutatingInput()
    {
        var parameters = CompleteHalconGeometry();
        parameters["engine"] = "OpenCv";
        parameters["halcon.standardScale"] = "NaN";
        parameters["halcon.templateWidth"] = "garbage";
        AddCompleteLegacyGeometry(parameters);
        var snapshot = parameters.ToArray();

        var geometry = TemplateReferencePoseCodec.ReadActive(parameters);

        Assert.NotNull(geometry);
        Assert.Equal(1.5, geometry.StandardPose.Scale);
        Assert.Equal(snapshot, parameters.ToArray());
    }

    [Fact]
    public void HalconEngineIgnoresInvalidInactiveLegacyGeometryWithoutMutatingInput()
    {
        var parameters = CompleteHalconGeometry();
        parameters["standardScale"] = "NaN";
        parameters["templateWidth"] = "garbage";
        var snapshot = parameters.ToArray();

        var geometry = TemplateReferencePoseCodec.ReadActive(parameters);

        Assert.NotNull(geometry);
        Assert.Equal(1.25, geometry.StandardPose.Scale);
        Assert.Equal(snapshot, parameters.ToArray());
    }

    [Fact]
    public void MissingEngineUsesLegacyOpenCvGeometry()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddCompleteLegacyGeometry(parameters);

        var geometry = TemplateReferencePoseCodec.ReadActive(parameters);

        Assert.NotNull(geometry);
        Assert.Equal(TemplateMatchingEngine.OpenCv, TemplateMatchingEngineResolver.Resolve(parameters));
        Assert.Equal(1.5, geometry.StandardPose.Scale);
    }

    [Theory]
    [InlineData("OpenCv")]
    [InlineData("ManagedNcc")]
    public void LegacyStandardScaleDefaultsToOneAndAngleDefaultsToZero(string engine)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = engine,
            ["standardX"] = "10",
            ["standardY"] = "20",
            ["templateWidth"] = "40",
            ["templateHeight"] = "50"
        };

        var geometry = TemplateReferencePoseCodec.ReadActive(parameters);

        Assert.NotNull(geometry);
        Assert.Equal((10d, 20d, 0d, 1d),
            (geometry.StandardPose.X, geometry.StandardPose.Y, geometry.StandardPose.Angle, geometry.StandardPose.Scale));
    }

    [Fact]
    public void LegacyGeometryFallsBackToLearnedTemplateCenterWithinItsOwnNamespace()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["templateX"] = "5",
            ["templateY"] = "7",
            ["templateWidth"] = "40",
            ["templateHeight"] = "20"
        };

        var geometry = TemplateReferencePoseCodec.ReadActive(parameters);

        Assert.NotNull(geometry);
        Assert.Equal((25d, 17d, 0d, 1d),
            (geometry.StandardPose.X, geometry.StandardPose.Y, geometry.StandardPose.Angle, geometry.StandardPose.Scale));
        Assert.Equal((40, 20), (geometry.TemplateWidth, geometry.TemplateHeight));
    }

    [Fact]
    public void IncompleteLegacyGeometryDoesNotUseCompleteHalconGeometry()
    {
        var parameters = CompleteHalconGeometry();
        parameters["engine"] = "OpenCv";
        parameters["standardX"] = "10";
        parameters["templateWidth"] = "40";
        parameters["templateHeight"] = "50";

        Assert.Null(TemplateReferencePoseCodec.ReadActive(parameters));
    }

    public static IEnumerable<object[]> InvalidActiveFields()
    {
        yield return ["halcon.standardX", "NaN"];
        yield return ["halcon.standardY", "Infinity"];
        yield return ["halcon.standardAngle", "not-a-number"];
        yield return ["halcon.standardScale", "NaN"];
        yield return ["halcon.standardScale", "0"];
        yield return ["halcon.standardScale", "-1"];
        yield return ["halcon.templateWidth", "0"];
        yield return ["halcon.templateWidth", "1.5"];
        yield return ["halcon.templateHeight", "garbage"];
    }

    [Theory]
    [MemberData(nameof(InvalidActiveFields))]
    public void HalconRejectsExplicitInvalidActiveGeometry(string key, string value)
    {
        var parameters = CompleteHalconGeometry();
        parameters[key] = value;

        var exception = Assert.Throws<TemplateMatchingConfigurationException>(() =>
            TemplateReferencePoseCodec.ReadActive(parameters));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, exception.Code);
    }

    public static IEnumerable<object[]> InvalidIncompleteHalconActiveFields()
    {
        yield return ["halcon.standardX", "NaN", "halcon.templateHeight"];
        yield return ["halcon.standardY", "Infinity", "halcon.templateHeight"];
        yield return ["halcon.standardAngle", "not-a-number", "halcon.templateHeight"];
        yield return ["halcon.standardScale", "NaN", "halcon.templateHeight"];
        yield return ["halcon.standardScale", "0", "halcon.templateHeight"];
        yield return ["halcon.standardScale", "-1", "halcon.templateHeight"];
        yield return ["halcon.templateWidth", "0", "halcon.templateHeight"];
        yield return ["halcon.templateHeight", "garbage", "halcon.templateWidth"];
    }

    [Theory]
    [MemberData(nameof(InvalidIncompleteHalconActiveFields))]
    public void HalconRejectsEveryExplicitInvalidActiveFieldBeforeCheckingCompleteness(
        string invalidKey,
        string invalidValue,
        string missingKey)
    {
        var parameters = CompleteHalconGeometry();
        parameters[invalidKey] = invalidValue;
        parameters.Remove(missingKey);

        var exception = Assert.Throws<TemplateMatchingConfigurationException>(() =>
            TemplateReferencePoseCodec.ReadActive(parameters));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, exception.Code);
    }

    public static IEnumerable<object[]> InvalidIncompleteLegacyActiveFields()
    {
        yield return ["standardX", "NaN", "templateHeight"];
        yield return ["standardY", "Infinity", "templateHeight"];
        yield return ["standardAngle", "not-a-number", "templateHeight"];
        yield return ["standardScale", "NaN", "templateHeight"];
        yield return ["templateX", "NaN", "templateHeight"];
        yield return ["templateY", "Infinity", "templateHeight"];
        yield return ["templateWidth", "0", "templateHeight"];
        yield return ["templateHeight", "0", "templateWidth"];
    }

    [Theory]
    [MemberData(nameof(InvalidIncompleteLegacyActiveFields))]
    public void LegacyRejectsEveryExplicitInvalidActiveFieldBeforeCheckingCompleteness(
        string invalidKey,
        string invalidValue,
        string missingKey)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["standardX"] = "10",
            ["standardY"] = "20",
            ["standardAngle"] = "30",
            ["standardScale"] = "1",
            ["templateX"] = "5",
            ["templateY"] = "7",
            ["templateWidth"] = "40",
            ["templateHeight"] = "50"
        };
        parameters[invalidKey] = invalidValue;
        parameters.Remove(missingKey);

        var exception = Assert.Throws<TemplateMatchingConfigurationException>(() =>
            TemplateReferencePoseCodec.ReadActive(parameters));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, exception.Code);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("garbage")]
    public void LegacyEnginesRejectExplicitInvalidStandardScale(string value)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["standardX"] = "10",
            ["standardY"] = "20",
            ["standardScale"] = value,
            ["templateWidth"] = "40",
            ["templateHeight"] = "50"
        };

        var exception = Assert.Throws<TemplateMatchingConfigurationException>(() =>
            TemplateReferencePoseCodec.ReadActive(parameters));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, exception.Code);
    }

    [Fact]
    public void UnknownEngineIsNotTreatedAsLegacyGeometry()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "Halconn"
        };
        AddCompleteLegacyGeometry(parameters);

        var exception = Assert.Throws<TemplateMatchingConfigurationException>(() =>
            TemplateReferencePoseCodec.ReadActive(parameters));

        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigUnknownEngine, exception.Code);
    }

    private static Dictionary<string, string> CompleteHalconGeometry()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "Halcon",
            ["halcon.standardX"] = "10",
            ["halcon.standardY"] = "20",
            ["halcon.standardAngle"] = "30",
            ["halcon.standardScale"] = "1.25",
            ["halcon.templateWidth"] = "40",
            ["halcon.templateHeight"] = "50"
        };
    }

    private static void AddCompleteLegacyGeometry(IDictionary<string, string> parameters)
    {
        parameters["standardX"] = "100";
        parameters["standardY"] = "200";
        parameters["standardAngle"] = "45";
        parameters["standardScale"] = "1.5";
        parameters["templateWidth"] = "60";
        parameters["templateHeight"] = "70";
    }
}
