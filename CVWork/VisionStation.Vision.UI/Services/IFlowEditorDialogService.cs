namespace VisionStation.Vision.UI.Services;

/// <summary>按配方打开共享视觉流程编辑工作区。</summary>
public interface IFlowEditorDialogService
{
    /// <summary>打开共享流程编辑器；指定配方时先串行加载，null 时保留当前编辑状态。</summary>
    Task ShowEditorAsync(
        string? recipeId = null,
        CancellationToken cancellationToken = default);
}
