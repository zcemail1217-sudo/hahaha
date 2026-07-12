using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VisionStation.Application.Inspection;
using VisionStation.Application.Inspection.Steps;
using VisionStation.Communication;
using VisionStation.Devices;
using VisionStation.Domain;
using VisionStation.Domain.Utilities;
using VisionStation.Vision;

namespace VisionStation.Application;

public interface IInspectionRunner
{
    event EventHandler<InspectionRunResult>? RunCompleted;

    Task<InspectionRunResult> RunAsync(InspectionRequest request, CancellationToken cancellationToken = default);
}

public sealed record FlowRunResult(
    string FlowId,
    string FlowName,
    ImageFrame ResultFrame,
    IReadOnlyList<ToolResult> ToolResults,
    InspectionOutcome Outcome,
    string Barcode,
    string Message);

public sealed record InspectionRunResult(InspectionResult Result, ImageFrame OriginalFrame, ImageFrame ResultFrame, Recipe Recipe)
{
    public IReadOnlyList<FlowRunResult> FlowResults { get; init; } = Array.Empty<FlowRunResult>();

    public IReadOnlyDictionary<string, string> RuntimeValues { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class InspectionRunner : IInspectionRunner
{
    private readonly ICameraDevice _camera;
    private readonly IConfigurableCameraDevice _configurableCamera;
    private readonly IAxisController _axis;
    private readonly IPlcClient _plc;
    private readonly IDeviceRuntime _devices;
    private readonly IDeviceConfigurationRepository _configurationRepository;
    private DeviceConfiguration _configuration;
    private readonly IVisionPipeline _pipeline;
    private readonly IRecipeRepository _recipes;
    private readonly IInspectionRecordRepository _records;
    private readonly IImageTraceStore _traceStore;
    private readonly IAppLogService _log;
    private readonly ICommunicationChannelRuntime _communicationChannels;
    private readonly IInspectionRunControl _runControl;
    private readonly DelayStepHandler _delayStepHandler = new();
    private readonly StringProcessStepHandler _stringProcessStepHandler = new();
    private readonly ResultJudgeStepHandler _resultJudgeStepHandler = new();
    private readonly ReadVisionResultStepHandler _readVisionResultStepHandler = new();
    private readonly WriteResultTableStepHandler _writeResultTableStepHandler = new();
    private readonly WritePlcStepHandler _writePlcStepHandler = new();
    private readonly DeviceReadStepHandler _deviceReadStepHandler = new();
    private readonly DeviceWriteStepHandler _deviceWriteStepHandler = new();
    private readonly DeviceCommandStepHandler _deviceCommandStepHandler = new();

    public InspectionRunner(
        ICameraDevice camera,
        IConfigurableCameraDevice configurableCamera,
        IAxisController axis,
        IPlcClient plc,
        IDeviceRuntime devices,
        DeviceConfiguration configuration,
        IDeviceConfigurationRepository configurationRepository,
        IVisionPipeline pipeline,
        IRecipeRepository recipes,
        IInspectionRecordRepository records,
        IImageTraceStore traceStore,
        IAppLogService log,
        ICommunicationChannelRuntime communicationChannels,
        IInspectionRunControl runControl)
    {
        _camera = camera;
        _configurableCamera = configurableCamera;
        _axis = axis;
        _plc = plc;
        _devices = devices;
        _configuration = configuration;
        _configurationRepository = configurationRepository;
        _pipeline = pipeline;
        _recipes = recipes;
        _records = records;
        _traceStore = traceStore;
        _log = log;
        _communicationChannels = communicationChannels;
        _runControl = runControl;
    }

    public event EventHandler<InspectionRunResult>? RunCompleted;

    public async Task<InspectionRunResult> RunAsync(InspectionRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var recipe = await ResolveRecipeAsync(request, cancellationToken);
        _configuration = await _configurationRepository.GetAsync(cancellationToken);
        var initialRuntimeValues = CreateInitialRuntimeValues(recipe, request, _configuration);
        var runtimeRecipe = ApplyVariableBindings(recipe, initialRuntimeValues).WithNormalizedFlows();

        var hasRuntimeProcess = runtimeRecipe.ProcessSteps.Any(step => step.Enabled);
        var execution = hasRuntimeProcess
            ? await ExecuteProcessFlowAsync(runtimeRecipe, initialRuntimeValues, cancellationToken)
            : await ExecuteVisionOnlyAsync(runtimeRecipe, initialRuntimeValues, cancellationToken);
        if (!request.ProcessOnly || HasEnabledStep(runtimeRecipe, ProcessStepType.AxisMove))
        {
            await CaptureAxisRuntimeValuesAsync(runtimeRecipe, execution, cancellationToken);
        }

        EvaluateExpressionVariables(runtimeRecipe, execution);

        var originalFrame = execution.OriginalFrame ?? CreateEmptyFrame("ProcessOnly");
        if (!request.ProcessOnly && execution.OriginalFrame is null)
        {
            originalFrame = await CaptureFrameAsync(runtimeRecipe, cancellationToken);
        }

        var resultFrame = execution.ResultFrame ?? originalFrame;
        stopwatch.Stop();

        var result = new InspectionResult
        {
            RecipeId = runtimeRecipe.Id,
            RecipeName = $"{runtimeRecipe.Name} / {execution.FlowName}",
            BatchId = request.BatchId,
            Outcome = ResolveOutcome(execution),
            CycleTime = stopwatch.Elapsed,
            Barcode = ResolveBarcode(execution),
            Message = BuildCompletionMessage(execution),
            ToolResults = execution.ToolResults.ToArray(),
            ResultData = new Dictionary<string, string>(execution.ResultTable, StringComparer.OrdinalIgnoreCase)
        };

        if (!request.ProcessOnly)
        {
            var tracePaths = await _traceStore.SaveAsync(runtimeRecipe, originalFrame, resultFrame, result, cancellationToken);
            result = result with
            {
                OriginalImagePath = tracePaths.OriginalImagePath,
                ResultImagePath = tracePaths.ResultImagePath
            };
        }

        if (!request.ProcessOnly)
        {
            await _records.AddAsync(result, cancellationToken);
        }

        _log.Info("Inspection", $"{runtimeRecipe.Name}/{execution.FlowName} {result.Outcome} {result.CycleTime.TotalMilliseconds:0}ms {result.Barcode}");

        var runResult = new InspectionRunResult(result, originalFrame, resultFrame, runtimeRecipe)
        {
            FlowResults = execution.FlowResults.ToArray(),
            RuntimeValues = new Dictionary<string, string>(execution.RuntimeValues, StringComparer.OrdinalIgnoreCase)
        };
        RunCompleted?.Invoke(this, runResult);
        return runResult;
    }

    private async Task<Recipe> ResolveRecipeAsync(
        InspectionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.RecipeSnapshot is { } snapshot)
        {
            if (string.IsNullOrWhiteSpace(snapshot.Id))
            {
                throw new ArgumentException(
                    "RecipeSnapshot.Id is required.",
                    nameof(request));
            }

            if (!string.IsNullOrWhiteSpace(request.RecipeId) &&
                !string.Equals(
                    request.RecipeId,
                    snapshot.Id,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"RecipeId '{request.RecipeId}' does not match RecipeSnapshot.Id '{snapshot.Id}'.",
                    nameof(request));
            }

            return snapshot.WithNormalizedFlows();
        }

        Recipe? recipe = null;
        if (!string.IsNullOrWhiteSpace(request.RecipeId))
        {
            recipe = await _recipes.GetAsync(request.RecipeId, cancellationToken);
        }

        return (recipe ?? await _recipes.GetCurrentAsync(cancellationToken))
            .WithNormalizedFlows();
    }

    private async Task<ProcessExecutionContext> ExecuteVisionOnlyAsync(
        Recipe recipe,
        IReadOnlyDictionary<string, string> initialRuntimeValues,
        CancellationToken cancellationToken)
    {
        var context = new ProcessExecutionContext(recipe.GetActiveFlow().Name, initialRuntimeValues);
        var runtimeRecipe = ApplyVariableBindings(recipe, context.RuntimeValues);
        var frame = await CaptureFrameAsync(runtimeRecipe, cancellationToken);
        var pipelineResult = await _pipeline.ExecuteAsync(runtimeRecipe, frame, cancellationToken);

        context.OriginalFrame = frame;
        context.ResultFrame = pipelineResult.ResultFrame;
        context.LastPipelineResult = pipelineResult;
        context.ToolResults.AddRange(pipelineResult.ToolResults);
        context.FlowResults.Add(ToFlowRunResult(runtimeRecipe, pipelineResult));
        VisionRuntimeValueSeeder.SeedPipelineOutputs(runtimeRecipe, context, pipelineResult);
        EvaluateExpressionVariables(runtimeRecipe, context);
        return context;
    }

    private async Task<ProcessExecutionContext> ExecuteProcessFlowAsync(
        Recipe recipe,
        IReadOnlyDictionary<string, string> initialRuntimeValues,
        CancellationToken cancellationToken)
    {
        var context = new ProcessExecutionContext(recipe.GetActiveFlow().Name, initialRuntimeValues);
        var steps = recipe.ProcessSteps
            .Where(step => step.Enabled)
            .OrderBy(step => step.StepNo)
            .ToArray();

        foreach (var rawStep in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _runControl.WaitIfPausedOrResetAsync(cancellationToken);
            var step = ResolveProcessStepVariables(rawStep, context.RuntimeValues);
            var stepStopwatch = Stopwatch.StartNew();
            _log.Info("ProcessFlow", $"开始步骤 {step.StepNo}: {step.Name}（{DescribeProcessStepType(step.StepType)}）");

            try
            {
                switch (step.StepType)
                {
                    case ProcessStepType.AxisMove:
                        await MoveAxisAsync(step, cancellationToken);
                        await CaptureAxisRuntimeValuesAsync(recipe, context, cancellationToken);
                        LogStepResult(step, "轴运动完成");
                        break;
                    case ProcessStepType.WaitPlcSignal:
                        await WaitSignalAsync(recipe, step, context, cancellationToken);
                        break;
                    case ProcessStepType.AcquireImage:
                    {
                        var captureRecipe = ApplyVariableBindings(recipe, context.RuntimeValues);
                        var frame = await CaptureFrameAsync(captureRecipe, cancellationToken);
                        context.OriginalFrame ??= frame;
                        context.ResultFrame = frame;
                        LogStepResult(step, "采图完成");
                        break;
                    }
                    case ProcessStepType.RunVisionFlow:
                    {
                        var flowRecipe = ApplyVariableBindings(ResolveFlowRecipe(recipe, step.FlowId), context.RuntimeValues);
                        var frame = context.ResultFrame ?? context.OriginalFrame;
                        if (frame is null)
                        {
                            frame = VisionFlowFramePolicy.RequiresExternalFrame(flowRecipe)
                                ? await CaptureFrameAsync(flowRecipe, cancellationToken)
                                : CreateEmptyFrame($"VisionFlow:{flowRecipe.GetActiveFlow().Id}:InternalAcquire");
                        }

                        context.OriginalFrame ??= frame;
                        var pipelineResult = await _pipeline.ExecuteAsync(flowRecipe, frame, cancellationToken);
                        context.FlowName = flowRecipe.GetActiveFlow().Name;
                        context.ResultFrame = pipelineResult.ResultFrame;
                        context.LastPipelineResult = pipelineResult;
                        context.ToolResults.AddRange(pipelineResult.ToolResults);
                        context.FlowResults.Add(ToFlowRunResult(flowRecipe, pipelineResult));
                        VisionRuntimeValueSeeder.SeedPipelineOutputs(flowRecipe, context, pipelineResult, step.OutputTarget);
                        VisionRuntimeValueSeeder.SeedResultToolBindings(flowRecipe, context, pipelineResult, step.Parameters);
                        EvaluateExpressionVariables(flowRecipe, context);
                        LogStepResult(step, $"视觉流程 {flowRecipe.GetActiveFlow().Name}，结果 {pipelineResult.Outcome}，工具 {pipelineResult.ToolResults.Count}");
                        break;
                    }
                    case ProcessStepType.ReadVisionResult:
                    {
                        if (!TryResolveProcessValue(recipe, context, step.ResultKey, out var value))
                        {
                            throw new InvalidOperationException($"Unable to resolve vision result '{step.ResultKey}' in step '{step.Name}'.");
                        }

                        _readVisionResultStepHandler.Execute(step, value, context);
                        LogStepResult(step, $"{step.ResultKey} = {TruncateForLog(value)}");
                        break;
                    }
                    case ProcessStepType.WriteResultTable:
                    {
                        if (!TryResolveProcessValue(recipe, context, step.ResultKey, out var value))
                        {
                            throw new InvalidOperationException($"Unable to resolve runtime value '{step.ResultKey}' in step '{step.Name}'.");
                        }

                        var target = _writeResultTableStepHandler.Execute(step, value, context);
                        LogStepResult(step, $"{target} = {TruncateForLog(value)}");
                        break;
                    }
                    case ProcessStepType.WritePlc:
                    {
                        if (!TryResolveProcessValue(recipe, context, step.ResultKey, out var value))
                        {
                            throw new InvalidOperationException($"Unable to resolve PLC output value '{step.ResultKey}' in step '{step.Name}'.");
                        }

                        var target = string.IsNullOrWhiteSpace(step.OutputTarget)
                                ? VisionResultResolver.ResolveVisionResultAddress(recipe, step.ResultKey)
                            : step.OutputTarget;
                        if (string.IsNullOrWhiteSpace(target))
                        {
                            throw new InvalidOperationException($"PLC output target is empty for step '{step.Name}'.");
                        }

                        var device = ResolveAddressableDevice(step.DeviceKey);
                        var result = await _writePlcStepHandler.ExecuteAsync(step, device, target, value, context, cancellationToken);
                        LogStepResult(step, $"写入 {result.DeviceKey}:{result.Address} = {TruncateForLog(result.Value)}");
                        break;
                    }
                    case ProcessStepType.DeviceRead:
                    {
                        await ReadDeviceAsync(step, context, cancellationToken);
                        break;
                    }
                    case ProcessStepType.DeviceWrite:
                    {
                        await WriteDeviceAsync(recipe, context, step, cancellationToken);
                        break;
                    }
                    case ProcessStepType.DeviceCommand:
                    {
                        await InvokeDeviceCommandAsync(step, context, cancellationToken);
                        break;
                    }
                    case ProcessStepType.StringProcess:
                    {
                        await ProcessStringValueAsync(recipe, step, context, cancellationToken);
                        break;
                    }
                    case ProcessStepType.ResultJudge:
                    {
                        if (!TryResolveProcessValue(recipe, context, step.ResultKey, out var value))
                        {
                            throw new InvalidOperationException($"Unable to resolve judge value '{step.ResultKey}' in step '{step.Name}'.");
                        }

                        var judgeResult = _resultJudgeStepHandler.Execute(step, value, context);
                        LogStepResult(step, $"{step.ResultKey} = {TruncateForLog(value)}，判定 {judgeResult}");
                        break;
                    }
                    case ProcessStepType.Delay:
                    {
                        var delay = await _delayStepHandler.ExecuteAsync(step, cancellationToken);
                        LogStepResult(step, $"延时 {delay} ms");
                        break;
                    }
                    case ProcessStepType.End:
                        stepStopwatch.Stop();
                        LogStepResult(step, "流程结束");
                        _log.Info("ProcessFlow", $"完成步骤 {step.StepNo}: {step.Name}，流程结束，耗时 {stepStopwatch.ElapsedMilliseconds} ms");
                        return context;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                stepStopwatch.Stop();
                _log.Error("ProcessFlow", $"步骤失败 {step.StepNo}: {step.Name}，耗时 {stepStopwatch.ElapsedMilliseconds} ms，错误 {ex.Message}");
                throw;
            }

            stepStopwatch.Stop();
            _log.Info("ProcessFlow", $"完成步骤 {step.StepNo}: {step.Name}，耗时 {stepStopwatch.ElapsedMilliseconds} ms");
        }

        return context;
    }

    private async Task<ImageFrame> CaptureFrameAsync(Recipe recipe, CancellationToken cancellationToken)
    {
        var settings = ResolveCameraSettings(recipe);
        if (settings is not null)
        {
            await _configurableCamera.ApplyAcquisitionSettingsAsync(settings, cancellationToken);
        }

        await EnsureCameraConnectedAsync(cancellationToken);
        return await _camera.GrabAsync(cancellationToken);
    }

    private static bool HasEnabledStep(Recipe recipe, ProcessStepType stepType)
    {
        return recipe.ProcessSteps.Any(step => step.Enabled && step.StepType == stepType);
    }

    private static ImageFrame CreateEmptyFrame(string source)
    {
        return new ImageFrame(
            Guid.NewGuid().ToString("N"),
            1,
            1,
            1,
            PixelFormatKind.Gray8,
            [0],
            DateTimeOffset.Now,
            source);
    }

    private static CameraAcquisitionSettings? ResolveCameraSettings(Recipe recipe)
    {
        var acquireTool = recipe.GetActiveFlow().Tools.FirstOrDefault(tool => tool.Enabled && tool.Kind == VisionToolKind.AcquireImage);
        if (acquireTool is null)
        {
            return new CameraAcquisitionSettings
            {
                DeviceId = recipe.Camera.CameraId,
                ExposureTimeMs = recipe.Camera.ExposureTimeUs / 1000.0,
                TriggerSource = recipe.Camera.HardwareTrigger ? "Line0" : "软件触发",
                HeartbeatTimeoutMs = 3000,
                ClearBufferBeforeTrigger = true
            };
        }

        var parameters = acquireTool.Parameters;
        var source = parameters.GetValueOrDefault("source") ?? "Camera";
        if (!string.Equals(source, "Camera", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new CameraAcquisitionSettings
        {
            DeviceId = parameters.GetValueOrDefault("device") ?? parameters.GetValueOrDefault("cameraSerial") ?? recipe.Camera.CameraId,
            ExposureTimeMs = GetExposureTimeMs(parameters, recipe.Camera.ExposureTimeUs / 1000.0),
            TriggerSource = parameters.GetValueOrDefault("triggerSource") ?? (recipe.Camera.HardwareTrigger ? "Line0" : "软件触发"),
            HeartbeatTimeoutMs = (int)Math.Clamp(GetDouble(parameters, "heartbeatTimeoutMs", 3000), 1000, 60000),
            ClearBufferBeforeTrigger = GetBool(parameters, "clearBufferBeforeTrigger", true)
        };
    }

    private static bool GetBool(IReadOnlyDictionary<string, string> parameters, string key, bool fallback)
    {
        return ParameterParser.GetBool(parameters, key, fallback);
    }

    private static string GetParameter(IReadOnlyDictionary<string, string> parameters, string key)
    {
        return ParameterParser.GetString(parameters, key);
    }

    private static int GetInt(IReadOnlyDictionary<string, string> parameters, string key, int fallback)
    {
        return ParameterParser.GetInt(parameters, key, fallback);
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback)
        where TEnum : struct, Enum
    {
        return Enum.TryParse<TEnum>(value, true, out var parsed) ? parsed : fallback;
    }

    private static double GetDouble(IReadOnlyDictionary<string, string> parameters, string key, double fallback)
    {
        return ParameterParser.GetDouble(parameters, key, fallback);
    }

    private static double GetExposureTimeMs(IReadOnlyDictionary<string, string> parameters, double fallback)
    {
        if (parameters.TryGetValue("exposureUs", out var exposureUs) && double.TryParse(exposureUs, out var us))
        {
            return us / 1000.0;
        }

        return GetDouble(parameters, "exposureMs", fallback);
    }

    private async Task MoveAxisAsync(ProcessStepDefinition step, CancellationToken cancellationToken)
    {
        var axis = ResolveAxisController(step.DeviceKey);
        await EnsureAxisConnectedAsync(axis, cancellationToken);
        var targets = step.AxisTargets.Count > 0
            ? step.AxisTargets
            :
            [
                new AxisTargetDefinition
                {
                    AxisKey = string.IsNullOrWhiteSpace(step.AxisKey) ? AxisDefaults.PrimaryAxisKey : step.AxisKey,
                    Position = step.Position,
                    Speed = step.Speed,
                    Acceleration = step.Acceleration
                }
            ];

        var timeout = TimeSpan.FromMilliseconds(Math.Max(step.TimeoutMs, 1000));
        var tasks = targets.Select(target => axis.MoveAbsoluteAsync(
            new AxisMoveCommand
            {
                AxisKey = string.IsNullOrWhiteSpace(target.AxisKey) ? AxisDefaults.PrimaryAxisKey : target.AxisKey,
                Position = target.Position,
                Speed = target.Speed <= 0 ? 100 : target.Speed,
                Acceleration = target.Acceleration <= 0 ? 100 : target.Acceleration,
                Deceleration = target.Acceleration <= 0 ? 100 : target.Acceleration,
                Timeout = timeout,
                WaitForCompletion = true
            },
            cancellationToken));
        await Task.WhenAll(tasks);
    }

    private async Task WaitSignalAsync(
        Recipe recipe,
        ProcessStepDefinition step,
        ProcessExecutionContext context,
        CancellationToken cancellationToken)
    {
        step = ResolveMappedSignalStep(recipe, step);
        var options = CreateSignalWaitOptions(recipe, step);

        if (string.IsNullOrWhiteSpace(options.Address) && !IsCommunicationSource(options.Source))
        {
            if (options.Blocking)
            {
                throw new InvalidOperationException($"Signal address in step '{step.Name}' is empty.");
            }

            _log.Warning("ProcessFlow", $"Signal address is empty in step '{step.Name}'. Step was skipped.");
            return;
        }

        _log.Info(
            "ProcessFlow",
            $"等待步骤 {step.StepNo}: {step.Name}，等待 {options.SourceText} {options.DeviceText}:{options.Address} {DescribeMatchMode(options.MatchMode)} {options.Expected}，超时 {options.TimeoutMs} ms");

        string? matchedValue;
        if (string.Equals(options.Source, "tcp", StringComparison.OrdinalIgnoreCase))
        {
            matchedValue = await WaitTcpSignalAsync(step, options.Expected, options.MatchMode, options.TimeoutMs, options.PollIntervalMs, cancellationToken);
        }
        else if (string.Equals(options.Source, "serial", StringComparison.OrdinalIgnoreCase))
        {
            matchedValue = await WaitSerialSignalAsync(step, options.Expected, options.MatchMode, options.TimeoutMs, options.PollIntervalMs, cancellationToken);
        }
        else if (SignalSourceMapper.IsRuntimeValueSource(options.Source))
        {
            matchedValue = await WaitRuntimeValueSignalAsync(context, options.Address, options.Expected, options.MatchMode, options.TimeoutMs, options.PollIntervalMs, options.DebounceMs, cancellationToken);
        }
        else
        {
            matchedValue = await WaitDeviceSignalAsync(step, options.Address, options.Expected, options.MatchMode, options.TimeoutMs, options.PollIntervalMs, options.DebounceMs, options.Source, cancellationToken);
        }

        if (matchedValue is not null)
        {
            var resultKey = FirstNonEmpty(step.ResultKey, $"{FirstNonEmpty(step.DeviceKey, options.Source)}:{options.Address}");
            SetRuntimeValue(context.RuntimeValues, resultKey, matchedValue);
            SetRuntimeValue(context.RuntimeValues, "LastSignalValue", matchedValue);
            _log.Info(
                "ProcessFlow",
                $"等待完成 {step.StepNo}: {step.Name}，收到 {options.SourceText} {options.DeviceText}:{options.Address} = {TruncateForLog(matchedValue)}");
            return;
        }

        var message = $"等待信号超时：步骤 {step.StepNo} {step.Name}，等待 {options.SourceText} {options.DeviceText}:{options.Address} {DescribeMatchMode(options.MatchMode)} {options.Expected}，超时 {options.TimeoutMs} ms";
        if (options.Blocking)
        {
            throw new TimeoutException(message);
        }

        var timeoutAction = GetParameter(step.Parameters, "onTimeout");
        if (string.Equals(timeoutAction, "Ng", StringComparison.OrdinalIgnoreCase))
        {
            SetRuntimeValue(context.RuntimeValues, "OverallResult", InspectionOutcome.Ng.ToString());
        }

        _log.Warning("ProcessFlow", $"{message} The flow continued.");
    }

    private ProcessStepDefinition ResolveMappedSignalStep(Recipe recipe, ProcessStepDefinition step)
    {
        var parameters = step.Parameters;
        var signalKey = FirstNonEmpty(GetParameter(parameters, "variableKey"), GetParameter(parameters, "signalKey"), step.SignalId);
        var variableSignal = VariableSignalBindingResolver.Resolve(recipe, signalKey);
        if (variableSignal is not null)
        {
            var mappedParameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = variableSignal.Source
            };

            if (!string.IsNullOrWhiteSpace(variableSignal.ChannelKey))
            {
                mappedParameters["channelKey"] = variableSignal.ChannelKey;
            }

            if (!string.IsNullOrWhiteSpace(variableSignal.Payload))
            {
                mappedParameters["payload"] = variableSignal.Payload;
            }

            step = step with
            {
                DeviceKey = variableSignal.DeviceKey,
                SignalId = variableSignal.Address,
                ResultKey = FirstNonEmpty(step.ResultKey, variableSignal.VariableKey),
                Parameters = mappedParameters
            };
            return step;
        }

        var signalMapping = RecipeSignalResolver.ResolveSignalMapping(recipe, signalKey);
        if (signalMapping is not null)
        {
            var mappedParameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = SignalSourceMapper.MapSignalSourceType(signalMapping.SourceType)
            };

            if (!string.IsNullOrWhiteSpace(signalMapping.ChannelKey))
            {
                mappedParameters["channelKey"] = signalMapping.ChannelKey;
            }

            if (!string.IsNullOrWhiteSpace(signalMapping.RequestText))
            {
                mappedParameters["payload"] = signalMapping.RequestText;
            }

            return step with
            {
                DeviceKey = FirstNonEmpty(signalMapping.DeviceKey, signalMapping.ChannelKey, step.DeviceKey),
                SignalId = FirstNonEmpty(signalMapping.Address, step.SignalId),
                Parameters = mappedParameters
            };
        }

        return step;
    }

    private SignalWaitOptions CreateSignalWaitOptions(Recipe recipe, ProcessStepDefinition step)
    {
        var parameters = step.Parameters;
        var signal = RecipeSignalResolver.ResolvePlcSignal(recipe, step.SignalId);
        var address = FirstNonEmpty(GetParameter(parameters, "address"), signal?.Address, step.SignalId);
        var expected = FirstNonEmpty(
            GetParameter(parameters, "expected"),
            GetParameter(parameters, "triggerValue"),
            signal?.TriggerValue,
            "1");
        var matchMode = FirstNonEmpty(GetParameter(parameters, "match"), "Equals");
        var timeoutMs = GetInt(parameters, "timeoutMs", signal?.TimeoutMs > 0 ? signal.TimeoutMs : Math.Max(step.TimeoutMs, 1000));
        var pollIntervalMs = Math.Clamp(GetInt(parameters, "pollIntervalMs", 50), 10, 1000);
        var debounceMs = Math.Clamp(GetInt(parameters, "debounceMs", GetInt(parameters, "stableMs", 0)), 0, timeoutMs);
        var blocking = GetBool(parameters, "blocking", signal?.Blocking ?? true);
        var source = ResolveSignalSource(step);
        var sourceText = DescribeSignalSource(source);
        var deviceText = FirstNonEmpty(step.DeviceKey, GetParameter(parameters, "channelKey"), source);

        return new SignalWaitOptions(
            address,
            expected,
            matchMode,
            timeoutMs,
            pollIntervalMs,
            debounceMs,
            blocking,
            source,
            sourceText,
            deviceText);
    }

    private sealed record SignalWaitOptions(
        string Address,
        string Expected,
        string MatchMode,
        int TimeoutMs,
        int PollIntervalMs,
        int DebounceMs,
        bool Blocking,
        string Source,
        string SourceText,
        string DeviceText);

    private async Task<string?> WaitDeviceSignalAsync(
        ProcessStepDefinition step,
        string address,
        string expected,
        string matchMode,
        int timeoutMs,
        int pollIntervalMs,
        int debounceMs,
        string source,
        CancellationToken cancellationToken)
    {
        return await SignalWaiter.WaitUntilMatchedAsync(
            async token => await ReadSignalValueAsync(step, address, source, token),
            expected,
            matchMode,
            timeoutMs,
            pollIntervalMs,
            debounceMs,
            cancellationToken);
    }

    private static async Task<string?> WaitRuntimeValueSignalAsync(
        ProcessExecutionContext context,
        string key,
        string expected,
        string matchMode,
        int timeoutMs,
        int pollIntervalMs,
        int debounceMs,
        CancellationToken cancellationToken)
    {
        return await SignalWaiter.WaitUntilMatchedAsync(
            _ => Task.FromResult(context.RuntimeValues.TryGetValue(key, out var value) ? value : null),
            expected,
            matchMode,
            timeoutMs,
            pollIntervalMs,
            debounceMs,
            cancellationToken);
    }

    private async Task<string> ReadSignalValueAsync(
        ProcessStepDefinition step,
        string address,
        string source,
        CancellationToken cancellationToken)
    {
        if (IsDigitalIoSource(source, step.DeviceKey))
        {
            var deviceKey = string.IsNullOrWhiteSpace(step.DeviceKey) ? "io-main" : step.DeviceKey;
            var device = _devices.GetRequired<IDigitalIoDeviceClient>(deviceKey, "digital IO signal read");
            await DeviceConnection.EnsureConnectedAsync(device, cancellationToken);
            var status = await device.Controller.GetPointStatusAsync(address, cancellationToken);
            return status.Value ? "1" : "0";
        }

        var addressableDevice = ResolveAddressableDevice(step.DeviceKey);
        await DeviceConnection.EnsureConnectedAsync(addressableDevice, cancellationToken);
        return await addressableDevice.ReadAsync(address, cancellationToken);
    }

    private async Task<string?> WaitTcpSignalAsync(
        ProcessStepDefinition step,
        string expected,
        string matchMode,
        int timeoutMs,
        int pollIntervalMs,
        CancellationToken cancellationToken)
    {
        var channelKey = FirstNonEmpty(GetParameter(step.Parameters, "channelKey"), step.DeviceKey, "tcp-main");
        var channel = _configuration.SystemSettings.Communication.TcpChannels.FirstOrDefault(item =>
            string.Equals(item.Key, channelKey, StringComparison.OrdinalIgnoreCase));
        if (channel is null || !channel.Enabled)
        {
            throw new InvalidOperationException($"TCP channel '{channelKey}' is not configured or enabled.");
        }

        var connectionPolicy = CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy);
        if (connectionPolicy != CommunicationChannelConnectionPolicies.OnDemand)
        {
            var payloadText = GetParameter(step.Parameters, "payload");
            var waitResponse = GetBool(step.Parameters, "waitResponse", true);
            var payloadBytes = string.IsNullOrWhiteSpace(payloadText) ? [] : Encoding.UTF8.GetBytes(payloadText);
            if (!waitResponse)
            {
                await _communicationChannels.TryExchangeTcpAsync(channel, payloadBytes, timeoutMs, false, cancellationToken);
                return string.Empty;
            }

            var runtimeStopwatch = Stopwatch.StartNew();
            while (runtimeStopwatch.ElapsedMilliseconds <= timeoutMs)
            {
                var remaining = Math.Max(1, timeoutMs - (int)runtimeStopwatch.ElapsedMilliseconds);
                var response = await _communicationChannels.TryExchangeTcpAsync(
                    channel,
                    payloadBytes,
                    Math.Min(Math.Max(pollIntervalMs, 50), remaining),
                    true,
                    cancellationToken);
                payloadBytes = [];
                if (response is null)
                {
                    continue;
                }

                var match = CommunicationSignalMatcher.MatchResponse(response, expected, matchMode);
                if (match.MatchedValue is not null)
                {
                    return match.MatchedValue;
                }

                LogIgnoredSignals(step, "TCP", channel.Key, match.IgnoredValues, expected, matchMode);
            }

            return null;
        }

        if (string.Equals(channel.Mode, "Server", StringComparison.OrdinalIgnoreCase))
        {
            return await WaitTcpServerSignalAsync(step, channel, expected, matchMode, timeoutMs, pollIntervalMs, cancellationToken);
        }

        if (!string.Equals(channel.Mode, "Client", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"TCP wait signal only supports client channel '{channelKey}'.");
        }

        using var client = new TcpClient();
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(Math.Max(channel.ConnectTimeoutMs, 100));
        await client.ConnectAsync(channel.Host, channel.Port, connectTimeout.Token);
        await using var stream = client.GetStream();

        var payload = GetParameter(step.Parameters, "payload");
        if (!string.IsNullOrWhiteSpace(payload))
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);
        }

