using VisionStation.Domain;

namespace VisionStation.Vision.UI.Services;

public interface IImageFrameFileService
{
    Task<ImageFrame?> PickImageAsync(CancellationToken cancellationToken = default);

    Task<ImageFrame> LoadImageAsync(string path, CancellationToken cancellationToken = default);

    Task<string?> PickDirectoryAsync(CancellationToken cancellationToken = default);

    Task<string?> SaveImageAsync(ImageFrame frame, CancellationToken cancellationToken = default);
}
