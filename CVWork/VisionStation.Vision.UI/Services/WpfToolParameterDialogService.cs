using System.Windows;
using VisionStation.Vision.UI.Views;
using VisionStation.Vision.UI.ViewModels;
using VisionStation.Devices;
using VisionStation.Domain;
using VisionStation.Infrastructure;
using VisionStation.Vision;

namespace VisionStation.Vision.UI.Services;

public sealed class WpfToolParameterDialogService : IToolParameterDialogService
{
    private readonly ICameraDevice _camera;
    private readonly ICameraDeviceDiscovery _cameraDiscovery;
    private readonly IConfigurableCameraDevice _configurableCamera;
    private readonly ICameraDiagnosticsProvider _cameraDiagnostics;
    private readonly IImageFrameFileService _imageFiles;
    private readonly IVisionPipeline _pipeline;
    private readonly ITemplateMatchingService _templateMatchingService;
    private readonly ITemplateModelStore _templateModelStore;
    private readonly ITemplateModelResourceManager _templateModelResources;
    private readonly IDeviceConfigurationRepository _deviceConfigurationRepository;
    private readonly RuntimePaths _paths;
    private readonly IAppLogService _log;

    public WpfToolParameterDialogService(
        ICameraDevice camera,
        ICameraDeviceDiscovery cameraDiscovery,
        IConfigurableCameraDevice configurableCamera,
        ICameraDiagnosticsProvider cameraDiagnostics,
        IImageFrameFileService imageFiles,
        IVisionPipeline pipeline,
        ITemplateMatchingService templateMatchingService,
        ITemplateModelStore templateModelStore,
        ITemplateModelResourceManager templateModelResources,
        IDeviceConfigurationRepository deviceConfigurationRepository,
        RuntimePaths paths,
        IAppLogService log)
    {
        _camera = camera;
        _cameraDiscovery = cameraDiscovery;
        _configurableCamera = configurableCamera;
        _cameraDiagnostics = cameraDiagnostics;
        _imageFiles = imageFiles;
        _pipeline = pipeline;
        _templateMatchingService = templateMatchingService ?? throw new ArgumentNullException(nameof(templateMatchingService));
        _templateModelStore = templateModelStore ?? throw new ArgumentNullException(nameof(templateModelStore));
        _templateModelResources = templateModelResources ?? throw new ArgumentNullException(nameof(templateModelResources));
        _deviceConfigurationRepository = deviceConfigurationRepository;
        _paths = paths;
        _log = log;
    }

    public ToolParameterDialogResult EditTool(
        VisionToolItem tool,
        IReadOnlyList<RoiChoiceItem> roiChoices,
        IReadOnlyList<RoiDefinition> rois,
        IReadOnlyList<VisionToolKind> toolKinds,
        string flowName,
        Action<ImageFrame>? imageUpdated = null,
        ImageFrame? currentFrame = null,
        Recipe? previewRecipe = null)
    {
        if (tool.Kind == VisionToolKind.AcquireImage)
        {
            return EditAcquireImageTool(tool, flowName, _camera, _cameraDiscovery, _configurableCamera, _cameraDiagnostics, _imageFiles, _log, imageUpdated);
        }

        if (tool.Kind is VisionToolKind.TemplateLocate or VisionToolKind.MultiTargetMatch)
        {
            return EditTemplateLocateTool(
                tool,
                roiChoices,
                rois,
                flowName,
                currentFrame,
                previewRecipe,
                _paths,
                _pipeline,
                _log,
                _templateMatchingService,
                _templateModelStore,
                _templateModelResources);
        }

        if (tool.Kind == VisionToolKind.FindLine)
        {
            return EditFindLineTool(tool, rois, flowName, currentFrame, previewRecipe, _pipeline, _log);
        }

        if (tool.Kind == VisionToolKind.FindCircle)
        {
            return EditFindCircleTool(tool, rois, flowName, currentFrame, previewRecipe, _pipeline, _log);
        }

        if (tool.Kind == VisionToolKind.DefectDetect)
        {
            return EditBlobAnalysisTool(tool, rois, flowName, currentFrame, previewRecipe, _pipeline, _log);
        }

        if (tool.Kind == VisionToolKind.ImageProcess && IsSpecializedImageProcessTool(tool))
        {
            return EditImageProcessTool(tool, flowName, currentFrame, previewRecipe, _pipeline);
        }

        var viewModel = new ToolParameterDialogViewModel(
            tool,
            roiChoices,
            toolKinds,
            currentFrame,
            previewRecipe,
            LoadCommunicationSettings());
        var dialog = new ToolParameterDialog
        {
            DataContext = viewModel,
            Owner = GetOwner()
        };

        viewModel.CloseRequested += (_, accepted) =>
        {
            if (accepted)
            {
                viewModel.ApplyTo(tool);
            }

            dialog.DialogResult = accepted;
            dialog.Close();
        };

        return dialog.ShowDialog() == true
            ? new ToolParameterDialogResult(true, CreatedRois: viewModel.CreatedRois.ToArray())
            : ToolParameterDialogResult.Cancelled;
    }

