namespace VisionStation.Application.Presentation;

public sealed record UnsavedChangeItem(
    string Key,
    string Title,
    string Detail);

public interface IUnsavedChangesService
{
    event EventHandler? Changed;

    bool HasUnsavedChanges { get; }

    IReadOnlyList<UnsavedChangeItem> GetUnsavedChanges();

    void SetUnsaved(
        string key,
        string title,
        bool hasUnsavedChanges,
        Func<CancellationToken, Task>? saveAsync = null,
        string? detail = null);

    void Clear(string key);

    Task SaveAllAsync(CancellationToken cancellationToken = default);
}

public sealed class UnsavedChangesService : IUnsavedChangesService
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<string, UnsavedChangeRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? Changed;

    public bool HasUnsavedChanges
    {
        get
        {
            lock (_syncRoot)
            {
                return _registrations.Count > 0;
            }
        }
    }

    public IReadOnlyList<UnsavedChangeItem> GetUnsavedChanges()
    {
        lock (_syncRoot)
        {
            return _registrations.Values
                .Select(registration => registration.Item)
                .OrderBy(item => item.Title, StringComparer.CurrentCulture)
                .ToArray();
        }
    }

    public void SetUnsaved(
        string key,
        string title,
        bool hasUnsavedChanges,
        Func<CancellationToken, Task>? saveAsync = null,
        string? detail = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Unsaved change key is required.", nameof(key));
        }

        if (!hasUnsavedChanges)
        {
            Clear(key);
            return;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Unsaved change title is required.", nameof(title));
        }

        lock (_syncRoot)
        {
            _registrations[key.Trim()] = new UnsavedChangeRegistration(
                new UnsavedChangeItem(key.Trim(), title.Trim(), detail?.Trim() ?? string.Empty),
                saveAsync ?? (_ => Task.CompletedTask));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var removed = false;
        lock (_syncRoot)
        {
            removed = _registrations.Remove(key.Trim());
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SaveAllAsync(CancellationToken cancellationToken = default)
    {
        UnsavedChangeRegistration[] registrations;
        lock (_syncRoot)
        {
            registrations = _registrations.Values.ToArray();
        }

        foreach (var registration in registrations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await registration.SaveAsync(cancellationToken).ConfigureAwait(false);
            Clear(registration.Item.Key);
        }
    }

    private sealed record UnsavedChangeRegistration(
        UnsavedChangeItem Item,
        Func<CancellationToken, Task> SaveAsync);
}
