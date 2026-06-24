using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Application;
using VisionStation.Application.Presentation;

namespace VisionStation.Client.ViewModels;

public sealed class GlobalSaveViewModel : BindableBase
{
    private readonly IUnsavedChangesService _unsavedChanges;
    private readonly IUiDispatcher _uiDispatcher;
    private int _unsavedChangeCount;
    private bool _isSaving;

    public GlobalSaveViewModel(IUnsavedChangesService unsavedChanges, IUiDispatcher uiDispatcher)
    {
        _unsavedChanges = unsavedChanges;
        _uiDispatcher = uiDispatcher;
        SaveAllCommand = new DelegateCommand(
            async () => await ExecuteSaveAllCommandAsync(),
            () => CanSaveAll);

        _unsavedChanges.Changed += OnUnsavedChangesChanged;
        RefreshState();
    }

    public event EventHandler<string>? SaveFailed;

    public DelegateCommand SaveAllCommand { get; }

    public bool HasUnsavedChanges => UnsavedChangeCount > 0;

    public int UnsavedChangeCount
    {
        get => _unsavedChangeCount;
        private set
        {
            if (SetProperty(ref _unsavedChangeCount, value))
            {
                RaiseSaveStateChanged();
            }
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                RaiseSaveStateChanged();
            }
        }
    }

    public bool CanSaveAll => HasUnsavedChanges && !IsSaving;

    public string SaveAllButtonText
    {
        get
        {
            if (IsSaving)
            {
                return "保存中...";
            }

            return UnsavedChangeCount > 0
                ? $"保存全部 ({UnsavedChangeCount})"
                : "保存全部";
        }
    }

    public string SaveAllToolTip
    {
        get
        {
            if (IsSaving)
            {
                return "正在保存所有未保存修改";
            }

            return UnsavedChangeCount > 0
                ? $"保存 {UnsavedChangeCount} 项未保存修改"
                : "暂无未保存修改";
        }
    }

    public async Task SaveAllAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSaveAll)
        {
            return;
        }

        IsSaving = true;
        try
        {
            await _unsavedChanges.SaveAllAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            IsSaving = false;
            RefreshState();
        }
    }

    private async Task ExecuteSaveAllCommandAsync()
    {
        try
        {
            await SaveAllAsync();
        }
        catch (Exception ex)
        {
            SaveFailed?.Invoke(this, ex.Message);
        }
    }

    private void OnUnsavedChangesChanged(object? sender, EventArgs e)
    {
        _uiDispatcher.Invoke(RefreshState);
    }

    private void RefreshState()
    {
        UnsavedChangeCount = _unsavedChanges.GetUnsavedChanges().Count;
    }

    private void RaiseSaveStateChanged()
    {
        RaisePropertyChanged(nameof(HasUnsavedChanges));
        RaisePropertyChanged(nameof(CanSaveAll));
        RaisePropertyChanged(nameof(SaveAllButtonText));
        RaisePropertyChanged(nameof(SaveAllToolTip));
        SaveAllCommand.RaiseCanExecuteChanged();
    }
}