    private static ToolParameterDialogResult EditAcquireImageTool(
        VisionToolItem tool,
        string flowName,
        ICameraDevice camera,
        ICameraDeviceDiscovery cameraDiscovery,
        IConfigurableCameraDevice configurableCamera,
        ICameraDiagnosticsProvider cameraDiagnostics,
        IImageFrameFileService imageFiles,
        IAppLogService log,
        Action<ImageFrame>? imageUpdated)
    {
        var viewModel = new AcquireImageToolDialogViewModel(tool, flowName, camera, cameraDiscovery, configurableCamera, cameraDiagnostics, imageFiles, log);
        ImageFrame? outputFrame = null;
        var dialog = new AcquireImageToolDialog
        {
            DataContext = viewModel,
            Owner = GetOwner()
        };

        viewModel.FrameUpdated += (_, frame) =>
        {
            outputFrame = frame;
            imageUpdated?.Invoke(frame);
        };

        viewModel.CloseRequested += (_, accepted) =>
        {
            if (accepted)
            {
                viewModel.ApplyTo(tool);
            }

            dialog.DialogResult = accepted;
            dialog.Close();
        };

        return dialog.ShowDialog() == true
            ? new ToolParameterDialogResult(true, outputFrame ?? viewModel.CurrentFrame, viewModel.RunFlowRequested)
            : ToolParameterDialogResult.Cancelled;
    }

