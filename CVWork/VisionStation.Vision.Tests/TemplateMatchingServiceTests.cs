using System.Globalization;
using System.Text.Json;
using VisionStation.Domain;
using Xunit;

namespace VisionStation.Vision.Tests;

public sealed class TemplateMatchingServiceTests
{
    [Fact]
    public void RequestsTakeCaseInsensitiveReadOnlyParameterSnapshots()
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ENGINE"] = "Halcon",
            ["threshold"] = "1"
        };

        var learning = new TemplateLearningRequest(
            Owner(),
            Frame(),
            TemplateRoi(),
            null,
            parameters);
        var matching = new TemplateMatchingRequest(
            Owner(),
            Frame(),
            null,
            parameters,
            TemplateMatchCardinality.Single,
            1);

        parameters["threshold"] = "2";
        parameters["new"] = "value";

        Assert.Equal("Halcon", learning.Parameters["engine"]);
        Assert.Equal("1", matching.Parameters["THRESHOLD"]);
        Assert.False(learning.Parameters.ContainsKey("new"));
        Assert.False(matching.Parameters.ContainsKey("new"));
        Assert.False(learning.Parameters is IDictionary<string, string> learningDictionary && !learningDictionary.IsReadOnly);
        Assert.False(matching.Parameters is IDictionary<string, string> matchingDictionary && !matchingDictionary.IsReadOnly);
    }

    [Fact]
    public void RequestsRejectAmbiguousCaseDuplicateKeysAndSnapshotRoiPoints()
    {
        var ambiguous = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["engine"] = "OpenCv",
            ["ENGINE"] = "Halcon"
        };
        var points = new List<Point2D>
        {
            new(1, 1),
            new(20, 1),
            new(20, 20)
        };
        var roi = new RoiDefinition
        {
            Id = "polygon",
            Shape = RoiShapeKind.Polygon,
            Points = points
        };

        Assert.Throws<ArgumentException>(() => new TemplateMatchingRequest(
            Owner(), Frame(), null, ambiguous, TemplateMatchCardinality.Single, 1));
        Assert.Throws<ArgumentException>(() => new TemplateMatchingRequest(
            Owner(),
            Frame(),
            null,
            new Dictionary<string, string> { ["engine"] = null! },
            TemplateMatchCardinality.Single,
            1));

        var request = new TemplateLearningRequest(
            Owner(), Frame(), roi, roi, new Dictionary<string, string>());
        points[0] = new Point2D(99, 99);

        Assert.Equal(new Point2D(1, 1), request.TemplateRoi.Points[0]);
        Assert.Equal(new Point2D(1, 1), request.SearchRoi!.Points[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemplateMatchingRequest(
            Owner(), Frame(), null, new Dictionary<string, string>(), TemplateMatchCardinality.Single, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemplateMatchingRequest(
            Owner(), Frame(), null, new Dictionary<string, string>(), TemplateMatchCardinality.ExactCount, 101));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TemplateMatchingRequest(
            Owner(), Frame(), null, new Dictionary<string, string>(), (TemplateMatchCardinality)99, 1));
    }

    [Fact]
    public async Task SingleAndMultiUseTheSameBatchBackendContract()
    {
        var halcon = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon);
        await using var service = TemplateMatchingService.ForTests(halcon);

        await service.MatchAsync(Request("Halcon", TemplateMatchCardinality.Single, 1), default);
        await service.MatchAsync(Request("Halcon", TemplateMatchCardinality.ExactCount, 3), default);

        Assert.Collection(
            halcon.MatchRequests,
            request => Assert.Equal(TemplateMatchCardinality.Single, request.Cardinality),
            request => Assert.Equal(
                (TemplateMatchCardinality.ExactCount, 3),
                (request.Cardinality, request.ExpectedCount)));
    }

    [Fact]
    public async Task MissingOpenCvAndHalconEachInvokeOnlyTheirResolvedBackend()
    {
        var openCv = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.OpenCv);
        var managed = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.ManagedNcc);
        var halcon = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon);
        await using var service = TemplateMatchingService.ForTests(openCv, managed, halcon);

        var missingResult = await service.MatchAsync(Request(null), default);
        var openCvResult = await service.MatchAsync(Request("  opencv  "), default);
        var managedResult = await service.MatchAsync(Request("ManagedNcc"), default);
        var halconResult = await service.MatchAsync(Request("Halcon"), default);
        await service.LearnAsync(LearningRequest("Halcon"), default);

        Assert.Equal(2, openCv.MatchRequests.Count);
        Assert.Single(managed.MatchRequests);
        Assert.Single(halcon.MatchRequests);
        Assert.Single(halcon.LearningRequests);
        Assert.Equal(TemplateMatchingEngine.OpenCv, missingResult.Engine);
        Assert.Equal(TemplateMatchingEngine.OpenCv, openCvResult.Engine);
        Assert.Equal(TemplateMatchingEngine.ManagedNcc, managedResult.Engine);
        Assert.Equal(TemplateMatchingEngine.Halcon, halconResult.Engine);
    }

    [Fact]
    public async Task UnknownEngineReturnsUnknownBatchWithoutCallingAnyBackend()
    {
        var openCv = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.OpenCv);
        var halcon = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon);
        await using var service = TemplateMatchingService.ForTests(openCv, halcon);

        var result = await service.MatchAsync(Request("Halconn"), default);

        Assert.Equal(TemplateMatchingEngine.Unknown, result.Engine);
        Assert.False(result.HasMatch);
        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigUnknownEngine, result.Diagnostic?.Code);
        Assert.Empty(openCv.MatchRequests);
        Assert.Empty(halcon.MatchRequests);
    }

    [Fact]
    public async Task BackendNgDiagnosticIsPassedThroughUnchanged()
    {
        var diagnostic = TemplateMatchingDiagnostics.Create(
            TemplateMatchingDiagnosticCodes.MatchOuterContourWeak,
            "backend-detail");
        var halcon = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon)
        {
            MatchHandler = (_, _) => Task.FromResult(
                TemplateMatchingTestResults.NoMatch(TemplateMatchingEngine.Halcon, diagnostic))
        };
        await using var service = TemplateMatchingService.ForTests(halcon);

        var result = await service.MatchAsync(Request("Halcon"), default);

        Assert.Same(diagnostic, result.Diagnostic);
        Assert.Equal(TemplateMatchingEngine.Halcon, result.Engine);
        Assert.Equal("backend-detail", result.Diagnostic?.TechnicalDetails);
    }

    [Fact]
    public async Task ServicePreservesRejectedCandidatesMessageAndHasMatchFromBackend()
    {
        var candidate = new TemplateMatchBatchCandidate(
            new Pose2D(12, 14, 3) { Scale = 1.02 },
            0.4,
            10,
            8,
            Array.Empty<IReadOnlyList<Point2D>>(),
            [[new Point2D(7, 10), new Point2D(17, 10), new Point2D(17, 18), new Point2D(7, 18)]]);
        var diagnostic = TemplateMatchingDiagnostics.Create(TemplateMatchingDiagnosticCodes.MatchPolarityMismatch);
        var backend = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon)
        {
            MatchHandler = (_, _) => Task.FromResult(new TemplateMatchBatchResult(
                TemplateMatchingEngine.OpenCv,
                InspectionOutcome.Ng,
                false,
                [candidate],
                new TemplateSearchRegion(1, 2, 30, 40),
                "backend-message",
                true,
                diagnostic))
        };
        await using var service = TemplateMatchingService.ForTests(backend);

        var result = await service.MatchAsync(Request("Halcon"), default);

        Assert.Equal(TemplateMatchingEngine.Halcon, result.Engine);
        Assert.False(result.HasMatch);
        Assert.Same(candidate, Assert.Single(result.Matches));
        Assert.Equal("backend-message", result.Message);
        Assert.True(result.UsedAutoTemplate);
        Assert.Same(diagnostic, result.Diagnostic);
    }

    [Fact]
    public async Task ConfigurationExceptionBecomesStructuredNgButUnknownRuntimeExceptionPropagates()
    {
        var backend = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon)
        {
            MatchHandler = (_, _) => Task.FromException<TemplateMatchBatchResult>(
                new TemplateMatchingConfigurationException(
                    TemplateMatchingDiagnostics.Create(
                        TemplateMatchingDiagnosticCodes.ConfigInvalidParameter,
                        "bad-value")))
        };
        await using var service = TemplateMatchingService.ForTests(backend);

        var configuredFailure = await service.MatchAsync(Request("Halcon"), default);

        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, configuredFailure.Diagnostic?.Code);
        Assert.Equal("bad-value", configuredFailure.Diagnostic?.TechnicalDetails);

        backend.MatchHandler = (_, _) => Task.FromException<TemplateMatchBatchResult>(new InvalidOperationException("boom"));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MatchAsync(Request("Halcon"), default));
        Assert.Equal("boom", exception.Message);
    }

    [Fact]
    public async Task BackendLearningFailureCannotPublishNewModelParameters()
    {
        var diagnostic = TemplateMatchingDiagnostics.Create(TemplateMatchingDiagnosticCodes.ModelLoadFailed);
        var backend = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon)
        {
            LearnHandler = (_, _) => Task.FromResult(new TemplateLearningResult(
                TemplateMatchingEngine.OpenCv,
                false,
                new Dictionary<string, string> { ["halcon.modelPath"] = "stale.shm" },
                "Learning failed.",
                diagnostic))
        };
        await using var service = TemplateMatchingService.ForTests(backend);

        var result = await service.LearnAsync(LearningRequest("Halcon"), default);

        Assert.False(result.Success);
        Assert.Equal(TemplateMatchingEngine.Halcon, result.Engine);
        Assert.Empty(result.Parameters);
        Assert.Equal("Learning failed.", result.Message);
        Assert.Same(diagnostic, result.Diagnostic);
    }

    [Fact]
    public async Task CancellationBeforeDispatchAndBackendCancellationBothPropagate()
    {
        var backend = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon);
        await using var service = TemplateMatchingService.ForTests(backend);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.MatchAsync(Request("Halcon"), cancellation.Token));
        Assert.Empty(backend.MatchRequests);

        backend.MatchHandler = (_, token) => Task.FromException<TemplateMatchBatchResult>(
            new OperationCanceledException(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.MatchAsync(Request("Halcon"), default));
    }

    [Fact]
    public async Task DisposeRejectsNewRequestsWaitsForInFlightWorkAndDisposesEachBackendOnce()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openCv = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.OpenCv)
        {
            MatchHandler = async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
                return TemplateMatchingTestResults.NoMatch(TemplateMatchingEngine.OpenCv);
            }
        };
        var halcon = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon);
        var service = TemplateMatchingService.ForTests(openCv, halcon);

        var matchTask = service.MatchAsync(Request(null), default);
        await entered.Task;
        var disposeTask = service.DisposeAsync().AsTask();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            service.MatchAsync(Request("Halcon"), default));
        Assert.False(disposeTask.IsCompleted);
        Assert.Equal(0, openCv.DisposeCount);
        Assert.Equal(0, halcon.DisposeCount);

        release.SetResult();
        await matchTask;
        await disposeTask;
        await service.DisposeAsync();

        Assert.Equal(1, openCv.DisposeCount);
        Assert.Equal(1, halcon.DisposeCount);
    }

    [Fact]
    public async Task DisposeReleasesHalconBackendBeforeOtherBackendsRegardlessOfRegistrationOrder()
    {
        var disposalOrder = new List<TemplateMatchingEngine>();
        var openCv = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.OpenCv)
        {
            DisposeHandler = Record(TemplateMatchingEngine.OpenCv)
        };
        var managed = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.ManagedNcc)
        {
            DisposeHandler = Record(TemplateMatchingEngine.ManagedNcc)
        };
        var halcon = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon)
        {
            DisposeHandler = Record(TemplateMatchingEngine.Halcon)
        };
        var service = TemplateMatchingService.ForTests(openCv, managed, halcon);

        await service.DisposeAsync();

        Assert.Equal(
            [
                TemplateMatchingEngine.Halcon,
                TemplateMatchingEngine.OpenCv,
                TemplateMatchingEngine.ManagedNcc
            ],
            disposalOrder);

        Func<ValueTask> Record(TemplateMatchingEngine engine)
        {
            return () =>
            {
                disposalOrder.Add(engine);
                return ValueTask.CompletedTask;
            };
        }
    }

    [Fact]
    public async Task DisposeAwaitsHalconBackendBeforeStartingOtherBackendCleanup()
    {
        var halconDisposeEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHalconDispose = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var otherDisposeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var openCv = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.OpenCv)
        {
            DisposeHandler = () =>
            {
                otherDisposeStarted.TrySetResult();
                return ValueTask.CompletedTask;
            }
        };
        var halcon = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon)
        {
            DisposeHandler = async () =>
            {
                halconDisposeEntered.TrySetResult();
                await releaseHalconDispose.Task;
            }
        };
        var service = TemplateMatchingService.ForTests(openCv, halcon);

        Task disposeTask = service.DisposeAsync().AsTask();
        await halconDisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.False(otherDisposeStarted.Task.IsCompleted);
        }
        finally
        {
            releaseHalconDispose.TrySetResult();
        }

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(otherDisposeStarted.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RegistryCopiesInputRejectsDuplicateAndUnknownEngineKeys()
    {
        var openCv = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.OpenCv);
        var replacement = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon);
        ITemplateMatchingBackend[] source = [openCv];
        await using var service = TemplateMatchingService.ForTests(source);
        source[0] = replacement;

        await service.MatchAsync(Request(null), default);

        Assert.Single(openCv.MatchRequests);
        Assert.Empty(replacement.MatchRequests);
        Assert.Throws<ArgumentException>(() => TemplateMatchingService.ForTests(
            new RecordingTemplateMatchingBackend(TemplateMatchingEngine.OpenCv),
            new RecordingTemplateMatchingBackend(TemplateMatchingEngine.OpenCv)));
        Assert.Throws<ArgumentException>(() => TemplateMatchingService.ForTests(
            new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Unknown)));
        Assert.Throws<ArgumentException>(() => TemplateMatchingService.ForTests(
            new RecordingTemplateMatchingBackend((TemplateMatchingEngine)99)));
        Assert.Throws<ArgumentException>(() => TemplateMatchingService.ForTests(
            new ITemplateMatchingBackend[] { null! }));
    }

    [Fact]
    public async Task ServiceDoesNotSerializeIndependentBackendRequests()
    {
        var entered = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon)
        {
            MatchHandler = async (_, _) =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                {
                    bothEntered.SetResult();
                }

                await release.Task;
                return TemplateMatchingTestResults.NoMatch(TemplateMatchingEngine.Halcon);
            }
        };
        await using var service = TemplateMatchingService.ForTests(backend);

        var first = service.MatchAsync(Request("Halcon"), default);
        var second = service.MatchAsync(Request("Halcon"), default);
        try
        {
            await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, entered);
    }

    [Fact]
    public async Task ConcurrentDisposeSharesFailureAndStillDisposesEveryBackendOnce()
    {
        var failure = new InvalidOperationException("dispose-failed");
        var first = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.OpenCv)
        {
            DisposeHandler = () => ValueTask.FromException(failure)
        };
        var second = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon);
        var service = TemplateMatchingService.ForTests(first, second);

        var dispose1 = service.DisposeAsync().AsTask();
        var dispose2 = service.DisposeAsync().AsTask();
        var exception1 = await Assert.ThrowsAsync<InvalidOperationException>(() => dispose1);
        var exception2 = await Assert.ThrowsAsync<InvalidOperationException>(() => dispose2);

        Assert.Same(failure, exception1);
        Assert.Same(failure, exception2);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task ConcurrentDisposeSharesAggregateAndPreservesEveryBackendFailure()
    {
        var halconFailure = new InvalidOperationException("halcon-dispose-failed");
        var openCvFailure = new IOException("opencv-dispose-failed");
        var openCv = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.OpenCv)
        {
            DisposeHandler = () => ValueTask.FromException(openCvFailure)
        };
        var managed = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.ManagedNcc);
        var halcon = new RecordingTemplateMatchingBackend(TemplateMatchingEngine.Halcon)
        {
            DisposeHandler = () => ValueTask.FromException(halconFailure)
        };
        var service = TemplateMatchingService.ForTests(openCv, managed, halcon);

        var dispose1 = service.DisposeAsync().AsTask();
        var dispose2 = service.DisposeAsync().AsTask();
        var exception1 = await Assert.ThrowsAsync<AggregateException>(() => dispose1);
        var exception2 = await Assert.ThrowsAsync<AggregateException>(() => dispose2);

        Assert.Same(exception1, exception2);
        Assert.Equal([halconFailure, openCvFailure], exception1.InnerExceptions);
        Assert.Equal(1, openCv.DisposeCount);
        Assert.Equal(1, managed.DisposeCount);
        Assert.Equal(1, halcon.DisposeCount);
    }

    [Fact]
    public async Task LegacyOnlyRequiresAServiceBackendForHalconAndKeepsManagedMultiUnsupported()
    {
        await using var service = TemplateMatchingService.CreateLegacyOnly();

        var halcon = await service.MatchAsync(Request("Halcon"), default);
        var managedMulti = await service.MatchAsync(
            Request("ManagedNcc", TemplateMatchCardinality.ExactCount, 2),
            default);

        Assert.Equal(TemplateMatchingEngine.Halcon, halcon.Engine);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigServiceRequired, halcon.Diagnostic?.Code);
        Assert.Equal(TemplateMatchingEngine.ManagedNcc, managedMulti.Engine);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigUnsupportedMode, managedMulti.Diagnostic?.Code);
    }

    [Theory]
    [InlineData("OpenCv")]
    [InlineData("ManagedNcc")]
    public async Task LegacySingleAdaptersUseExplicitTemplateRoiAndReturnCompleteContours(string engine)
    {
        await using var service = TemplateMatchingService.CreateLegacyOnly();
        var frame = PatternFrame();
        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = engine,
            ["matchMode"] = "GrayNcc",
            ["minScore"] = "0.5",
            ["searchStep"] = "1",
            ["templateRoiX"] = "0"
        };

        var learned = await service.LearnAsync(
            new TemplateLearningRequest(Owner(), frame, TemplateRoi(), null, source),
            default);
        var runtime = new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in learned.Parameters)
        {
            runtime[pair.Key] = pair.Value;
        }

        var matched = await service.MatchAsync(
            new TemplateMatchingRequest(
                Owner(),
                frame,
                null,
                runtime,
                TemplateMatchCardinality.Single,
                1),
            default);

        Assert.True(learned.Success);
        Assert.Equal("template-roi", learned.Parameters["templateSourceRoiId"]);
        Assert.Equal("8", learned.Parameters["templateRoiX"]);
        Assert.Equal("0", source["templateRoiX"]);
        Assert.True(matched.HasMatch);
        var candidate = Assert.Single(matched.Matches);
        Assert.NotEmpty(candidate.TemplateRoiContours);
        Assert.All(candidate.TemplateRoiContours, contour =>
        {
            Assert.True(contour.Count >= 3);
            Assert.All(contour, point => Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)));
        });
    }

    [Theory]
    [MemberData(nameof(InvalidExplicitTemplateRois))]
    public async Task LegacyLearningRejectsInvalidExplicitTemplateRoiBeforeCallingMatcher(
        string scenario,
        RoiDefinition templateRoi)
    {
        await using var service = TemplateMatchingService.CreateLegacyOnly();
        var parameters = new Dictionary<string, string>
        {
            ["engine"] = "OpenCv",
            ["matchMode"] = "GrayNcc"
        };

        var result = await service.LearnAsync(
            new TemplateLearningRequest(
                Owner(),
                PatternFrame(),
                templateRoi,
                null,
                parameters),
            default);
        AssertInvalidLearningResult(result, scenario);

        var sentinelResult = await service.LearnAsync(
            new TemplateLearningRequest(
                Owner(),
                InvalidLearningSentinelFrame(),
                templateRoi,
                null,
                parameters),
            default);
        AssertInvalidLearningResult(sentinelResult, $"{scenario}-must-not-call-matcher");
    }

    [Theory]
    [MemberData(nameof(ValidExplicitTemplateRois))]
    public async Task LegacyLearningSupportsEveryValidExplicitTemplateRoiShape(
        string scenario,
        RoiDefinition templateRoi)
    {
        await using var service = TemplateMatchingService.CreateLegacyOnly();

        var result = await service.LearnAsync(
            new TemplateLearningRequest(
                Owner(),
                PatternFrame(),
                templateRoi,
                null,
                new Dictionary<string, string>
                {
                    ["engine"] = "OpenCv",
                    ["matchMode"] = "GrayNcc"
                }),
            default);

        Assert.True(result.Success, $"{scenario}: {result.Message} {result.Diagnostic?.TechnicalDetails}");
        Assert.Null(result.Diagnostic);
        Assert.Equal(templateRoi.Shape.ToString(), result.Parameters["templateRoiShape"]);
        Assert.Equal(templateRoi.Id, result.Parameters["templateSourceRoiId"]);
    }

    [Theory]
    [MemberData(nameof(ValidExplicitTemplateRoisWithNonFiniteInactiveFields))]
    public async Task LegacyLearningNormalizesNonActiveTemplateRoiFieldsWithoutMutatingRequest(
        string scenario,
        string engine,
        RoiDefinition templateRoi)
    {
        await using var service = TemplateMatchingService.CreateLegacyOnly();
        var request = new TemplateLearningRequest(
            Owner(),
            PatternFrame(),
            templateRoi,
            null,
            new Dictionary<string, string>
            {
                ["engine"] = engine,
                ["matchMode"] = "GrayNcc"
            });

        var result = await service.LearnAsync(request, default);

        Assert.True(result.Success, $"{scenario}: {result.Message} {result.Diagnostic?.TechnicalDetails}");
        var normalized = JsonSerializer.Deserialize<RoiDefinition>(result.Parameters["templateRoiJson"]);
        Assert.NotNull(normalized);
        Assert.Equal(request.TemplateRoi.Id, normalized.Id);
        Assert.Equal(request.TemplateRoi.Name, normalized.Name);
        Assert.Equal(request.TemplateRoi.Shape, normalized.Shape);
        Assert.Equal(normalized.Id, result.Parameters["templateSourceRoiId"]);
        var legacyValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        Assert.All(
            new[] { "templateRoiX", "templateRoiY", "templateRoiWidth", "templateRoiHeight", "templateRoiAngle" },
            key =>
            {
                Assert.True(
                    double.TryParse(
                        result.Parameters[key],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var value),
                    $"{scenario}: {key} is not numeric.");
                Assert.True(double.IsFinite(value), $"{scenario}: {key} is not finite.");
                legacyValues[key] = value;
            });
        AssertNormalizedInactiveFields(request.TemplateRoi, normalized);
        AssertNormalizedLegacyRoiKeys(normalized, legacyValues);
    }

    [Theory]
    [InlineData("OpenCv")]
    [InlineData("ManagedNcc")]
    public async Task LegacyLearningNormalizesNullTemplateRoiIdentityWithoutPublishingNullParameters(
        string engine)
    {
        await using var service = TemplateMatchingService.CreateLegacyOnly();
        var request = new TemplateLearningRequest(
            Owner(),
            PatternFrame(),
            new RoiDefinition
            {
                Id = null!,
                Name = null!,
                Shape = RoiShapeKind.Rectangle,
                X = 8,
                Y = 8,
                Width = 16,
                Height = 16
            },
            null,
            new Dictionary<string, string>
            {
                ["engine"] = engine,
                ["matchMode"] = "GrayNcc"
            });

        var result = await service.LearnAsync(request, default);

        Assert.True(result.Success, $"{result.Message} {result.Diagnostic?.TechnicalDetails}");
        Assert.Null(request.TemplateRoi.Id);
        Assert.Null(request.TemplateRoi.Name);
        Assert.All(result.Parameters, pair => Assert.NotNull(pair.Value));
        Assert.Equal(string.Empty, result.Parameters["templateSourceRoiId"]);
        var normalized = JsonSerializer.Deserialize<RoiDefinition>(result.Parameters["templateRoiJson"]);
        Assert.NotNull(normalized);
        Assert.Equal(string.Empty, normalized.Id);
        Assert.Equal(string.Empty, normalized.Name);
    }

    [Fact]
    public async Task OpenCvSingleFailsClosedWhenLegacyMatcherReturnsNoValidTemplateRoiContour()
    {
        await using var service = TemplateMatchingService.CreateLegacyOnly();
        var frame = PatternFrame();
        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["matchMode"] = "GrayNcc",
            ["minScore"] = "0.5"
        };
        var learned = await service.LearnAsync(
            new TemplateLearningRequest(Owner(), frame, TemplateRoi(), null, source),
            default);
        var runtime = new Dictionary<string, string>(learned.Parameters, StringComparer.OrdinalIgnoreCase)
        {
            ["templateRoiJson"] = "{"
        };

        var result = await service.MatchAsync(new TemplateMatchingRequest(
            Owner(), frame, null, runtime, TemplateMatchCardinality.Single, 1), default);

        Assert.False(result.HasMatch);
        Assert.Empty(result.Matches);
        Assert.Equal(TemplateMatchingDiagnosticCodes.MatchOperatorFailed, result.Diagnostic?.Code);
    }

    [Fact]
    public async Task OpenCvSingleAutoLearnPublishesItsEffectiveReferenceGeometryWithoutMutatingRequest()
    {
        await using var service = TemplateMatchingService.CreateLegacyOnly();
        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["matchMode"] = "GrayNcc",
            ["autoLearnTemplate"] = "true",
            ["minScore"] = "0.5"
        };
        var request = new TemplateMatchingRequest(
            Owner(),
            PatternFrame(),
            null,
            source,
            TemplateMatchCardinality.Single,
            1);

        var result = await service.MatchAsync(request, default);

        Assert.True(result.HasMatch, result.Message);
        Assert.True(result.UsedAutoTemplate);
        var candidate = Assert.Single(result.Matches);
        Assert.Equal((40, 40), (candidate.TemplateWidth, candidate.TemplateHeight));
        Assert.Equal(4, Assert.Single(candidate.TemplateRoiContours).Count);
        Assert.False(source.ContainsKey("templateWidth"));
        Assert.False(request.Parameters.ContainsKey("templateWidth"));
        Assert.False(request.Parameters.ContainsKey("templatePixels"));
    }

    [Fact]
    public async Task OpenCvSingleAutoLearnPreCancellationAndRuntimeFailureAreNotConvertedToNg()
    {
        await using var service = TemplateMatchingService.CreateLegacyOnly();
        var parameters = new Dictionary<string, string>
        {
            ["engine"] = "OpenCv",
            ["matchMode"] = "GrayNcc",
            ["autoLearnTemplate"] = "true"
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.MatchAsync(
            new TemplateMatchingRequest(
                Owner(), PatternFrame(), null, parameters, TemplateMatchCardinality.Single, 1),
            cancellation.Token));

        var invalidFrame = new ImageFrame(
            "invalid-auto-learn",
            0,
            0,
            0,
            PixelFormatKind.Gray8,
            Array.Empty<byte>(),
            DateTimeOffset.UnixEpoch,
            "test");
        await Assert.ThrowsAnyAsync<Exception>(() => service.MatchAsync(
            new TemplateMatchingRequest(
                Owner(), invalidFrame, null, parameters, TemplateMatchCardinality.Single, 1),
            default));
    }

    [Fact]
    public void LegacyMultiContourTransformUsesOriginalReferenceGeometryAtAngleAndScale()
    {
        var roi = new RoiDefinition
        {
            Shape = RoiShapeKind.Rectangle,
            X = 0,
            Y = 0,
            Width = 40,
            Height = 20
        };
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["templateX"] = "0",
            ["templateY"] = "0",
            ["templateWidth"] = "40",
            ["templateHeight"] = "20",
            ["templateRoiJson"] = JsonSerializer.Serialize(roi)
        };

        var success = LegacyTemplateMatchingAdapterSupport.TryCreateTemplateRoiContours(
            parameters,
            new Pose2D(100, 100, 35) { Scale = 1.1 },
            out var contours,
            out var width,
            out var height,
            out var technicalDetails);

        Assert.True(success, technicalDetails);
        Assert.Equal((40, 20), (width, height));
        var contour = Assert.Single(contours);
        Assert.Equal(88.288, contour[0].X, 3);
        Assert.Equal(78.371, contour[0].Y, 3);
    }

    [Theory]
    [InlineData(RoiShapeKind.Rectangle, 4)]
    [InlineData(RoiShapeKind.RotatedRectangle, 4)]
    [InlineData(RoiShapeKind.Circle, 64)]
    [InlineData(RoiShapeKind.Polygon, 4)]
    public void LegacyMultiContourTransformSupportsEveryTemplateRoiShape(
        RoiShapeKind shape,
        int expectedPointCount)
    {
        var roi = shape switch
        {
            RoiShapeKind.Circle => new RoiDefinition { Shape = shape, X = 20, Y = 10, Radius = 8 },
            RoiShapeKind.RotatedRectangle => new RoiDefinition
            {
                Shape = shape,
                X = 20,
                Y = 10,
                Width = 30,
                Height = 12,
                Angle = 17
            },
            RoiShapeKind.Polygon => new RoiDefinition
            {
                Shape = shape,
                Points = [new Point2D(1, 1), new Point2D(35, 2), new Point2D(38, 17), new Point2D(2, 19)]
            },
            _ => new RoiDefinition { Shape = shape, X = 1, Y = 1, Width = 38, Height = 18 }
        };
        var parameters = new Dictionary<string, string>
        {
            ["templateX"] = "0",
            ["templateY"] = "0",
            ["templateWidth"] = "40",
            ["templateHeight"] = "20",
            ["templateRoiJson"] = JsonSerializer.Serialize(roi)
        };

        var success = LegacyTemplateMatchingAdapterSupport.TryCreateTemplateRoiContours(
            parameters,
            new Pose2D(50, 60, -12) { Scale = 0.9 },
            out var contours,
            out _,
            out _,
            out var technicalDetails);

        Assert.True(success, technicalDetails);
        var contour = Assert.Single(contours);
        Assert.Equal(expectedPointCount, contour.Count);
        Assert.All(contour, point => Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)));
    }

    [Theory]
    [InlineData(InspectionOutcome.Ng, 2, 2, InspectionOutcome.Ng)]
    [InlineData(InspectionOutcome.Ok, 2, 2, InspectionOutcome.Ok)]
    [InlineData(InspectionOutcome.Ok, 1, 2, InspectionOutcome.Ng)]
    [InlineData(InspectionOutcome.Ok, 3, 2, InspectionOutcome.Ng)]
    public void OpenCvExactCountCanOnlyDowngradeLegacyOutcome(
        InspectionOutcome sourceOutcome,
        int actualCount,
        int expectedCount,
        InspectionOutcome expectedOutcome)
    {
        var outcome = OpenCvTemplateMatchingBackend.ResolveExactCountOutcome(
            sourceOutcome,
            actualCount,
            expectedCount);

        Assert.Equal(expectedOutcome, outcome);
    }

    [Fact]
    public void LegacyContourTransformRejectsExplicitInvalidRoiAngle()
    {
        var parameters = new Dictionary<string, string>
        {
            ["templateX"] = "0",
            ["templateY"] = "0",
            ["templateWidth"] = "40",
            ["templateHeight"] = "20",
            ["templateRoiX"] = "0",
            ["templateRoiY"] = "0",
            ["templateRoiWidth"] = "40",
            ["templateRoiHeight"] = "20",
            ["templateRoiShape"] = "RotatedRectangle",
            ["templateRoiAngle"] = "not-a-number"
        };

        var success = LegacyTemplateMatchingAdapterSupport.TryCreateTemplateRoiContours(
            parameters,
            new Pose2D(20, 10, 0),
            out _,
            out _,
            out _,
            out _);

        Assert.False(success);
    }

    [Fact]
    public async Task OpenCvMultiAdapterReturnsReferenceSizedCompleteContours()
    {
        await using var service = TemplateMatchingService.CreateLegacyOnly();
        var frame = PatternFrame();
        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["matchMode"] = "GrayNcc",
            ["multiMatchMode"] = "GrayNcc",
            ["minScore"] = "0.5",
            ["matchCount"] = "1",
            ["minCount"] = "1",
            ["angleStart"] = "0",
            ["angleExtent"] = "0"
        };
        var learned = await service.LearnAsync(
            new TemplateLearningRequest(Owner(), frame, TemplateRoi(), null, source),
            default);
        var runtime = new Dictionary<string, string>(learned.Parameters, StringComparer.OrdinalIgnoreCase);

        var result = await service.MatchAsync(new TemplateMatchingRequest(
            Owner(), frame, null, runtime, TemplateMatchCardinality.ExactCount, 1), default);

        Assert.True(
            result.Matches.Count > 0,
            $"{result.Message} {result.Diagnostic?.Code} {result.Diagnostic?.TechnicalDetails}");
        var candidate = Assert.Single(result.Matches);
        Assert.Equal((16, 16), (candidate.TemplateWidth, candidate.TemplateHeight));
        Assert.Equal(4, Assert.Single(candidate.TemplateRoiContours).Count);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task OpenCvExactCountUsesSentinelWithoutReducingConfiguredMatchCount(
        int configuredMatchCount)
    {
        await using var service = TemplateMatchingService.CreateLegacyOnly();
        var frame = ThreePatternFrame();
        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["engine"] = "OpenCv",
            ["matchMode"] = "GrayNcc",
            ["multiMatchMode"] = "GrayNcc",
            ["minScore"] = "0.999",
            ["matchCount"] = configuredMatchCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["minCount"] = "1",
            ["minDistance"] = "24",
            ["angleStart"] = "0",
            ["angleExtent"] = "0"
        };
        var learned = await service.LearnAsync(
            new TemplateLearningRequest(Owner(), frame, TemplateRoi(), null, source),
            default);
        var runtime = new Dictionary<string, string>(learned.Parameters, StringComparer.OrdinalIgnoreCase);
        var request = new TemplateMatchingRequest(
            Owner(),
            frame,
            null,
            runtime,
            TemplateMatchCardinality.ExactCount,
            2);

        var result = await service.MatchAsync(request, default);

        Assert.Equal(InspectionOutcome.Ng, result.Outcome);
        Assert.Equal(3, result.Matches.Count);
        Assert.Equal(configuredMatchCount.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Parameters["matchCount"]);
        Assert.Equal(configuredMatchCount.ToString(System.Globalization.CultureInfo.InvariantCulture), runtime["matchCount"]);
    }

    [Fact]
    public void ManagedRectangleContourAppliesPoseAngleAndScaleBeforeProjection()
    {
        var contours = LegacyTemplateMatchingAdapterSupport.CreateRectangleContours(
            new Pose2D(100, 100, 35) { Scale = 1.1 },
            40,
            20);

        var contour = Assert.Single(contours);
        Assert.Equal(88.288, contour[0].X, 3);
        Assert.Equal(78.371, contour[0].Y, 3);
    }

    public static IEnumerable<object[]> InvalidExplicitTemplateRois()
    {
        yield return
        [
            "rectangle-non-finite-x",
            new RoiDefinition
            {
                Shape = RoiShapeKind.Rectangle,
                X = double.NaN,
                Y = 8,
                Width = 16,
                Height = 16
            }
        ];
        yield return
        [
            "rectangle-non-finite-y",
            new RoiDefinition
            {
                Shape = RoiShapeKind.Rectangle,
                X = 8,
                Y = double.PositiveInfinity,
                Width = 16,
                Height = 16
            }
        ];
        yield return
        [
            "circle-non-finite-x",
            new RoiDefinition { Shape = RoiShapeKind.Circle, X = double.NaN, Y = 16, Radius = 8 }
        ];
        yield return
        [
            "circle-non-finite-y",
            new RoiDefinition { Shape = RoiShapeKind.Circle, X = 16, Y = double.NegativeInfinity, Radius = 8 }
        ];
        yield return
        [
            "circle-zero-radius",
            new RoiDefinition { Shape = RoiShapeKind.Circle, X = 16, Y = 16, Radius = 0 }
        ];
        yield return
        [
            "circle-negative-radius",
            new RoiDefinition { Shape = RoiShapeKind.Circle, X = 16, Y = 16, Radius = -8 }
        ];
        yield return
        [
            "rotated-rectangle-non-finite-x",
            new RoiDefinition
            {
                Shape = RoiShapeKind.RotatedRectangle,
                X = double.NaN,
                Y = 16,
                Width = 14,
                Height = 10,
                Angle = 17
            }
        ];
        yield return
        [
            "rotated-rectangle-non-finite-y",
            new RoiDefinition
            {
                Shape = RoiShapeKind.RotatedRectangle,
                X = 16,
                Y = double.PositiveInfinity,
                Width = 14,
                Height = 10,
                Angle = 17
            }
        ];
        yield return
        [
            "rotated-rectangle-non-finite-angle",
            new RoiDefinition
            {
                Shape = RoiShapeKind.RotatedRectangle,
                X = 16,
                Y = 16,
                Width = 14,
                Height = 10,
                Angle = double.NaN
            }
        ];
        yield return
        [
            "rotated-rectangle-zero-width",
            new RoiDefinition
            {
                Shape = RoiShapeKind.RotatedRectangle,
                X = 16,
                Y = 16,
                Width = 0,
                Height = 10,
                Angle = 17
            }
        ];
        yield return
        [
            "rotated-rectangle-negative-width",
            new RoiDefinition
            {
                Shape = RoiShapeKind.RotatedRectangle,
                X = 16,
                Y = 16,
                Width = -14,
                Height = 10,
                Angle = 17
            }
        ];
        yield return
        [
            "rotated-rectangle-zero-height",
            new RoiDefinition
            {
                Shape = RoiShapeKind.RotatedRectangle,
                X = 16,
                Y = 16,
                Width = 14,
                Height = 0,
                Angle = 17
            }
        ];
        yield return
        [
            "rotated-rectangle-negative-height",
            new RoiDefinition
            {
                Shape = RoiShapeKind.RotatedRectangle,
                X = 16,
                Y = 16,
                Width = 14,
                Height = -10,
                Angle = 17
            }
        ];
        yield return
        [
            "polygon-fewer-than-three-points",
            new RoiDefinition
            {
                Shape = RoiShapeKind.Polygon,
                Points = [new Point2D(8, 8), new Point2D(24, 24)]
            }
        ];
        yield return
        [
            "polygon-degenerate",
            new RoiDefinition
            {
                Shape = RoiShapeKind.Polygon,
                Points = [new Point2D(8, 8), new Point2D(16, 16), new Point2D(24, 24)]
            }
        ];
        yield return
        [
            "polygon-non-finite-point",
            new RoiDefinition
            {
                Shape = RoiShapeKind.Polygon,
                Points =
                [
                    new Point2D(8, 8),
                    new Point2D(double.NaN, 8),
                    new Point2D(24, 24),
                    new Point2D(8, 24)
                ]
            }
        ];
    }

    public static IEnumerable<object[]> ValidExplicitTemplateRois()
    {
        yield return
        [
            "rectangle",
            new RoiDefinition
            {
                Id = "valid-rectangle",
                Shape = RoiShapeKind.Rectangle,
                X = 8,
                Y = 8,
                Width = 16,
                Height = 16
            }
        ];
        yield return
        [
            "circle",
            new RoiDefinition
            {
                Id = "valid-circle",
                Shape = RoiShapeKind.Circle,
                X = 16,
                Y = 16,
                Radius = 8
            }
        ];
        yield return
        [
            "rotated-rectangle",
            new RoiDefinition
            {
                Id = "valid-rotated-rectangle",
                Shape = RoiShapeKind.RotatedRectangle,
                X = 16,
                Y = 16,
                Width = 14,
                Height = 10,
                Angle = 17
            }
        ];
        yield return
        [
            "polygon",
            new RoiDefinition
            {
                Id = "valid-polygon",
                Shape = RoiShapeKind.Polygon,
                Points =
                [
                    new Point2D(8, 8),
                    new Point2D(24, 8),
                    new Point2D(24, 24),
                    new Point2D(8, 24)
                ]
            }
        ];
    }

    public static IEnumerable<object[]> ValidExplicitTemplateRoisWithNonFiniteInactiveFields()
    {
        var inactivePoints = new[]
        {
            new Point2D(double.NaN, double.PositiveInfinity)
        };
        yield return
        [
            "rectangle",
            "OpenCv",
            new RoiDefinition
            {
                Id = "normalized-rectangle",
                Name = "Rectangle ROI",
                Shape = RoiShapeKind.Rectangle,
                X = 8,
                Y = 8,
                Width = 16,
                Height = 16,
                Angle = double.NaN,
                Radius = double.PositiveInfinity,
                Points = inactivePoints
            }
        ];
        yield return
        [
            "circle",
            "ManagedNcc",
            new RoiDefinition
            {
                Id = "normalized-circle",
                Name = "Circle ROI",
                Shape = RoiShapeKind.Circle,
                X = 16,
                Y = 16,
                Width = double.NaN,
                Height = double.PositiveInfinity,
                Angle = double.NegativeInfinity,
                Radius = 8,
                Points = inactivePoints
            }
        ];
        yield return
        [
            "rotated-rectangle",
            "OpenCv",
            new RoiDefinition
            {
                Id = "normalized-rotated-rectangle",
                Name = "Rotated ROI",
                Shape = RoiShapeKind.RotatedRectangle,
                X = 16,
                Y = 16,
                Width = 14,
                Height = 10,
                Angle = 17,
                Radius = double.NaN,
                Points = inactivePoints
            }
        ];
        yield return
        [
            "polygon",
            "ManagedNcc",
            new RoiDefinition
            {
                Id = "normalized-polygon",
                Name = "Polygon ROI",
                Shape = RoiShapeKind.Polygon,
                X = double.NaN,
                Y = double.PositiveInfinity,
                Width = double.NegativeInfinity,
                Height = double.NaN,
                Angle = double.PositiveInfinity,
                Radius = double.NegativeInfinity,
                Points =
                [
                    new Point2D(8, 8),
                    new Point2D(24, 8),
                    new Point2D(24, 24),
                    new Point2D(8, 24)
                ]
            }
        ];
    }

    private static TemplateMatchingRequest Request(
        string? engine,
        TemplateMatchCardinality cardinality = TemplateMatchCardinality.Single,
        int expectedCount = 1)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (engine is not null)
        {
            parameters["engine"] = engine;
        }

        return new TemplateMatchingRequest(
            Owner(),
            Frame(),
            null,
            parameters,
            cardinality,
            expectedCount);
    }

    private static void AssertInvalidLearningResult(TemplateLearningResult result, string scenario)
    {
        Assert.False(result.Success, scenario);
        Assert.Equal(TemplateMatchingEngine.OpenCv, result.Engine);
        Assert.Equal(TemplateMatchingDiagnosticCodes.ConfigInvalidParameter, result.Diagnostic?.Code);
        Assert.Empty(result.Parameters);
    }

    private static void AssertNormalizedInactiveFields(
        RoiDefinition requestSnapshot,
        RoiDefinition normalized)
    {
        switch (requestSnapshot.Shape)
        {
            case RoiShapeKind.Rectangle:
                Assert.True(double.IsNaN(requestSnapshot.Angle));
                Assert.True(double.IsPositiveInfinity(requestSnapshot.Radius));
                Assert.NotEmpty(requestSnapshot.Points);
                Assert.Equal((requestSnapshot.X, requestSnapshot.Y), (normalized.X, normalized.Y));
                Assert.Equal((requestSnapshot.Width, requestSnapshot.Height), (normalized.Width, normalized.Height));
                Assert.Equal((0d, 0d), (normalized.Angle, normalized.Radius));
                Assert.Empty(normalized.Points);
                break;

            case RoiShapeKind.Circle:
                Assert.True(double.IsNaN(requestSnapshot.Width));
                Assert.True(double.IsPositiveInfinity(requestSnapshot.Height));
                Assert.True(double.IsNegativeInfinity(requestSnapshot.Angle));
                Assert.NotEmpty(requestSnapshot.Points);
                Assert.Equal((requestSnapshot.X, requestSnapshot.Y, requestSnapshot.Radius),
                    (normalized.X, normalized.Y, normalized.Radius));
                Assert.Equal((0d, 0d, 0d), (normalized.Width, normalized.Height, normalized.Angle));
                Assert.Empty(normalized.Points);
                break;

            case RoiShapeKind.RotatedRectangle:
                Assert.True(double.IsNaN(requestSnapshot.Radius));
                Assert.NotEmpty(requestSnapshot.Points);
                Assert.Equal(
                    (requestSnapshot.X, requestSnapshot.Y, requestSnapshot.Width, requestSnapshot.Height, requestSnapshot.Angle),
                    (normalized.X, normalized.Y, normalized.Width, normalized.Height, normalized.Angle));
                Assert.Equal(0d, normalized.Radius);
                Assert.Empty(normalized.Points);
                break;

            case RoiShapeKind.Polygon:
                Assert.True(double.IsNaN(requestSnapshot.X));
                Assert.True(double.IsPositiveInfinity(requestSnapshot.Y));
                Assert.True(double.IsNegativeInfinity(requestSnapshot.Width));
                Assert.True(double.IsNaN(requestSnapshot.Height));
                Assert.True(double.IsPositiveInfinity(requestSnapshot.Angle));
                Assert.True(double.IsNegativeInfinity(requestSnapshot.Radius));
                Assert.Equal((0d, 0d, 0d, 0d, 0d, 0d),
                    (normalized.X, normalized.Y, normalized.Width, normalized.Height, normalized.Angle, normalized.Radius));
                Assert.Equal(requestSnapshot.Points.ToArray(), normalized.Points.ToArray());
                break;

            default:
                throw new InvalidOperationException($"Unexpected ROI shape '{requestSnapshot.Shape}'.");
        }

        Assert.True(double.IsFinite(normalized.X));
        Assert.True(double.IsFinite(normalized.Y));
        Assert.True(double.IsFinite(normalized.Width));
        Assert.True(double.IsFinite(normalized.Height));
        Assert.True(double.IsFinite(normalized.Angle));
        Assert.True(double.IsFinite(normalized.Radius));
    }

    private static void AssertNormalizedLegacyRoiKeys(
        RoiDefinition normalized,
        IReadOnlyDictionary<string, double> legacyValues)
    {
        var expectedBounds = normalized.Shape switch
        {
            RoiShapeKind.Rectangle => (normalized.X, normalized.Y, normalized.Width, normalized.Height),
            RoiShapeKind.Circle => (
                normalized.X - normalized.Radius,
                normalized.Y - normalized.Radius,
                normalized.Radius * 2,
                normalized.Radius * 2),
            RoiShapeKind.RotatedRectangle => GetRotatedBounds(normalized),
            RoiShapeKind.Polygon => (
                normalized.Points.Min(point => point.X),
                normalized.Points.Min(point => point.Y),
                normalized.Points.Max(point => point.X) - normalized.Points.Min(point => point.X),
                normalized.Points.Max(point => point.Y) - normalized.Points.Min(point => point.Y)),
            _ => throw new InvalidOperationException($"Unexpected ROI shape '{normalized.Shape}'.")
        };

        Assert.Equal(expectedBounds.Item1, legacyValues["templateRoiX"], 12);
        Assert.Equal(expectedBounds.Item2, legacyValues["templateRoiY"], 12);
        Assert.Equal(expectedBounds.Item3, legacyValues["templateRoiWidth"], 12);
        Assert.Equal(expectedBounds.Item4, legacyValues["templateRoiHeight"], 12);
        Assert.Equal(normalized.Angle, legacyValues["templateRoiAngle"], 12);

        static (double X, double Y, double Width, double Height) GetRotatedBounds(RoiDefinition roi)
        {
            var radians = roi.Angle * Math.PI / 180d;
            var width = Math.Abs(roi.Width * Math.Cos(radians)) +
                        Math.Abs(roi.Height * Math.Sin(radians));
            var height = Math.Abs(roi.Width * Math.Sin(radians)) +
                         Math.Abs(roi.Height * Math.Cos(radians));
            return (roi.X - width / 2d, roi.Y - height / 2d, width, height);
        }
    }

    private static TemplateLearningRequest LearningRequest(string engine)
    {
        return new TemplateLearningRequest(
            Owner(),
            Frame(),
            TemplateRoi(),
            null,
            new Dictionary<string, string> { ["engine"] = engine });
    }

    private static TemplateModelOwner Owner() => new("recipe", "flow", "tool");

    private static RoiDefinition TemplateRoi() => new()
    {
        Id = "template-roi",
        Shape = RoiShapeKind.Rectangle,
        X = 8,
        Y = 8,
        Width = 16,
        Height = 16
    };

    private static ImageFrame Frame() => new(
        "service-test",
        32,
        32,
        32,
        PixelFormatKind.Gray8,
        new byte[32 * 32],
        DateTimeOffset.UnixEpoch,
        "test");

    private static ImageFrame InvalidLearningSentinelFrame() => new(
        "must-not-reach-legacy-learn",
        0,
        0,
        0,
        PixelFormatKind.Gray8,
        Array.Empty<byte>(),
        DateTimeOffset.UnixEpoch,
        "test");

    private static ImageFrame PatternFrame()
    {
        const int width = 40;
        const int height = 40;
        var pixels = Enumerable.Repeat((byte)230, width * height).ToArray();
        for (var y = 8; y < 24; y++)
        {
            for (var x = 8; x < 24; x++)
            {
                pixels[y * width + x] = (byte)(((x + y) % 5 == 0 || x == 9 || y == 21) ? 20 : 170);
            }
        }

        return new ImageFrame(
            "pattern",
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UnixEpoch,
            "test");
    }

    private static ImageFrame ThreePatternFrame()
    {
        const int width = 112;
        const int height = 40;
        var pixels = Enumerable.Repeat((byte)230, width * height).ToArray();
        foreach (var offsetX in new[] { 8, 48, 88 })
        {
            for (var y = 8; y < 24; y++)
            {
                for (var x = offsetX; x < offsetX + 16; x++)
                {
                    var localX = x - offsetX + 8;
                    pixels[y * width + x] = (byte)(((localX + y) % 5 == 0 || localX == 9 || y == 21) ? 20 : 170);
                }
            }
        }

        return new ImageFrame(
            "three-patterns",
            width,
            height,
            width,
            PixelFormatKind.Gray8,
            pixels,
            DateTimeOffset.UnixEpoch,
            "test");
    }
}
