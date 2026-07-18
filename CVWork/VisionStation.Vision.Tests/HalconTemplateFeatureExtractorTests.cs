using System.Text;
using System.Text.Json;
using OpenCvSharp;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class HalconTemplateFeatureExtractorTests
{
    private static readonly RoiDefinition TemplateRoi = new()
    {
        Id = "template-roi",
        Name = "Template",
        Shape = RoiShapeKind.Rectangle,
        X = 20,
        Y = 20,
        Width = 220,
        Height = 140
    };

    private readonly HalconTemplateFeatureExtractor _extractor = new();

    [Fact]
    public void Gray8HistogramPreservesOddAndEvenMedianSemantics()
    {
        var empty = new Gray8Histogram();
        var odd = new Gray8Histogram();
        var even = new Gray8Histogram();
        foreach (byte value in new byte[] { 255, 10, 20 })
        {
            odd.Add(value);
        }

        foreach (byte value in new byte[] { 255, 0, 20, 10 })
        {
            even.Add(value);
        }

        Assert.True(double.IsNaN(empty.Median()));
        Assert.Equal(20, odd.Median());
        Assert.Equal(15, even.Median());
        Assert.Equal(3, odd.Count);
        Assert.Equal(4, even.Count);
    }

    [Fact]
    public void Gray8HistogramPreservesNearestRankPercentileBoundaries()
    {
        var histogram = new Gray8Histogram();
        foreach (byte value in new byte[] { 255, 0, 20, 10 })
        {
            histogram.Add(value);
        }

        Assert.True(double.IsNaN(new Gray8Histogram().NearestRankPercentile(0.15)));
        Assert.Equal(0, histogram.NearestRankPercentile(0));
        Assert.Equal(0, histogram.NearestRankPercentile(0.25));
        Assert.Equal(10, histogram.NearestRankPercentile(0.2500001));
        Assert.Equal(20, histogram.NearestRankPercentile(0.75));
        Assert.Equal(255, histogram.NearestRankPercentile(1));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-0.000001)]
    [InlineData(1.000001)]
    public void Gray8HistogramRejectsPercentileOutsideFiniteUnitInterval(double percentile)
    {
        var histogram = new Gray8Histogram();
        histogram.Add(10);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => histogram.NearestRankPercentile(percentile));
    }

    [Fact]
    public void CompleteDarkAsymmetricProductProducesDistributedFeatures()
    {
        ImageFrame frame = SyntheticTemplate();

        HalconTemplateFeatureExtractionResult result = _extractor.Extract(frame, TemplateRoi);

        Assert.True(result.Success, result.Diagnostic?.TechnicalDetails);
        HalconTemplateFeatureSet features = Assert.IsType<HalconTemplateFeatureSet>(result.Features);
        Assert.Null(result.Diagnostic);
        Assert.True(features.IsDarkForeground);
        Assert.True(features.OuterContour.Count >= 100);
        Assert.True(SignedArea(features.OuterContour) > 0);
        Assert.True(features.InnerFeatureGroups.Count >= 3);
        Assert.Equal(
            Math.Max(2, (int)Math.Ceiling(features.InnerFeatureGroups.Count * 0.67)),
            features.MinimumValidInnerGroupCount);
        Assert.True(features.FilledSupport.Area > 0);
        Assert.NotEmpty(features.FilledSupport.Runs);
        Assert.All(
            features.InnerFeatureGroups.SelectMany(group => group),
            point => Assert.True(
                MinimumDistance(point, features.OuterContour) > 3.5,
                $"Inner point ({point.X:R}, {point.Y:R}) entered the outer exclusion band."));
        Assert.Contains(
            features.InnerFeatureGroups.SelectMany(group => group),
            point => IsSubPixel(point.X) || IsSubPixel(point.Y));
    }

    [Fact]
    public void PersistedFilledSupportKeepsCompleteOuterSilhouette()
    {
        HalconTemplateFeatureSet features = Assert.IsType<HalconTemplateFeatureSet>(
            _extractor.Extract(SyntheticTemplate(), TemplateRoi).Features);
        (double minimumX, double minimumY, double maximumX, double maximumY) =
            FilledSupportBounds(features.FilledSupport);

        Assert.True(
            minimumX <= features.OuterContour.Min(point => point.X) + 0.75,
            $"Persisted support minimum X {minimumX:R} was eroded inside outer contour " +
            $"{features.OuterContour.Min(point => point.X):R}.");
        Assert.True(
            minimumY <= features.OuterContour.Min(point => point.Y) + 0.75,
            $"Persisted support minimum Y {minimumY:R} was eroded inside outer contour " +
            $"{features.OuterContour.Min(point => point.Y):R}.");
        Assert.True(
            maximumX >= features.OuterContour.Max(point => point.X) - 0.75,
            $"Persisted support maximum X {maximumX:R} was eroded inside outer contour " +
            $"{features.OuterContour.Max(point => point.X):R}.");
        Assert.True(
            maximumY >= features.OuterContour.Max(point => point.Y) - 0.75,
            $"Persisted support maximum Y {maximumY:R} was eroded inside outer contour " +
            $"{features.OuterContour.Max(point => point.Y):R}.");
    }

    [Theory]
    [InlineData(RoiShapeKind.RotatedRectangle)]
    [InlineData(RoiShapeKind.Circle)]
    [InlineData(RoiShapeKind.Polygon)]
    public void NonAxisAlignedRoiShapesRetainTheirIndependentDomainMask(RoiShapeKind shape)
    {
        RoiDefinition roi = shape switch
        {
            RoiShapeKind.RotatedRectangle => new RoiDefinition
            {
                Shape = shape,
                X = 130,
                Y = 90,
                Width = 220,
                Height = 140,
                Angle = 3
            },
            RoiShapeKind.Circle => new RoiDefinition
            {
                Shape = shape,
                X = 126,
                Y = 90,
                Radius = 90
            },
            RoiShapeKind.Polygon => new RoiDefinition
            {
                Shape = shape,
                Points =
                [
                    new Point2D(25, 25),
                    new Point2D(235, 30),
                    new Point2D(240, 150),
                    new Point2D(20, 155)
                ]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };

        HalconTemplateFeatureExtractionResult result = _extractor.Extract(SyntheticTemplate(), roi);

        Assert.True(result.Success, result.Diagnostic?.TechnicalDetails);
        Assert.True(Assert.IsType<HalconTemplateFeatureSet>(result.Features).FilledSupport.Area > 0);
    }

    [Fact]
    public void AsymmetricPolygonUsesItsAabbCenterAsReference()
    {
        var polygon = new RoiDefinition
        {
            Shape = RoiShapeKind.Polygon,
            Points =
            [
                new Point2D(20, 20),
                new Point2D(240, 20),
                new Point2D(240, 150),
                new Point2D(100, 160),
                new Point2D(20, 120)
            ]
        };

        HalconTemplateFeatureExtractionResult result =
            _extractor.Extract(SyntheticTemplate(), polygon);

        Assert.True(result.Success, result.Diagnostic?.TechnicalDetails);
        HalconTemplateFeatureSet features = Assert.IsType<HalconTemplateFeatureSet>(result.Features);
        Assert.Equal(130, 20 + features.ReferenceColumn, 12);
        Assert.Equal(90, 20 + features.ReferenceRow, 12);
    }

    [Fact]
    public void RepeatedExtractionOfSameFrameIsDeterministic()
    {
        ImageFrame frame = SyntheticTemplate();

        HalconTemplateFeatureSet first = Assert.IsType<HalconTemplateFeatureSet>(
            _extractor.Extract(frame, TemplateRoi).Features);
        HalconTemplateFeatureSet second = Assert.IsType<HalconTemplateFeatureSet>(
            _extractor.Extract(frame, TemplateRoi).Features);

        Assert.Equal(first.ReferenceRow, second.ReferenceRow);
        Assert.Equal(first.ReferenceColumn, second.ReferenceColumn);
        Assert.Equal(first.ModelDomainCentroidRow, second.ModelDomainCentroidRow);
        Assert.Equal(first.ModelDomainCentroidColumn, second.ModelDomainCentroidColumn);
        Assert.Equal(first.OuterContour, second.OuterContour);
        Assert.Equal(first.InnerFeatureGroups.Count, second.InnerFeatureGroups.Count);
        Assert.True(first.InnerFeatureGroups.Count >= 4);
        for (var index = 0; index < first.InnerFeatureGroups.Count; index++)
        {
            Assert.Equal(first.InnerFeatureGroups[index], second.InnerFeatureGroups[index]);
        }

        Point2D[] centroids = first.InnerFeatureGroups
            .Select(group => new Point2D(
                group.Average(point => point.X),
                group.Average(point => point.Y)))
            .ToArray();
        Assert.Equal(
            centroids.OrderBy(point => point.Y).ThenBy(point => point.X),
            centroids);

        Assert.Equal(first.FilledSupport.Runs, second.FilledSupport.Runs);
    }

    [Fact]
    public void LearningRejectsTemplateThatTouchesAnyRoiBoundary()
    {
        ImageFrame frame = SyntheticTemplate(touchesBoundary: true);

        HalconTemplateFeatureExtractionResult result = _extractor.Extract(frame, TemplateRoi);

        Assert.False(result.Success);
        Assert.Null(result.Features);
        Assert.Equal(
            TemplateMatchingDiagnosticCodes.ModelTemplateIncomplete,
            Assert.IsType<TemplateMatchingDiagnostic>(result.Diagnostic).Code);
    }

    [Fact]
    public void LearningRejectsWeakForegroundBackgroundContrast()
    {
        ImageFrame frame = SyntheticTemplate(weakContrast: true);

        HalconTemplateFeatureExtractionResult result = _extractor.Extract(frame, TemplateRoi);

        Assert.False(result.Success);
        Assert.Equal(
            TemplateMatchingDiagnosticCodes.ModelContrastWeak,
            Assert.IsType<TemplateMatchingDiagnostic>(result.Diagnostic).Code);
    }

    [Fact]
    public void LearningRejectsInsufficientInternalGroups()
    {
        ImageFrame frame = SyntheticTemplate(internalFeatureCount: 1);

        HalconTemplateFeatureExtractionResult result = _extractor.Extract(frame, TemplateRoi);

        Assert.False(result.Success);
        Assert.Equal(
            TemplateMatchingDiagnosticCodes.ModelInternalFeaturesWeak,
            Assert.IsType<TemplateMatchingDiagnostic>(result.Diagnostic).Code);
    }

    [Fact]
    public void LearningRejectsThreeGroupsOccupyingOnlyTwoSpatialBins()
    {
        ImageFrame frame = SyntheticTemplate(
            internalFeatureCount: 3,
            featureCenters:
            [
                new Point(116, 78),
                new Point(137, 91),
                new Point(182, 106)
            ]);

        HalconTemplateFeatureExtractionResult result = _extractor.Extract(frame, TemplateRoi);

        Assert.False(result.Success);
        Assert.Null(result.Features);
        Assert.Equal(
            TemplateMatchingDiagnosticCodes.ModelInternalFeaturesWeak,
            Assert.IsType<TemplateMatchingDiagnostic>(result.Diagnostic).Code);
    }

    [Fact]
    public void LearningAcceptsThreeGroupsAcrossThreeSpatialBins()
    {
        ImageFrame frame = SyntheticTemplate(
            internalFeatureCount: 3,
            featureCenters:
            [
                new Point(82, 78),
                new Point(130, 85),
                new Point(180, 105)
            ]);

        HalconTemplateFeatureExtractionResult result = _extractor.Extract(frame, TemplateRoi);

        Assert.True(result.Success, result.Diagnostic?.TechnicalDetails);
        Assert.True(Assert.IsType<HalconTemplateFeatureSet>(result.Features)
            .InnerFeatureGroups.Count >= 3);
    }

    [Theory]
    [InlineData(RoiShapeKind.Rectangle)]
    [InlineData(RoiShapeKind.RotatedRectangle)]
    [InlineData(RoiShapeKind.Circle)]
    [InlineData(RoiShapeKind.Polygon)]
    public void RoiMustBeCompletelyInsideImageAndIsNeverClamped(RoiShapeKind shape)
    {
        RoiDefinition roi = InvalidRoi(shape);

        HalconTemplateFeatureExtractionResult result =
            _extractor.Extract(SyntheticTemplate(), roi);

        Assert.False(result.Success);
        Assert.Equal(
            TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
            Assert.IsType<TemplateMatchingDiagnostic>(result.Diagnostic).Code);
    }

    [Fact]
    public void FeatureSetAndPersistentSupportTakeDeepSnapshots()
    {
        var outer = new List<Point2D> { new(1, 2), new(3, 4) };
        var innerGroup = new List<Point2D> { new(5.25, 6.5) };
        var groups = new List<IReadOnlyList<Point2D>> { innerGroup };
        var runs = new List<HalconSupportRun> { new(0, 0, 2) };
        var support = new HalconFilledSupportRegion(-1, -2, runs);

        var features = new HalconTemplateFeatureSet(
            20,
            10,
            5,
            10,
            5.5,
            10.5,
            true,
            outer,
            groups,
            1,
            support);
        outer[0] = new Point2D(100, 200);
        innerGroup[0] = new Point2D(300, 400);
        groups.Clear();
        runs[0] = new HalconSupportRun(8, 8, 8);

        Assert.Equal(new Point2D(1, 2), features.OuterContour[0]);
        Assert.Equal(new Point2D(5.25, 6.5), Assert.Single(Assert.Single(features.InnerFeatureGroups)));
        Assert.Equal(new HalconSupportRun(0, 0, 2), Assert.Single(features.FilledSupport.Runs));
    }

    [Fact]
    public void FilledSupportContainsPreservesRowHolesRunBoundariesAndOriginRounding()
    {
        const double originX = -10.25;
        const double originY = -20.75;
        var support = new HalconFilledSupportRegion(
            originX,
            originY,
            [
                new HalconSupportRun(0, 0, 1),
                new HalconSupportRun(2, 2, 2),
                new HalconSupportRun(2, 6, 2),
                new HalconSupportRun(4, 4, 1)
            ]);

        Assert.True(support.Contains(originX, originY));
        Assert.False(support.Contains(originX - 0.5, originY));
        Assert.False(support.Contains(originX + 0.5, originY));

        Assert.False(support.Contains(originX + 1.49, originY + 1.5));
        Assert.True(support.Contains(originX + 1.5, originY + 1.5));
        Assert.True(support.Contains(originX + 3.49, originY + 2));
        Assert.False(support.Contains(originX + 3.5, originY + 2));
        Assert.False(support.Contains(originX + 4.5, originY + 2));
        Assert.True(support.Contains(originX + 5.5, originY + 2));
        Assert.True(support.Contains(originX + 7.49, originY + 2));
        Assert.False(support.Contains(originX + 7.5, originY + 2));

        Assert.False(support.Contains(originX + 2, originY + 2.5));
        Assert.True(support.Contains(originX + 4, originY + 4));
    }

    [Fact]
    public void FilledSupportContainsHandlesMaximumColumnEndpointWithoutOverflow()
    {
        var support = new HalconFilledSupportRegion(
            0,
            0,
            [new HalconSupportRun(0, int.MaxValue, 1)]);

        Assert.Equal(int.MaxValue, support.MaximumColumn);
        Assert.False(support.Contains(int.MaxValue - 1.0, 0));
        Assert.True(support.Contains(int.MaxValue, 0));
    }

    [Fact]
    public void CompleteMetadataUsesPinnedSchemaAndRequiresGenerationAndChecksum()
    {
        HalconTemplateModelMetadata metadata = CreateMetadata("generation-1", new string('a', 64));

        Assert.Equal(1, metadata.SchemaVersion);
        Assert.Equal("Halcon", metadata.Engine);
        Assert.Equal(TemplateModelParameterCodec.HalconScaledShapeModelFormat, metadata.ModelFormat);
        Assert.Equal("halcon-scaled-shape-v1", metadata.ModelVersion);
        Assert.Equal("26050.0.0", metadata.ManagedPackageVersion);
        Assert.Equal("26050.0.0.0", metadata.ManagedAssemblyVersion);
        Assert.Equal("26.05.0.0", metadata.NativeRuntimeVersion);
        Assert.Throws<ArgumentException>(() => CreateMetadata(string.Empty, new string('a', 64)));
        Assert.Throws<ArgumentException>(() => CreateMetadata("generation-1", string.Empty));
    }

    [Fact]
    public void CompleteMetadataRejectsIncompleteLearnedEvidence()
    {
        Assert.Throws<ArgumentException>(() => CreateMetadata(
            "generation-1",
            new string('a', 64),
            outerContour: MetadataOuterContour(99)));
        Assert.Throws<ArgumentException>(() => CreateMetadata(
            "generation-1",
            new string('a', 64),
            innerFeatureGroups:
            [
                [new Point2D(-4, -4)],
                [new Point2D(4, 4)]
            ]));
    }

    [Fact]
    public void CompleteMetadataRequiresExactInnerQuorumAndGenerationFingerprint()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateMetadata(
            "generation-1",
            new string('a', 64),
            minimumValidInnerGroupCount: 2));
        Assert.Throws<ArgumentException>(() => CreateMetadata(
            "generation-1",
            new string('a', 64),
            generationParameterFingerprint: new string('f', 64)));
    }

    [Fact]
    public void CompleteMetadataRejectsNonFiniteValidationAudit()
    {
        HalconTemplateValidationDefaults defaults = ValidationDefaults() with
        {
            CandidateMinScore = double.NaN
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => CreateMetadata(
            "generation-1",
            new string('a', 64),
            validationDefaultsAtLearn: defaults));
    }

    [Theory]
    [MemberData(nameof(InvalidValidationDefaults))]
    public void CompleteMetadataRejectsValidationAuditOutsideCatalogDomain(
        object defaults)
    {
        Assert.ThrowsAny<ArgumentException>(() => CreateMetadata(
            "generation-1",
            new string('a', 64),
            validationDefaultsAtLearn: Assert.IsType<HalconTemplateValidationDefaults>(defaults)));
    }

    [Theory]
    [MemberData(nameof(ValidValidationBoundaryDefaults))]
    public void CompleteMetadataAcceptsValidationAuditAtCatalogBoundaries(object defaults)
    {
        HalconTemplateValidationDefaults expected =
            Assert.IsType<HalconTemplateValidationDefaults>(defaults);

        HalconTemplateModelMetadata metadata = CreateMetadata(
            "generation-1",
            new string('a', 64),
            validationDefaultsAtLearn: expected);

        Assert.Equal(expected, metadata.ValidationDefaultsAtLearn);
    }

    [Theory]
    [InlineData(double.MaxValue, double.MaxValue)]
    [InlineData(double.MaxValue, 1.0)]
    public void CompleteMetadataRejectsGenerationAngleEndThatIsNotFiniteAndGreater(
        double angleStartDeg,
        double angleExtentDeg)
    {
        var generationParameters = new TemplateModelGenerationParameters(
            angleStartDeg,
            angleExtentDeg,
            1,
            1,
            0);

        Assert.Throws<ArgumentOutOfRangeException>(() => CreateMetadata(
            "generation-1",
            new string('a', 64),
            generationParametersAtLearn: generationParameters));
    }

    [Fact]
    public void CompleteMetadataAcceptsLargeGenerationAnglesWithFiniteAdvancingEnd()
    {
        var generationParameters = new TemplateModelGenerationParameters(
            double.MaxValue / 4,
            double.MaxValue / 4,
            1,
            1,
            0);

        HalconTemplateModelMetadata metadata = CreateMetadata(
            "generation-1",
            new string('a', 64),
            generationParametersAtLearn: generationParameters);

        Assert.Equal(generationParameters, metadata.GenerationParameters);
    }

    [Fact]
    public void MetadataJsonUsesLowerCamelCaseAndRoundTripsCompleteMetadata()
    {
        HalconTemplateModelMetadata metadata = CreateMetadata("generation-1", new string('a', 64));
        byte[] json = HalconTemplateModelMetadataJson.Serialize(metadata);
        string text = Encoding.UTF8.GetString(json);

        HalconTemplateModelMetadata roundTrip = Assert.IsType<HalconTemplateModelMetadata>(
            HalconTemplateModelMetadataJson.Deserialize(json));
        Assert.Equal(metadata.Generation, roundTrip.Generation);
        Assert.Contains("\"schemaVersion\":1", text, StringComparison.Ordinal);
        Assert.Contains(
            "\"owner\":{\"recipeId\":\"recipe\",\"flowId\":\"flow\",\"toolId\":\"tool\"}",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("\"SchemaVersion\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Owner\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataJsonOmitsDerivedPropertiesAndRejectsInjectedCopies()
    {
        byte[] json = HalconTemplateModelMetadataJson.Serialize(
            CreateMetadata("generation-1", new string('a', 64)));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement filledSupport = root.GetProperty("filledSupport");

        Assert.False(root.TryGetProperty("templateWidth", out _));
        Assert.False(root.TryGetProperty("templateHeight", out _));
        Assert.False(filledSupport.TryGetProperty("area", out _));

        string text = Encoding.UTF8.GetString(json);
        string injectedTemplateWidth = text.Insert(
            text.LastIndexOf('}'),
            ",\"templateWidth\":220");
        const string filledSupportMarker = "\"filledSupport\":{";
        int filledSupportContentIndex =
            text.IndexOf(filledSupportMarker, StringComparison.Ordinal) + filledSupportMarker.Length;
        Assert.True(filledSupportContentIndex >= filledSupportMarker.Length);
        string injectedArea = text.Insert(filledSupportContentIndex, "\"area\":2,");

        Assert.Throws<JsonException>(() => HalconTemplateModelMetadataJson.Deserialize(
            Encoding.UTF8.GetBytes(injectedTemplateWidth)));
        Assert.Throws<JsonException>(() => HalconTemplateModelMetadataJson.Deserialize(
            Encoding.UTF8.GetBytes(injectedArea)));
    }

    [Fact]
    public void MetadataJsonPreservesVersionMismatchForTask11Classification()
    {
        byte[] json = HalconTemplateModelMetadataJson.Serialize(
            CreateMetadata("generation-1", new string('a', 64)));
        string text = Encoding.UTF8.GetString(json);
        string changed = text.Replace(
            "\"engine\":\"Halcon\"",
            "\"engine\":\"OpenCv\"",
            StringComparison.Ordinal);
        Assert.NotEqual(text, changed);

        HalconTemplateModelMetadata parsed = HalconTemplateModelMetadataJson.Deserialize(
            Encoding.UTF8.GetBytes(changed));

        Assert.Equal("OpenCv", parsed.Engine);
    }

    [Fact]
    public void MetadataJsonRejectsUnknownAndCaseInsensitiveDuplicateProperties()
    {
        byte[] json = HalconTemplateModelMetadataJson.Serialize(
            CreateMetadata("generation-1", new string('a', 64)));
        string text = Encoding.UTF8.GetString(json);
        string unknown = text.Insert(text.LastIndexOf('}'), ",\"unexpected\":true");
        string duplicate = text.Replace(
            "\"recipeId\":\"recipe\"",
            "\"RecipeId\":\"other\",\"recipeId\":\"recipe\"",
            StringComparison.Ordinal);

        Assert.Throws<JsonException>(() => HalconTemplateModelMetadataJson.Deserialize(
            Encoding.UTF8.GetBytes(unknown)));
        Assert.Throws<JsonException>(() => HalconTemplateModelMetadataJson.Deserialize(
            Encoding.UTF8.GetBytes(duplicate)));
    }

    [Fact]
    public void MetadataJsonStillRejectsStructurallyInvalidVersionHeader()
    {
        byte[] json = HalconTemplateModelMetadataJson.Serialize(
            CreateMetadata("generation-1", new string('a', 64)));
        string text = Encoding.UTF8.GetString(json);
        string invalid = text.Replace(
            "\"modelVersion\":\"halcon-scaled-shape-v1\"",
            "\"modelVersion\":\"\"",
            StringComparison.Ordinal);

        Assert.NotNull(Record.Exception(() => HalconTemplateModelMetadataJson.Deserialize(
            Encoding.UTF8.GetBytes(invalid))));
    }

    [Fact]
    public void PathologicalFrameDimensionsReturnConfigurationDiagnosticWithoutOverflow()
    {
        var frame = new ImageFrame(
            "overflow-frame",
            int.MaxValue,
            2,
            int.MaxValue,
            PixelFormatKind.Bgra32,
            [],
            DateTimeOffset.UnixEpoch,
            "Synthetic");

        HalconTemplateFeatureExtractionResult result = _extractor.Extract(frame, TemplateRoi);

        Assert.False(result.Success);
        Assert.Equal(
            TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
            Assert.IsType<TemplateMatchingDiagnostic>(result.Diagnostic).Code);
    }

    public static IEnumerable<object[]> InvalidValidationDefaults()
    {
        HalconTemplateValidationDefaults valid = ValidationDefaults();
        yield return [valid with { CandidateMinScore = -0.01 }];
        yield return [valid with { OuterCoverageMin = 1.01 }];
        yield return [valid with { InnerCoverageMin = double.PositiveInfinity }];
        yield return [valid with { EdgeTolerancePx = 0 }];
        yield return [valid with { EdgeTolerancePx = 100.01 }];
        yield return [valid with { PolarityAgreementMin = -0.01 }];
        yield return [valid with { CandidateMaxOverlap = 1.01 }];
        yield return [valid with { MaxOverlap = valid.CandidateMaxOverlap + 0.01 }];
        yield return [valid with { Greediness = double.NegativeInfinity }];
        yield return [valid with { SubPixel = string.Empty }];
        yield return [valid with { SubPixel = "none" }];
        yield return [valid with { CandidateLimit = 1 }];
        yield return [valid with { CandidateLimit = 513 }];
        yield return [valid with { OperatorTimeoutMs = 99 }];
        yield return [valid with { OperatorTimeoutMs = 60001 }];
        yield return [valid with { ExpectedCount = 0 }];
        yield return [valid with { ExpectedCount = 101 }];
        yield return [valid with { CandidateLimit = 2, ExpectedCount = 2 }];
    }

    public static IEnumerable<object[]> ValidValidationBoundaryDefaults()
    {
        HalconTemplateValidationDefaults valid = ValidationDefaults();
        yield return
        [
            valid with
            {
                SubPixel = "least_squares",
                CandidateLimit = 2,
                OperatorTimeoutMs = 100,
                ExpectedCount = 1
            }
        ];
        yield return
        [
            valid with
            {
                SubPixel = "least_squares",
                CandidateLimit = 512,
                OperatorTimeoutMs = 60000,
                ExpectedCount = 100
            }
        ];
    }

    private static HalconTemplateModelMetadata CreateMetadata(
        string generation,
        string checksum,
        IReadOnlyList<Point2D>? outerContour = null,
        IReadOnlyList<IReadOnlyList<Point2D>>? innerFeatureGroups = null,
        int? minimumValidInnerGroupCount = null,
        string? generationParameterFingerprint = null,
        HalconTemplateValidationDefaults? validationDefaultsAtLearn = null,
        TemplateModelGenerationParameters? generationParametersAtLearn = null)
    {
        HalconTemplateMatchingParameters parameters = TemplateMatchingParameterCatalog.ParseHalcon(
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single),
            TemplateMatchCardinality.Single);
        TemplateModelGenerationParameters generationParameters =
            generationParametersAtLearn ?? TemplateModelGenerationParameters.From(parameters);
        outerContour ??= MetadataOuterContour(100);
        innerFeatureGroups ??=
        [
            [new Point2D(-8.25, -6.5)],
            [new Point2D(7.5, -4.25)],
            [new Point2D(1.25, 8.75)]
        ];
        minimumValidInnerGroupCount ??= Math.Max(
            2,
            (int)Math.Ceiling(innerFeatureGroups.Count * 0.67));
        return new HalconTemplateModelMetadata(
            new TemplateModelOwner("recipe", "flow", "tool"),
            generation,
            "model-generation-1.shm",
            checksum,
            new TemplateLearnedGeometry(new Pose2D(130, 90, 0), 220, 140),
            70,
            110,
            71,
            111,
            true,
            outerContour,
            innerFeatureGroups,
            minimumValidInnerGroupCount.Value,
            new HalconFilledSupportRegion(-1, -1, [new HalconSupportRun(0, 0, 2)]),
            generationParameters,
            generationParameterFingerprint ?? TemplateModelGenerationFingerprint.Compute(generationParameters),
            validationDefaultsAtLearn ?? HalconTemplateValidationDefaults.From(parameters));
    }

    private static HalconTemplateValidationDefaults ValidationDefaults()
    {
        HalconTemplateMatchingParameters parameters = TemplateMatchingParameterCatalog.ParseHalcon(
            TemplateMatchingParameterCatalog.CreateStrictDefaults(TemplateMatchCardinality.Single),
            TemplateMatchCardinality.Single);
        return HalconTemplateValidationDefaults.From(parameters);
    }

    private static IReadOnlyList<Point2D> MetadataOuterContour(int count)
    {
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                double angle = index * 2 * Math.PI / count;
                return new Point2D(10 * Math.Cos(angle), 10 * Math.Sin(angle));
            })
            .ToArray();
    }

    private static ImageFrame SyntheticTemplate(
        bool touchesBoundary = false,
        bool weakContrast = false,
        int internalFeatureCount = 4,
        IReadOnlyList<Point>? featureCenters = null)
    {
        const int width = 260;
        const int height = 180;
        byte background = weakContrast ? (byte)132 : (byte)235;
        byte foreground = weakContrast ? (byte)122 : (byte)35;
        byte internalValue = weakContrast ? (byte)130 : (byte)220;
        using var image = new Mat(height, width, MatType.CV_8UC1, new Scalar(background));
        int left = touchesBoundary ? 20 : 45;
        Point[] product =
        [
            new(left, 57),
            new(178, 50),
            new(213, 77),
            new(190, 124),
            new(72, 129),
            new(43, 100)
        ];
        Cv2.FillPoly(image, [product], new Scalar(foreground));

        IReadOnlyList<Point> centers = featureCenters ??
        [
            new Point(82, 78),
            new Point(128, 70),
            new Point(182, 90),
            new Point(112, 108),
            new Point(163, 111)
        ];
        for (var index = 0; index < Math.Min(internalFeatureCount, centers.Count); index++)
        {
            Point center = centers[index];
            if (index % 2 == 0)
            {
                Cv2.Ellipse(
                    image,
                    center,
                    new Size(7 + index, 4 + index / 2),
                    index * 11,
                    0,
                    360,
                    new Scalar(internalValue),
                    -1,
                    LineTypes.AntiAlias);
            }
            else
            {
                Cv2.Rectangle(
                    image,
                    new Rect(center.X - 7, center.Y - 4, 15, 9),
                    new Scalar(internalValue),
                    -1,
                    LineTypes.AntiAlias);
            }
        }

        image.GetArray(out byte[] pixels);
        return new ImageFrame(
            "halcon-feature-template",
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UnixEpoch,
            "Synthetic");
    }

    private static RoiDefinition InvalidRoi(RoiShapeKind shape)
    {
        return shape switch
        {
            RoiShapeKind.Rectangle => new RoiDefinition
            {
                Shape = shape,
                X = -1,
                Y = 10,
                Width = 60,
                Height = 60
            },
            RoiShapeKind.RotatedRectangle => new RoiDefinition
            {
                Shape = shape,
                X = 10,
                Y = 10,
                Width = 70,
                Height = 40,
                Angle = 35
            },
            RoiShapeKind.Circle => new RoiDefinition
            {
                Shape = shape,
                X = 20,
                Y = 20,
                Radius = 25
            },
            RoiShapeKind.Polygon => new RoiDefinition
            {
                Shape = shape,
                Points = [new Point2D(-1, 10), new Point2D(70, 10), new Point2D(50, 80)]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null)
        };
    }

    private static double MinimumDistance(Point2D point, IReadOnlyList<Point2D> contour)
    {
        return contour.Min(candidate => Math.Sqrt(
            Math.Pow(candidate.X - point.X, 2) + Math.Pow(candidate.Y - point.Y, 2)));
    }

    private static bool IsSubPixel(double value)
    {
        return Math.Abs(value - Math.Round(value)) > 1e-4;
    }

    private static double SignedArea(IReadOnlyList<Point2D> points)
    {
        double area = 0;
        for (var index = 0; index < points.Count; index++)
        {
            Point2D current = points[index];
            Point2D next = points[(index + 1) % points.Count];
            area += current.X * next.Y - next.X * current.Y;
        }

        return area / 2.0;
    }

    private static (double MinimumX, double MinimumY, double MaximumX, double MaximumY)
        FilledSupportBounds(HalconFilledSupportRegion support)
    {
        return (
            support.Runs.Min(run => support.OriginX + run.ColumnStart),
            support.Runs.Min(run => support.OriginY + run.Row),
            support.Runs.Max(run => support.OriginX + run.ColumnStart + run.Length - 1),
            support.Runs.Max(run => support.OriginY + run.Row));
    }
}