    private static ToolParameterDialogResult EditTemplateLocateTool(
        VisionToolItem tool,
        IReadOnlyList<RoiChoiceItem> roiChoices,
        IReadOnlyList<RoiDefinition> rois,
        string flowName,
        ImageFrame? currentFrame,
        Recipe? previewRecipe,
        RuntimePaths paths,
        IVisionPipeline pipeline,
        IAppLogService log,
        ITemplateMatchingService templateMatchingService,
        ITemplateModelStore templateModelStore,
        ITemplateModelResourceManager templateModelResources)
    {
        var viewModel = new TemplateLocateToolDialogViewModel(
            tool,
            roiChoices,
            rois,
            flowName,
            currentFrame,
            paths,
            log,
            previewRecipe,
            pipeline,
            templateMatchingService,
            templateModelStore,
            templateModelResources);
        var dialog = new TemplateLocateToolDialog
        {
            DataContext = viewModel,
            Owner = GetOwner()
        };
        var allowClose = false;
        var closeInProgress = false;
        var cancelCloseRequested = false;

        dialog.Loaded += async (_, _) =>
        {
            try
            {
                await viewModel.InitializeAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                log.Error("VisionDebug", $"{tool.Name} template dialog initialization failed: {exception.Message}");
            }
        };

        viewModel.CloseRequested += async (_, accepted) =>
        {
            if (closeInProgress)
            {
                if (!accepted)
                {
                    cancelCloseRequested = true;
                    viewModel.CancelPendingOperations();
                }

                return;
            }

            closeInProgress = true;
            cancelCloseRequested = !accepted;
            try
            {
                if (accepted)
                {
                    if (!await viewModel.PrepareToCloseAsync())
                    {
                        closeInProgress = false;
                        return;
                    }

                    await viewModel.CancelAndDrainAsync();
                    if (cancelCloseRequested)
                    {
                        await viewModel.CancelAndRetireAsync();
                        allowClose = true;
                        dialog.DialogResult = false;
                        return;
                    }

                    if (!viewModel.ApplyTo(tool))
                    {
                        closeInProgress = false;
                        return;
                    }

                    allowClose = true;
                }
                else
                {
                    await viewModel.CancelAndRetireAsync();
                    allowClose = true;
                }

                dialog.DialogResult = accepted;
            }
            catch (OperationCanceledException)
            {
                if (!cancelCloseRequested)
                {
                    closeInProgress = false;
                    return;
                }

                try
                {
                    await viewModel.CancelAndRetireAsync();
                    allowClose = true;
                    dialog.DialogResult = false;
                }
                catch (Exception exception)
                {
                    closeInProgress = false;
                    log.Error("VisionDebug", $"{tool.Name} cancelling template dialog failed: {exception.Message}");
                }
            }
            catch (Exception exception)
            {
                closeInProgress = false;
                log.Error("VisionDebug", $"{tool.Name} closing template dialog failed: {exception.Message}");
            }
        };

        dialog.Closing += async (_, args) =>
        {
            if (allowClose)
            {
                return;
            }

            args.Cancel = true;
            if (closeInProgress)
            {
                cancelCloseRequested = true;
                viewModel.CancelPendingOperations();
                return;
            }

            closeInProgress = true;
            cancelCloseRequested = true;
            try
            {
                await viewModel.CancelAndRetireAsync();
                allowClose = true;
                dialog.Close();
            }
            catch (Exception exception)
            {
                closeInProgress = false;
                log.Error("VisionDebug", $"{tool.Name} cancelling template dialog close failed: {exception.Message}");
            }
        };

        return dialog.ShowDialog() == true
            ? new ToolParameterDialogResult(
                true,
                null,
                viewModel.RunFlowRequested,
                viewModel.CreatedRois.ToArray(),
                viewModel.RemovedRoiIds.ToArray())
            : ToolParameterDialogResult.Cancelled;
    }

    private static ToolParameterDialogResult EditFindLineTool(
        VisionToolItem tool,
        IReadOnlyList<RoiDefinition> rois,
        string flowName,
        ImageFrame? currentFrame,
        Recipe? previewRecipe,
        IVisionPipeline pipeline,
        IAppLogService log)
    {
        var viewModel = new FindLineToolDialogViewModel(tool, rois, flowName, currentFrame, previewRecipe, pipeline, log);
        var dialog = new FindLineToolDialog
        {
            DataContext = viewModel,
            Owner = GetOwner()
        };

        viewModel.CloseRequested += async (_, accepted) =>
        {
            if (accepted && !await viewModel.ApplyToAsync(tool))
            {
                return;
            }

            dialog.DialogResult = accepted;
            dialog.Close();
        };

        return dialog.ShowDialog() == true
            ? new ToolParameterDialogResult(
                true,
                null,
                viewModel.RunFlowRequested,
                viewModel.CreatedRois.ToArray(),
                viewModel.RemovedRoiIds.ToArray())
            : ToolParameterDialogResult.Cancelled;
    }

    private static ToolParameterDialogResult EditFindCircleTool(
        VisionToolItem tool,
        IReadOnlyList<RoiDefinition> rois,
        string flowName,
        ImageFrame? currentFrame,
        Recipe? previewRecipe,
        IVisionPipeline pipeline,
        IAppLogService log)
    {
        var viewModel = new FindCircleToolDialogViewModel(tool, rois, flowName, currentFrame, previewRecipe, pipeline, log);
        var dialog = new FindCircleToolDialog
        {
            DataContext = viewModel,
            Owner = GetOwner()
        };

        viewModel.CloseRequested += async (_, accepted) =>
        {
            if (accepted && !await viewModel.ApplyToAsync(tool))
            {
                return;
            }

            dialog.DialogResult = accepted;
            dialog.Close();
        };

        return dialog.ShowDialog() == true
            ? new ToolParameterDialogResult(
                true,
                null,
                viewModel.RunFlowRequested,
                viewModel.CreatedRois.ToArray(),
                viewModel.RemovedRoiIds.ToArray())
            : ToolParameterDialogResult.Cancelled;
    }

