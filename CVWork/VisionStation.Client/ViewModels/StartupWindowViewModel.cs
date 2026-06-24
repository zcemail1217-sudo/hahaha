using System.Collections.ObjectModel;
using Prism.Mvvm;
using VisionStation.Client.Services;

namespace VisionStation.Client.ViewModels;

public sealed class StartupWindowViewModel : BindableBase
{
    private readonly ISystemLoadingService _loadingService;
    private string _loadingMessage = "系统启动加载";
    private string _loadingDetail = "正在按顺序初始化轴卡、数字IO、相机、PLC、配方文件、图像文件和视觉引擎。";

    public StartupWindowViewModel(ISystemLoadingService loadingService)
    {
        _loadingService = loadingService;
        _loadingService.SnapshotChanged += (_, snapshot) => ApplySnapshot(snapshot);
        ApplySnapshot(_loadingService.Current);
    }

    public ObservableCollection<SystemLoadingStageSnapshot> LoadingStages { get; } = new();

    public string LoadingMessage
    {
        get => _loadingMessage;
        private set => SetProperty(ref _loadingMessage, value);
    }

    public string LoadingDetail
    {
        get => _loadingDetail;
        private set => SetProperty(ref _loadingDetail, value);
    }

    private void ApplySnapshot(SystemLoadingSnapshot snapshot)
    {
        LoadingMessage = snapshot.Message;
        LoadingDetail = snapshot.Detail;

        LoadingStages.Clear();
        foreach (var stage in snapshot.Stages)
        {
            LoadingStages.Add(stage);
        }
    }
}
