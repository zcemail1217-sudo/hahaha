namespace VisionStation.Vision;

/// <summary>
/// Observes the synchronous native shape-model call without exposing HALCON types. The default
/// implementation is inert; the isolated TestHost uses the internal seam to prove that a user
/// cancellation happens after native admission and before the operator safely returns.
/// </summary>
internal interface IHalconFindScaledShapeObserver
{
    void OnStarted();

    void OnCompleted();
}

internal sealed class NullHalconFindScaledShapeObserver : IHalconFindScaledShapeObserver
{
    internal static NullHalconFindScaledShapeObserver Instance { get; } = new();

    private NullHalconFindScaledShapeObserver()
    {
    }

    public void OnStarted()
    {
    }

    public void OnCompleted()
    {
    }
}