    private static ToolParameterDialogResult EditBlobAnalysisTool(
        VisionToolItem tool,
        IReadOnlyList<RoiDefinition> rois,
        string flowName,
        ImageFrame? currentFrame,
        Recipe? previewRecipe,
        IVisionPipeline pipeline,
        IAppLogService log)
    {
        var viewModel = new BlobAnalysisToolDialogViewModel(tool, rois, flowName, currentFrame, previewRecipe, pipeline, log);
        var dialog = new BlobAnalysisToolDialog
        {
            DataContext = viewModel,
            Owner = GetOwner()
        };

        viewModel.CloseRequested += async (_, accepted) =>
        {
            if (accepted && !await viewModel.ApplyToAsync(tool))
            {
                return;
            }

            dialog.DialogResult = accepted;
            dialog.Close();
        };

        return dialog.ShowDialog() == true
            ? new ToolParameterDialogResult(
                true,
                null,
                viewModel.RunFlowRequested,
                viewModel.CreatedRois.ToArray(),
                viewModel.RemovedRoiIds.ToArray())
            : ToolParameterDialogResult.Cancelled;
    }

    private static ToolParameterDialogResult EditImageProcessTool(
        VisionToolItem tool,
        string flowName,
        ImageFrame? currentFrame,
        Recipe? previewRecipe,
        IVisionPipeline pipeline)
    {
        var viewModel = new ImageProcessToolDialogViewModel(tool, flowName, currentFrame, previewRecipe, pipeline);
        var dialog = new ImageProcessToolDialog
        {
            DataContext = viewModel,
            Owner = GetOwner()
        };

        viewModel.CloseRequested += (_, accepted) =>
        {
            if (accepted)
            {
                viewModel.ApplyTo(tool);
            }

            dialog.DialogResult = accepted;
            dialog.Close();
        };

        return dialog.ShowDialog() == true
            ? new ToolParameterDialogResult(true)
            : ToolParameterDialogResult.Cancelled;
    }

    private static bool IsSpecializedImageProcessTool(VisionToolItem tool)
    {
        var parameters = ParseParameters(tool.ParametersText);
        var operation = NormalizeImageProcessOperation(parameters.GetValueOrDefault("operation"));
        if (operation is "Threshold" or "Filter" or "Morphology")
        {
            return true;
        }

        return tool.Name.Contains("二值", StringComparison.OrdinalIgnoreCase) ||
               tool.Name.Contains("滤波", StringComparison.OrdinalIgnoreCase) ||
               tool.Name.Contains("降噪", StringComparison.OrdinalIgnoreCase) ||
               tool.Name.Contains("形态", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeImageProcessOperation(string? value)
    {
        return value?.Trim() switch
        {
            "Filter" or "Blur" or "Denoise" => "Filter",
            "Morph" or "Morphology" => "Morphology",
            "Threshold" or "Binary" or "Binarize" => "Threshold",
            _ => string.Empty
        };
    }

    private static Dictionary<string, string> ParseParameters(string text)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in text.Split(["\r\n", "\n", ";"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = segment.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            parameters[segment[..index].Trim()] = index == segment.Length - 1
                ? string.Empty
                : segment[(index + 1)..].Trim();
        }

        return parameters;
    }

    private CommunicationChannelSettings LoadCommunicationSettings()
    {
        try
        {
            return _deviceConfigurationRepository.GetAsync().GetAwaiter().GetResult().SystemSettings.Communication;
        }
        catch
        {
            return new CommunicationChannelSettings();
        }
    }

    private static Window? GetOwner()
    {
        return System.Windows.Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive) ?? System.Windows.Application.Current.MainWindow;
    }
}
