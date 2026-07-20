namespace VisionStation.Domain;

public enum InspectionOutcome
{
    None,
    Ok,
    Ng,
    Error
}

public enum DeviceConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Faulted
}

public enum ProductionState
{
    Stopped,
    Running,
    Paused,
    Faulted,
    Starting,
    Stopping
}

public enum RoiShapeKind
{
    Rectangle,
    RotatedRectangle,
    Circle,
    Polygon
}

public enum VisionToolKind
{
    AcquireImage,
    ImageProcess,
    TemplateLocate,
    MultiTargetMatch,
    CoordinateTransform,
    RoiMap,
    FindLine,
    FindCircle,
    MeasureDistance,
    CodeRead,
    Ocr,
    DefectDetect,
    Judge,
    LineAngle,
    LineIntersection,
    FitLineFromPoints,
    TemplatePoint,
    TcpCommunication,
    SerialCommunication,
    Result
}

public enum AlarmSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public enum AlarmLifecycleState
{
    Active,
    Acknowledged,
    Cleared
}

public enum PixelFormatKind
{
    Gray8,
    Bgr24,
    Bgra32
}

public enum ProcessStepType
{
    AxisMove,
    WaitPlcSignal,
    AcquireImage,
    RunVisionFlow,
    ReadVisionResult,
    WriteResultTable,
    WritePlc,
    DeviceRead,
    DeviceWrite,
    DeviceCommand,
    Delay,
    End,
    ResultJudge,
    StringProcess
}

public sealed record Point2D(double X, double Y);

public sealed record Pose2D(double X, double Y, double Angle);

public sealed record Line2D(Point2D Start, Point2D End);

public sealed record Circle2D(Point2D Center, double Radius);

public sealed record ImageFrame(
    string Id,
    int Width,
    int Height,
    int Stride,
    PixelFormatKind Format,
    byte[] Pixels,
    DateTimeOffset Timestamp,
    string Source);

public sealed record DeviceSnapshot(
    string Name,
    DeviceConnectionState State,
    string Message,
    DateTimeOffset Timestamp);

public sealed record AlarmEvent(
    string Id,
    AlarmSeverity Severity,
    string Source,
    string Message,
    DateTimeOffset Timestamp,
    bool Acknowledged = false,
    DateTimeOffset? AcknowledgedAt = null,
    DateTimeOffset? ClearedAt = null,
    string Details = "")
{
    public bool IsActive => ClearedAt is null;

    public AlarmLifecycleState State => ClearedAt is not null
        ? AlarmLifecycleState.Cleared
        : Acknowledged
            ? AlarmLifecycleState.Acknowledged
            : AlarmLifecycleState.Active;
}

public sealed record TracePolicy
{
    public bool SaveOkImages { get; init; } = true;

    public bool SaveNgImages { get; init; } = true;

    public string ImageFormat { get; init; } = "Bmp";

    public int RetentionDays { get; init; } = 30;

    public long MaxStorageMegabytes { get; init; } = 20_480;
}

public sealed record CameraSettings
{
    public string CameraId { get; init; } = "SIM-CAM-01";

    public string DisplayName { get; init; } = "模拟相机 01";

    public double ExposureTimeUs { get; init; } = 8000;

    public double Gain { get; init; } = 1.0;

    public bool HardwareTrigger { get; init; }

    public CameraCalibrationResult? CameraCalibration { get; init; }

    public PlaneCalibrationResult? PlaneCalibration { get; init; }
}

public sealed record ProductParameterDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "Parameter";

    public string Value { get; init; } = string.Empty;

    public string Unit { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public bool Editable { get; init; } = true;
}

public sealed record RecipeVariableDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Key { get; init; } = "Variable";

    public string Name { get; init; } = "变量";

    public string Direction { get; init; } = "Input";

    public string DataType { get; init; } = "string";

    public string DefaultValue { get; init; } = string.Empty;

    public string CurrentValue { get; init; } = string.Empty;

    public string Unit { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Target { get; init; } = string.Empty;

    public string Expression { get; init; } = string.Empty;

    public bool Required { get; init; }

    public bool Editable { get; init; } = true;

    public bool Enabled { get; init; } = true;

    public string Description { get; init; } = string.Empty;
}

public sealed record SignalMappingDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Key { get; init; } = "Signal";

    public string Name { get; init; } = "逻辑信号";

    public string DataType { get; init; } = "bool";

    public string SourceType { get; init; } = "PLC";

    public string DeviceKey { get; init; } = string.Empty;

    public string Address { get; init; } = string.Empty;

    public string ChannelKey { get; init; } = string.Empty;

    public string RequestText { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public string Description { get; init; } = string.Empty;
}

