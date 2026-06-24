namespace VisionStation.Application;

public interface IInspectionRunControl
{
    bool IsPaused { get; }

    void BeginRun();

    void EndRun();

    void Pause();

    void Resume();

    void RequestReset();

    Task WaitIfPausedOrResetAsync(CancellationToken cancellationToken);
}

public sealed class InspectionRunResetException : OperationCanceledException
{
    public InspectionRunResetException()
        : base("Inspection run reset requested.")
    {
    }
}

public sealed class InspectionRunControl : IInspectionRunControl
{
    private readonly object _syncRoot = new();
    private TaskCompletionSource _resumeGate = CreateGate(completed: true);
    private bool _isPaused;
    private bool _resetRequested;

    public bool IsPaused
    {
        get
        {
            lock (_syncRoot)
            {
                return _isPaused;
            }
        }
    }

    public void BeginRun()
    {
        lock (_syncRoot)
        {
            _isPaused = false;
            _resetRequested = false;
            _resumeGate = CreateGate(completed: true);
        }
    }

    public void EndRun()
    {
        Resume();
        lock (_syncRoot)
        {
            _resetRequested = false;
        }
    }

    public void Pause()
    {
        lock (_syncRoot)
        {
            if (_isPaused)
            {
                return;
            }

            _isPaused = true;
            _resumeGate = CreateGate(completed: false);
        }
    }

    public void Resume()
    {
        TaskCompletionSource? gate;
        lock (_syncRoot)
        {
            _isPaused = false;
            gate = _resumeGate;
        }

        gate.TrySetResult();
    }

    public void RequestReset()
    {
        TaskCompletionSource? gate;
        lock (_syncRoot)
        {
            _resetRequested = true;
            _isPaused = false;
            gate = _resumeGate;
        }

        gate.TrySetResult();
    }

    public async Task WaitIfPausedOrResetAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task waitTask;
            lock (_syncRoot)
            {
                if (_resetRequested)
                {
                    _resetRequested = false;
                    throw new InspectionRunResetException();
                }

                if (!_isPaused)
                {
                    return;
                }

                waitTask = _resumeGate.Task;
            }

            await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource CreateGate(bool completed)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
        {
            gate.SetResult();
        }

        return gate;
    }
}