        if (!GetBool(step.Parameters, "waitResponse", true))
        {
            return string.Empty;
        }

        var buffer = new byte[4096];
        var frameBuffer = new List<byte>();
        var frameOptions = CommunicationFrameOptionsFactory.Create(channel);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds <= timeoutMs)
        {
            var remaining = Math.Max(1, timeoutMs - (int)stopwatch.ElapsedMilliseconds);
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(Math.Min(pollIntervalMs, remaining));
            try
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readTimeout.Token);
                if (count <= 0)
                {
                    continue;
                }

                var match = CommunicationSignalMatcher.MatchFrames(
                    frameBuffer,
                    buffer.AsSpan(0, count).ToArray(),
                    frameOptions,
                    expected,
                    matchMode);
                if (match.MatchedValue is not null)
                {
                    return match.MatchedValue;
                }

                LogIgnoredSignals(step, "TCP", channel.Key, match.IgnoredValues, expected, matchMode);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        return null;
    }

    private async Task<string?> WaitTcpServerSignalAsync(
        ProcessStepDefinition step,
        TcpCommunicationChannelSettings channel,
        string expected,
        string matchMode,
        int timeoutMs,
        int pollIntervalMs,
        CancellationToken cancellationToken)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(ResolveListenAddress(channel.Host), channel.Port);
            listener.Start();

            using var acceptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            acceptTimeout.CancelAfter(Math.Max(timeoutMs, 50));
            using var client = await listener.AcceptTcpClientAsync(acceptTimeout.Token);
            await using var stream = client.GetStream();

            var payload = GetParameter(step.Parameters, "payload");
            if (!GetBool(step.Parameters, "waitResponse", true))
            {
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    var bytes = Encoding.UTF8.GetBytes(payload);
                    await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);
                }

                return string.Empty;
            }

            var buffer = new byte[4096];
            var frameBuffer = new List<byte>();
            var frameOptions = CommunicationFrameOptionsFactory.Create(channel);
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds <= timeoutMs)
            {
                var remaining = Math.Max(1, timeoutMs - (int)stopwatch.ElapsedMilliseconds);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(Math.Min(pollIntervalMs, remaining));
                try
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), readTimeout.Token);
                    if (count <= 0)
                    {
                        continue;
                    }

                    var match = CommunicationSignalMatcher.MatchFrames(
                        frameBuffer,
                        buffer.AsSpan(0, count).ToArray(),
                        frameOptions,
                        expected,
                        matchMode);
                    if (match.MatchedValue is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(payload))
                        {
                            var bytes = Encoding.UTF8.GetBytes(payload);
                            await stream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);
                        }

                        return match.MatchedValue;
                    }

                    LogIgnoredSignals(step, "TCP", channel.Key, match.IgnoredValues, expected, matchMode);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }

            return null;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static IPAddress ResolveListenAddress(string? value)
    {
        var host = value?.Trim();
        if (string.IsNullOrWhiteSpace(host) ||
            string.Equals(host, "*", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "0.0.0.0", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Any;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(host, out var address)
            ? address
            : IPAddress.Any;
    }

    private async Task<string?> WaitSerialSignalAsync(
        ProcessStepDefinition step,
        string expected,
        string matchMode,
        int timeoutMs,
        int pollIntervalMs,
        CancellationToken cancellationToken)
    {
        var channelKey = FirstNonEmpty(GetParameter(step.Parameters, "channelKey"), step.DeviceKey, "serial-main");
        var channel = _configuration.SystemSettings.Communication.SerialChannels.FirstOrDefault(item =>
            string.Equals(item.Key, channelKey, StringComparison.OrdinalIgnoreCase));
        if (channel is null || !channel.Enabled)
        {
            throw new InvalidOperationException($"Serial channel '{channelKey}' is not configured or enabled.");
        }

        var connectionPolicy = CommunicationChannelConnectionPolicies.Normalize(channel.ConnectionPolicy);
        if (connectionPolicy != CommunicationChannelConnectionPolicies.OnDemand)
        {
            var payloadText = GetParameter(step.Parameters, "payload");
            var waitResponse = GetBool(step.Parameters, "waitResponse", true);
            var payloadBytes = string.IsNullOrWhiteSpace(payloadText) ? [] : Encoding.UTF8.GetBytes(payloadText);
            if (!waitResponse)
            {
                await _communicationChannels.TryExchangeSerialAsync(channel, payloadBytes, timeoutMs, false, cancellationToken);
                return string.Empty;
            }

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds <= timeoutMs)
            {
                var remaining = Math.Max(1, timeoutMs - (int)stopwatch.ElapsedMilliseconds);
                var response = await _communicationChannels.TryExchangeSerialAsync(
                    channel,
                    payloadBytes,
                    Math.Min(Math.Max(pollIntervalMs, 50), remaining),
                    true,
                    cancellationToken);
                payloadBytes = [];
                if (response is null)
                {
                    continue;
                }

                var match = CommunicationSignalMatcher.MatchResponse(response, expected, matchMode);
                if (match.MatchedValue is not null)
                {
                    return match.MatchedValue;
                }

                LogIgnoredSignals(step, "串口", channel.Key, match.IgnoredValues, expected, matchMode);
            }

            return null;
        }

        return await Task.Run(() =>
        {
            using var port = new SerialPort(
                channel.PortName,
                channel.BaudRate,
                ParseEnum(channel.Parity, Parity.None),
                channel.DataBits,
                ParseEnum(channel.StopBits, StopBits.One))
            {
                ReadTimeout = Math.Clamp(pollIntervalMs, 10, 1000),
                WriteTimeout = Math.Max(timeoutMs, 100)
            };

            port.Open();
            var payload = GetParameter(step.Parameters, "payload");
            if (!string.IsNullOrWhiteSpace(payload))
            {
                port.Write(payload);
            }

            if (!GetBool(step.Parameters, "waitResponse", true))
            {
                return string.Empty;
            }

            var buffer = new byte[4096];
            var frameBuffer = new List<byte>();
            var frameOptions = CommunicationFrameOptionsFactory.Create(channel);
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds <= timeoutMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var available = port.BytesToRead;
                if (available <= 0)
                {
                    Thread.Sleep(pollIntervalMs);
                    continue;
                }

                var count = port.Read(buffer, 0, Math.Min(buffer.Length, available));
                var match = CommunicationSignalMatcher.MatchFrames(
                    frameBuffer,
                    buffer.AsSpan(0, count).ToArray(),
                    frameOptions,
                    expected,
                    matchMode);
                if (match.MatchedValue is not null)
                {
                    return match.MatchedValue;
                }

                LogIgnoredSignals(step, "串口", channel.Key, match.IgnoredValues, expected, matchMode);
            }

            return null;
        }, cancellationToken);
    }

    private string ResolveSignalSource(ProcessStepDefinition step)
    {
        var source = GetParameter(step.Parameters, "source");
        if (!string.IsNullOrWhiteSpace(source))
        {
            return source.Trim();
        }

        var deviceKey = step.DeviceKey?.Trim() ?? string.Empty;
        if (_configuration.SystemSettings.Communication.TcpChannels.Any(channel =>
                string.Equals(channel.Key, deviceKey, StringComparison.OrdinalIgnoreCase)))
        {
            return "tcp";
        }

        if (_configuration.SystemSettings.Communication.SerialChannels.Any(channel =>
                string.Equals(channel.Key, deviceKey, StringComparison.OrdinalIgnoreCase)))
        {
            return "serial";
        }

        return IsDigitalIoSource(string.Empty, deviceKey) ? "digitalIo" : "device";
    }

    private bool IsDigitalIoSource(string source, string? deviceKey)
    {
        if (string.Equals(source, "digitalIo", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, "io", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, "axisInput", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return _devices.TryGet<IDigitalIoDeviceClient>(deviceKey ?? string.Empty, out _);
    }

    private static bool IsCommunicationSource(string source)
    {
        return string.Equals(source, "tcp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(source, "serial", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeSignalSource(string source)
    {
        return source.Trim() switch
        {
            "tcp" or "TCP" => "TCP",
            "serial" or "Serial" => "串口",
            "digitalIo" or "io" or "axisInput" => "轴卡 IO",
            "runtimeValue" or "runtimeValues" => "运行参数",
            "device" or "" => "PLC/设备",
            _ => source
        };
    }

    private static string DescribeMatchMode(string matchMode)
    {
        return SignalMatcher.DescribeMatchMode(matchMode);
    }

    private void LogIgnoredSignal(
        ProcessStepDefinition step,
        string source,
        string channelKey,
        string actual,
        string expected,
        string matchMode)
    {
        _log.Info(
            "ProcessFlow",
            $"等待步骤 {step.StepNo}: {step.Name} 收到 {source} {channelKey} = {TruncateForLog(actual)}，未满足条件：{DescribeMatchMode(matchMode)} {expected}，继续等待");
    }

    private void LogIgnoredSignals(
        ProcessStepDefinition step,
        string source,
        string channelKey,
        IReadOnlyList<string> values,
        string expected,
        string matchMode)
    {
        foreach (var value in values)
        {
            LogIgnoredSignal(step, source, channelKey, value, expected, matchMode);
        }
    }

    private async Task ReadDeviceAsync(
        ProcessStepDefinition step,
        ProcessExecutionContext context,
        CancellationToken cancellationToken)
    {
        var device = ResolveAddressableDevice(step.DeviceKey);
        var result = await _deviceReadStepHandler.ExecuteAsync(step, device, context, cancellationToken);
        LogStepResult(step, $"读取 {result.DeviceKey}:{result.Address} = {TruncateForLog(result.Value)}，保存到 {result.ResultKey}");
    }

    private async Task WriteDeviceAsync(
        Recipe recipe,
        ProcessExecutionContext context,
        ProcessStepDefinition step,
        CancellationToken cancellationToken)
    {
        var device = ResolveAddressableDevice(step.DeviceKey);
        var value = ResolveDeviceWriteValue(recipe, context, step);
        var result = await _deviceWriteStepHandler.ExecuteAsync(step, device, value, context, cancellationToken);
        LogStepResult(step, $"写入 {result.DeviceKey}:{result.Address} = {TruncateForLog(result.Value)}");
    }

    private async Task InvokeDeviceCommandAsync(
        ProcessStepDefinition step,
        ProcessExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (await TryInvokeCommunicationCommandAsync(step, context, cancellationToken))
        {
            return;
        }

        var device = ResolveCommandDevice(step.DeviceKey);
        var result = await _deviceCommandStepHandler.ExecuteAsync(step, device, context, cancellationToken);
        LogStepResult(step, $"命令 {result.CommandName} 返回 {TruncateForLog(result.ContentText)}");
    }

    private async Task<bool> TryInvokeCommunicationCommandAsync(
        ProcessStepDefinition step,
        ProcessExecutionContext context,
        CancellationToken cancellationToken)
    {
        var source = ResolveSignalSource(step);
        if (!IsCommunicationSource(source))
        {
            return false;
        }

        var expected = GetParameter(step.Parameters, "expected");
        var matchMode = FirstNonEmpty(GetParameter(step.Parameters, "match"), "Contains");
        var timeoutMs = GetInt(step.Parameters, "timeoutMs", Math.Max(step.TimeoutMs, 1000));
        var pollIntervalMs = Math.Clamp(GetInt(step.Parameters, "pollIntervalMs", 50), 10, 1000);
        var response = string.Equals(source, "tcp", StringComparison.OrdinalIgnoreCase)
            ? await WaitTcpSignalAsync(step, expected, matchMode, timeoutMs, pollIntervalMs, cancellationToken)
            : await WaitSerialSignalAsync(step, expected, matchMode, timeoutMs, pollIntervalMs, cancellationToken);

        if (response is null)
        {
            throw new TimeoutException($"Timed out waiting for {source} response in step '{step.Name}'.");
        }

        var resultKey = FirstNonEmpty(step.ResultKey, $"{source}.Response");
        SetRuntimeValue(context.RuntimeValues, resultKey, response);
        if (!string.IsNullOrWhiteSpace(step.OutputTarget))
        {
            context.ResultTable[step.OutputTarget] = response;
        }

        var channelKey = FirstNonEmpty(GetParameter(step.Parameters, "channelKey"), step.DeviceKey, step.SignalId);
        LogStepResult(step, $"{source.ToUpperInvariant()} {channelKey} 回复 = {TruncateForLog(response)}，保存到 {resultKey}");

        return true;
    }

    private async Task ProcessStringValueAsync(
        Recipe recipe,
        ProcessStepDefinition step,
        ProcessExecutionContext context,
        CancellationToken cancellationToken)
    {
        var input = await ResolveStringInputValueAsync(recipe, step, context, cancellationToken);
        var output = _stringProcessStepHandler.Execute(step, input ?? string.Empty);
        var target = FirstNonEmpty(step.OutputTarget, step.ResultKey);
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidOperationException($"String output target is empty for step '{step.Name}'.");
        }

        SetRuntimeValue(context.RuntimeValues, target, output);
        SetRuntimeValue(context.RuntimeValues, "LastStringValue", output);
        context.ResultTable[target] = output;
        LogStepResult(
            step,
            $"字符串 {step.ResultKey}='{TruncateForLog(input)}' -> {target}='{TruncateForLog(output)}'，方式 {StringProcessStepHandler.DescribeOperation(step.CommandName)}");
    }

    private async Task<string> ResolveStringInputValueAsync(
        Recipe recipe,
        ProcessStepDefinition step,
        ProcessExecutionContext context,
        CancellationToken cancellationToken)
    {
        var binding = VariableSignalBindingResolver.Resolve(recipe, step.ResultKey);
        if (binding is not null && IsCommunicationSource(binding.Source))
        {
            var parameters = new Dictionary<string, string>(step.Parameters, StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = binding.Source,
                ["channelKey"] = FirstNonEmpty(binding.ChannelKey, binding.DeviceKey),
                ["waitResponse"] = "true"
            };

            if (!string.IsNullOrWhiteSpace(binding.Payload))
            {
                parameters["payload"] = binding.Payload;
            }

            var timeoutMs = GetInt(parameters, "timeoutMs", Math.Max(step.TimeoutMs, 1000));
            var pollIntervalMs = Math.Clamp(GetInt(parameters, "pollIntervalMs", 50), 10, 1000);
            var sourceStep = step with
            {
                DeviceKey = binding.DeviceKey,
                SignalId = binding.Address,
                Parameters = parameters,
                TimeoutMs = timeoutMs
            };

            var received = string.Equals(binding.Source, "tcp", StringComparison.OrdinalIgnoreCase)
                ? await WaitTcpSignalAsync(sourceStep, string.Empty, "Contains", timeoutMs, pollIntervalMs, cancellationToken)
                : await WaitSerialSignalAsync(sourceStep, string.Empty, "Contains", timeoutMs, pollIntervalMs, cancellationToken);

            if (received is null)
            {
                throw new TimeoutException($"Timed out waiting for {binding.Source} message for variable '{step.ResultKey}' in step '{step.Name}'.");
            }

            SetRuntimeValue(context.RuntimeValues, binding.VariableKey, received);
            _log.Info("ProcessFlow", $"变量 {binding.VariableKey} 从 {binding.Source.ToUpperInvariant()} 通道 {FirstNonEmpty(binding.ChannelKey, binding.DeviceKey)} 收到 '{TruncateForLog(received)}'");
            return received;
        }

        if (binding is not null && string.Equals(binding.Source, "device", StringComparison.OrdinalIgnoreCase))
        {
            var value = await ReadSignalValueAsync(
                step with { DeviceKey = binding.DeviceKey },
                binding.Address,
                binding.Source,
                cancellationToken);
            SetRuntimeValue(context.RuntimeValues, binding.VariableKey, value);
            return value;
        }

        if (binding is not null && string.Equals(binding.Source, "digitalIo", StringComparison.OrdinalIgnoreCase))
        {
            var value = await ReadSignalValueAsync(
                step with { DeviceKey = binding.DeviceKey },
                binding.Address,
                binding.Source,
                cancellationToken);
            SetRuntimeValue(context.RuntimeValues, binding.VariableKey, value);
            return value;
        }

        if (TryResolveProcessValue(recipe, context, step.ResultKey, out var input))
        {
            return input;
        }

        throw new InvalidOperationException($"Unable to resolve string input '{step.ResultKey}' in step '{step.Name}'.");
    }

    private void LogStepResult(ProcessStepDefinition step, string result)
    {
        if (!string.IsNullOrWhiteSpace(result))
        {
            _log.Info("ProcessFlow", $"步骤结果 {step.StepNo}: {step.Name}，{result}");
        }
    }

    private static string DescribeProcessStepType(ProcessStepType stepType)
    {
        return stepType switch
        {
            ProcessStepType.AxisMove => "运动控制",
            ProcessStepType.WaitPlcSignal => "等待信号",
            ProcessStepType.AcquireImage => "采图",
            ProcessStepType.RunVisionFlow => "运行视觉",
            ProcessStepType.ReadVisionResult => "读取结果",
            ProcessStepType.WriteResultTable => "写结果表",
            ProcessStepType.WritePlc => "写 PLC",
            ProcessStepType.DeviceRead => "读设备",
            ProcessStepType.DeviceWrite => "写设备",
            ProcessStepType.DeviceCommand => "设备命令",
            ProcessStepType.Delay => "延时",
            ProcessStepType.End => "结束",
            ProcessStepType.ResultJudge => "结果判定",
            ProcessStepType.StringProcess => "字符串处理",
            _ => stepType.ToString()
        };
    }

    private static string TruncateForLog(string? value, int maxLength = 160)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : $"{value[..maxLength]}...";
    }

    private string ResolveDeviceWriteValue(Recipe recipe, ProcessExecutionContext context, ProcessStepDefinition step)
    {
        if (step.Parameters.TryGetValue("value", out var configuredValue) && !string.IsNullOrWhiteSpace(configuredValue))
        {
            return TryResolveProcessValue(recipe, context, configuredValue, out var resolvedValue)
                ? resolvedValue
                : configuredValue;
        }

        if (!string.IsNullOrWhiteSpace(step.ResultKey))
        {
            return TryResolveProcessValue(recipe, context, step.ResultKey, out var value)
                ? value
                : step.ResultKey;
        }

        return "1";
    }

    private static Dictionary<string, string> CreateInitialRuntimeValues(
        Recipe recipe,
        InspectionRequest request,
        DeviceConfiguration configuration)
    {
        return VariableResolver.CreateInitialRuntimeValues(recipe, request, configuration);
    }

    private async Task CaptureAxisRuntimeValuesAsync(
        Recipe recipe,
        ProcessExecutionContext context,
        CancellationToken cancellationToken)
    {
        foreach (var axisKey in ResolveAxisKeys(recipe))
        {
            try
            {
                var status = await _axis.GetAxisStatusAsync(axisKey, cancellationToken);
                SeedAxisStatus(context.RuntimeValues, status);
            }
            catch (Exception ex)
            {
                SetRuntimeValue(context.RuntimeValues, $"Axis.{axisKey}.Error", ex.Message);
                _log.Warning("Axis", $"Unable to capture axis status '{axisKey}': {ex.Message}");
            }
        }
    }

    private IReadOnlyList<string> ResolveAxisKeys(Recipe recipe)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var axis in _configuration.Axes.Where(axis => axis.Enabled))
        {
            SetRuntimeValueKey(keys, axis.Key);
        }

        foreach (var step in recipe.ProcessSteps)
        {
            SetRuntimeValueKey(keys, step.AxisKey);
            foreach (var target in step.AxisTargets)
            {
                SetRuntimeValueKey(keys, target.AxisKey);
            }
        }

        SetRuntimeValueKey(keys, AxisDefaults.PrimaryAxisKey);
        return keys.ToArray();
    }

    private static void SeedAxisStatus(Dictionary<string, string> values, AxisStatus status)
    {
        var prefix = $"Axis.{status.AxisKey}";
        var shortPrefix = status.AxisKey;
        var encoder = FormatRuntimeDouble(status.EncoderPosition);
        var command = FormatRuntimeDouble(status.CommandPosition);

        SetRuntimeValue(values, $"{prefix}.EncoderPosition", encoder);
        SetRuntimeValue(values, $"{prefix}.CommandPosition", command);
        SetRuntimeValue(values, $"{prefix}.Position", encoder);
        SetRuntimeValue(values, $"{prefix}.ServoOn", status.ServoOn.ToString());
        SetRuntimeValue(values, $"{prefix}.Alarm", status.Alarm.ToString());
        SetRuntimeValue(values, $"{prefix}.PositiveLimit", status.PositiveLimit.ToString());
        SetRuntimeValue(values, $"{prefix}.NegativeLimit", status.NegativeLimit.ToString());
        SetRuntimeValue(values, $"{prefix}.Home", status.Home.ToString());
        SetRuntimeValue(values, $"{prefix}.EmergencyStop", status.EmergencyStop.ToString());
        SetRuntimeValue(values, $"{prefix}.Ready", status.Ready.ToString());
        SetRuntimeValue(values, $"{prefix}.InPosition", status.InPosition.ToString());
        SetRuntimeValue(values, $"{prefix}.Homed", status.Homed.ToString());
        SetRuntimeValue(values, $"{prefix}.Message", status.Message);
        SetRuntimeValue(values, $"{prefix}.Timestamp", status.Timestamp.ToString("O", CultureInfo.InvariantCulture));

        SetRuntimeValue(values, $"{shortPrefix}.EncoderPosition", encoder);
        SetRuntimeValue(values, $"{shortPrefix}.CommandPosition", command);
        SetRuntimeValue(values, $"{shortPrefix}.Position", encoder);
        SetRuntimeValue(values, $"{shortPrefix}.InPosition", status.InPosition.ToString());
    }

    private static void SetRuntimeValueKey(HashSet<string> keys, string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            keys.Add(key.Trim());
        }
    }

    private static Recipe ApplyVariableBindings(Recipe recipe, IReadOnlyDictionary<string, string> values)
    {
        return VariableResolver.ApplyVariableBindings(recipe, values);
    }

    private static ProcessStepDefinition ResolveProcessStepVariables(
        ProcessStepDefinition step,
        IReadOnlyDictionary<string, string> values)
    {
        return VariableResolver.ResolveProcessStepVariables(step, values);
    }

    private static void SetRuntimeValue(Dictionary<string, string> values, string? key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        values[key.Trim()] = value ?? string.Empty;
    }

    private static string FormatRuntimeDouble(double value)
    {
        return value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static Recipe ResolveFlowRecipe(Recipe recipe, string? flowId)
    {
        if (string.IsNullOrWhiteSpace(flowId))
        {
            return recipe;
        }

        var flow = recipe.EffectiveFlows.FirstOrDefault(item => string.Equals(item.Id, flowId, StringComparison.OrdinalIgnoreCase));
        return flow is null ? recipe : recipe.WithActiveFlow(flow);
    }

    private static FlowRunResult ToFlowRunResult(Recipe recipe, VisionPipelineResult pipelineResult)
    {
        var flow = recipe.GetActiveFlow();
        return new FlowRunResult(
            flow.Id,
            flow.Name,
            pipelineResult.ResultFrame,
            pipelineResult.ToolResults,
            pipelineResult.Outcome,
            pipelineResult.Barcode,
            pipelineResult.Message);
    }

    private static void EvaluateExpressionVariables(Recipe recipe, ProcessExecutionContext context)
    {
        VariableResolver.EvaluateExpressionVariables(recipe, context.RuntimeValues);
    }

    private static bool TryResolveProcessValue(Recipe recipe, ProcessExecutionContext context, string key, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (context.RuntimeValues.TryGetValue(key, out var runtimeValue))
        {
            value = runtimeValue ?? string.Empty;
            return true;
        }

        if (VisionResultResolver.TryResolveVisionValue(recipe, context.ToolResults, context.LastPipelineResult, key, out value))
        {
            context.RuntimeValues[key] = value;
            return true;
        }

        return false;
    }

    private async Task EnsureCameraConnectedAsync(CancellationToken cancellationToken)
    {
        if (_camera.Snapshot.State != DeviceConnectionState.Connected)
        {
            await _camera.ConnectAsync(cancellationToken);
        }
    }

    private IAxisController ResolveAxisController(string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return _axis;
        }

        if (string.Equals(deviceKey.Trim(), "motion-main", StringComparison.OrdinalIgnoreCase) &&
            !_devices.TryGet<IAxisDeviceClient>(deviceKey, out _))
        {
            return _axis;
        }

        return _devices.GetRequired<IAxisDeviceClient>(deviceKey, "axis motion").Controller;
    }

    private IAddressableDeviceClient ResolveAddressableDevice(string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return _devices.TryGet<IAddressableDeviceClient>("plc-main", out var registered)
                ? registered
                : new PlcDeviceClientAdapter("plc-main", "Main PLC", _plc);
        }

        if (string.Equals(deviceKey.Trim(), "plc-main", StringComparison.OrdinalIgnoreCase) &&
            !_devices.TryGet<IAddressableDeviceClient>(deviceKey, out _))
        {
            return new PlcDeviceClientAdapter("plc-main", "Main PLC", _plc);
        }

        return _devices.GetRequired<IAddressableDeviceClient>(deviceKey, "address read/write");
    }

    private ICommandDeviceClient ResolveCommandDevice(string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return _devices.TryGet<ICommandDeviceClient>("plc-main", out var registered)
                ? registered
                : new PlcDeviceClientAdapter("plc-main", "Main PLC", _plc);
        }

        if (string.Equals(deviceKey.Trim(), "plc-main", StringComparison.OrdinalIgnoreCase) &&
            !_devices.TryGet<ICommandDeviceClient>(deviceKey, out _))
        {
            return new PlcDeviceClientAdapter("plc-main", "Main PLC", _plc);
        }

        return _devices.GetRequired<ICommandDeviceClient>(deviceKey, "device command");
    }

    private static async Task EnsureAxisConnectedAsync(IAxisController axis, CancellationToken cancellationToken)
    {
        if (axis.Snapshot.State != DeviceConnectionState.Connected)
        {
            await axis.ConnectAsync(cancellationToken);
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;
    }

    private static InspectionOutcome ResolveOutcome(ProcessExecutionContext context)
    {
        if (context.RuntimeValues.TryGetValue("OverallResult", out var runtimeOutcome) &&
            Enum.TryParse<InspectionOutcome>(runtimeOutcome, true, out var explicitOutcome))
        {
            return explicitOutcome;
        }

        if (context.LastPipelineResult is not null)
        {
            return context.LastPipelineResult.Outcome;
        }

        return InspectionOutcome.Ok;
    }

    private static string ResolveBarcode(ProcessExecutionContext context)
    {
        if (context.RuntimeValues.TryGetValue("Barcode", out var barcode))
        {
            return barcode;
        }

        return context.LastPipelineResult?.Barcode ?? string.Empty;
    }

    private static string BuildCompletionMessage(ProcessExecutionContext context)
    {
        var baseMessage = context.LastPipelineResult?.Message;
        if (string.IsNullOrWhiteSpace(baseMessage))
        {
            baseMessage = "Process flow completed";
        }

        var tableWrites = context.ResultTable
            .Select(item => $"{item.Key}={item.Value}")
            .ToArray();

        if (context.RuntimeValues.TryGetValue("OverallResult", out var overallResult))
        {
            baseMessage = $"{baseMessage} | Judge={overallResult}";
        }

        return tableWrites.Length == 0
            ? baseMessage
            : $"{baseMessage} | {string.Join(", ", tableWrites)}";
    }

}