public sealed record MotionStepDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "Step";

    public string CommandType { get; init; } = "MoveAbsolute";

    public string AxisKey { get; init; } = string.Empty;

    public double Position { get; init; }

    public double Speed { get; init; } = 100;

    public double Acceleration { get; init; } = 100;

    public string WaitSignalId { get; init; } = string.Empty;

    public int TimeoutMs { get; init; } = 3000;

    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record MotionSequenceDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "Sequence";

    public string ControllerProfile { get; init; } = "Generic";

    public string Description { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public IReadOnlyList<MotionStepDefinition> Steps { get; init; } = Array.Empty<MotionStepDefinition>();
}

public sealed record AxisTargetDefinition
{
    public string AxisKey { get; init; } = string.Empty;

    public double Position { get; init; }

    public double Speed { get; init; } = 100;

    public double Acceleration { get; init; } = 100;
}

public sealed record ProcessStepDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public int StepNo { get; init; }

    public string Name { get; init; } = "流程步骤";

    public ProcessStepType StepType { get; init; } = ProcessStepType.Delay;

    public bool Enabled { get; init; } = true;

    public string DeviceKey { get; init; } = string.Empty;

    public string AxisKey { get; init; } = string.Empty;

    public double Position { get; init; }

    public double Speed { get; init; }

    public double Acceleration { get; init; }

    public IReadOnlyList<AxisTargetDefinition> AxisTargets { get; init; } = Array.Empty<AxisTargetDefinition>();

    public string FlowId { get; init; } = string.Empty;

    public string SignalId { get; init; } = string.Empty;

    public string ResultKey { get; init; } = string.Empty;

    public string OutputTarget { get; init; } = string.Empty;

    public string CommandName { get; init; } = string.Empty;

    public double? LowerLimit { get; init; }

    public double? UpperLimit { get; init; }

    public int DelayMs { get; init; }

    public int TimeoutMs { get; init; } = 3000;

    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string Description { get; init; } = string.Empty;
}

public sealed record VisionResultDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "Result";

    public string FlowId { get; init; } = string.Empty;

    public string SourceToolId { get; init; } = string.Empty;

    public string SourceKey { get; init; } = string.Empty;

    public string DataType { get; init; } = "string";

    public string Unit { get; init; } = string.Empty;

    public bool ParticipateInJudge { get; init; }

    public string ExternalAlias { get; init; } = string.Empty;

    public string PlcAddress { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;
}

public sealed record PlcSignalDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "Signal";

    public string Address { get; init; } = string.Empty;

    public string Direction { get; init; } = "Read";

    public string TriggerValue { get; init; } = "1";

    public int TimeoutMs { get; init; } = 3000;

    public bool Blocking { get; init; } = true;

    public string Description { get; init; } = string.Empty;
}

public sealed record RoiDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "ROI";

    public RoiShapeKind Shape { get; init; } = RoiShapeKind.Rectangle;

    public double X { get; init; }

    public double Y { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public double Angle { get; init; }

    public double Radius { get; init; }

    public IReadOnlyList<Point2D> Points { get; init; } = Array.Empty<Point2D>();
}

