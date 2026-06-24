namespace VisionStation.Application;

public interface IUiDispatcher
{
    void Invoke(Action action);
}
