using System.Collections;
using Prism.Commands;
using Prism.Mvvm;
using VisionStation.Domain;

namespace VisionStation.Vision.UI.ViewModels;

public sealed class ImagePreviewWindowViewModel : BindableBase
{
    private ImageFrame? _currentFrame;
    private IEnumerable? _overlayItemsSource;

    public ImagePreviewWindowViewModel(ImageFrame? currentFrame, IEnumerable? overlayItemsSource, string title)
    {
        _currentFrame = currentFrame;
        _overlayItemsSource = overlayItemsSource;
        Title = title;
        CloseCommand = new DelegateCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? CloseRequested;

    public string Title { get; }

    public DelegateCommand CloseCommand { get; }

    public ImageFrame? CurrentFrame
    {
        get => _currentFrame;
        set => SetProperty(ref _currentFrame, value);
    }

    public IEnumerable? OverlayItemsSource
    {
        get => _overlayItemsSource;
        set => SetProperty(ref _overlayItemsSource, value);
    }
}