public sealed record VisionToolDefinition
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = "视觉工具";

    public VisionToolKind Kind { get; init; }

    public bool Enabled { get; init; } = true;

    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record VisionFlowDefinition
{
    public string Id { get; init; } = "main";

    public string Name { get; init; } = "MainFlow";

    public string Description { get; init; } = string.Empty;

    public IReadOnlyList<RoiDefinition> Rois { get; init; } = Array.Empty<RoiDefinition>();

    public IReadOnlyList<VisionToolDefinition> Tools { get; init; } = Array.Empty<VisionToolDefinition>();

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record Recipe
{
    public string Id { get; init; } = "default";

    public string Name { get; init; } = "默认配方";

    public string ProductCode { get; init; } = "P-001";

    public string Description { get; init; } = "定位 + 测量 + 缺陷检测流程";

    public CameraSettings Camera { get; init; } = new();

    public IReadOnlyList<ProductParameterDefinition> ProductParameters { get; init; } = Array.Empty<ProductParameterDefinition>();

    public IReadOnlyList<RecipeVariableDefinition> Variables { get; init; } = Array.Empty<RecipeVariableDefinition>();

    public IReadOnlyList<SignalMappingDefinition> SignalMappings { get; init; } = Array.Empty<SignalMappingDefinition>();

    public IReadOnlyList<RoiDefinition> Rois { get; init; } = Array.Empty<RoiDefinition>();

    public IReadOnlyList<VisionToolDefinition> Tools { get; init; } = Array.Empty<VisionToolDefinition>();

    public string CurrentFlowId { get; init; } = "main";

    public IReadOnlyList<VisionFlowDefinition> Flows { get; init; } = Array.Empty<VisionFlowDefinition>();

    public IReadOnlyList<MotionSequenceDefinition> MotionSequences { get; init; } = Array.Empty<MotionSequenceDefinition>();

    public IReadOnlyList<ProcessStepDefinition> ProcessSteps { get; init; } = Array.Empty<ProcessStepDefinition>();

    public IReadOnlyList<VisionResultDefinition> VisionResults { get; init; } = Array.Empty<VisionResultDefinition>();

    public IReadOnlyList<PlcSignalDefinition> PlcSignals { get; init; } = Array.Empty<PlcSignalDefinition>();

    public TracePolicy TracePolicy { get; init; } = new();

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;

    public IReadOnlyList<VisionFlowDefinition> EffectiveFlows =>
        Flows.Count > 0
            ? Flows
            :
            [
                new VisionFlowDefinition
                {
                    Id = string.IsNullOrWhiteSpace(CurrentFlowId) ? "main" : CurrentFlowId,
                    Name = string.IsNullOrWhiteSpace(ProductCode) ? Name : ProductCode,
                    Description = Description,
                    Rois = Rois,
                    Tools = Tools,
                    UpdatedAt = UpdatedAt
                }
            ];

    public VisionFlowDefinition GetActiveFlow()
    {
        var flows = EffectiveFlows;
        return flows.FirstOrDefault(flow => string.Equals(flow.Id, CurrentFlowId, StringComparison.OrdinalIgnoreCase))
               ?? flows.First();
    }

    public Recipe WithNormalizedFlows()
    {
        var activeFlow = GetActiveFlow();
        return this with
        {
            CurrentFlowId = activeFlow.Id,
            Flows = EffectiveFlows,
            Rois = activeFlow.Rois,
            Tools = activeFlow.Tools
        };
    }

    public Recipe WithActiveFlow(VisionFlowDefinition flow)
    {
        var flows = EffectiveFlows.ToList();
        var index = flows.FindIndex(item => string.Equals(item.Id, flow.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            flows[index] = flow;
        }
        else
        {
            flows.Add(flow);
        }

        return this with
        {
            CurrentFlowId = flow.Id,
            Flows = flows,
            Rois = flow.Rois,
            Tools = flow.Tools,
            UpdatedAt = DateTimeOffset.Now
        };
    }
}

public sealed record InspectionRequest
{
    public string RecipeId { get; init; } = string.Empty;

    /// <summary>
    /// Gets the logical immutable recipe snapshot for this inspection.
    /// When null, the inspection execution module resolves <see cref="RecipeId" />
    /// from the recipe repository.
    /// </summary>
    /// <remarks>
    /// After calling ExecuteAsync, callers must not mutate collections reachable from this snapshot.
    /// When supplied, <see cref="Recipe.Id" /> must not be null, empty, or whitespace.
    /// When both values are supplied, <see cref="RecipeId" /> must match <see cref="Recipe.Id" />
    /// using <see cref="StringComparison.OrdinalIgnoreCase" />. A mismatch causes the inspection
    /// execution module to throw an <see cref="ArgumentException" /> before any inspection execution
    /// side effects occur.
    /// </remarks>
    public Recipe? RecipeSnapshot { get; init; }

    public string BatchId { get; init; } = DateTimeOffset.Now.ToString("yyyyMMdd");

    public string OperatorName { get; init; } = "Operator";

    public bool TriggeredByPlc { get; init; }

    public bool ProcessOnly { get; init; }

    public IReadOnlyDictionary<string, string> RuntimeVariables { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ToolResult
{
    public string ToolId { get; init; } = string.Empty;

    public string ToolName { get; init; } = string.Empty;

    public VisionToolKind Kind { get; init; }

    public InspectionOutcome Outcome { get; init; } = InspectionOutcome.Ok;

    public TimeSpan Duration { get; init; }

    public string Message { get; init; } = string.Empty;

    public Dictionary<string, string> Data { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record InspectionResult
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    public string RecipeId { get; init; } = string.Empty;

    public string RecipeName { get; init; } = string.Empty;

    public string BatchId { get; init; } = string.Empty;

    public InspectionOutcome Outcome { get; init; } = InspectionOutcome.None;

    public TimeSpan CycleTime { get; init; }

    public string Barcode { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string OriginalImagePath { get; init; } = string.Empty;

    public string ResultImagePath { get; init; } = string.Empty;

    public IReadOnlyList<ToolResult> ToolResults { get; init; } = Array.Empty<ToolResult>();

    public IReadOnlyDictionary<string, string> ResultData { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ImageTracePaths(string OriginalImagePath, string ResultImagePath, string MetadataPath);

public sealed record FlowResource
{
    public string ResourceId { get; init; } = Guid.NewGuid().ToString("N");

    public string RecipeId { get; init; } = string.Empty;

    public string FlowId { get; init; } = string.Empty;

    public string ToolId { get; init; } = string.Empty;

    public string ModelPath { get; init; } = string.Empty;

    public string ModelVersion { get; init; } = "1.0";

    public string Description { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record AppLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message);
