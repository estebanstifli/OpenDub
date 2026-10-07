namespace Dubbing.Infrastructure.Processes;

// A shared lease serializes accelerator work and releases the previous runtime's residency.
public sealed class RuntimeCoordinator : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? active;
    private Action? stopActive;
    private Func<Task>? releaseActive;
    public async Task<T> RunAsync<T>(string owner, Action stop, Func<Task<T>> operation, CancellationToken ct)
        => await RunAsync(owner, () => { stop(); return Task.CompletedTask; }, stop, operation, ct);
    public async Task<T> RunAsync<T>(string owner, Func<Task> release, Action abort, Func<Task<T>> operation, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (active != owner)
            {
                if (active is not null) ActivityJournal.Write($"Releasing {active} before starting {owner}.");
                await ReleaseCoreAsync(); active = owner; stopActive = abort; releaseActive = release;
            }
            try { return await operation(); }
            catch (OperationCanceledException) { await ReleaseCoreAsync(); throw; }
        }
        finally { gate.Release(); }
    }
    public async Task ReleaseAsync()
    {
        await gate.WaitAsync();
        try { await ReleaseCoreAsync(); }
        finally { gate.Release(); }
    }
    private async Task ReleaseCoreAsync()
    {
        if (releaseActive is null) return;
        await releaseActive();
        ActivityJournal.Write($"{active} · model memory released.");
        active = null; stopActive = null; releaseActive = null;
    }
    public void Dispose() { stopActive?.Invoke(); gate.Dispose(); }
}
