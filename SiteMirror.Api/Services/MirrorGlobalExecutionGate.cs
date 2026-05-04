namespace SiteMirror.Api.Services;

/// <summary>
/// Ensures only one <see cref="MirrorService.MirrorAsync"/> runs at a time per API process.
/// Parallel crawls (queue workers + direct API calls) otherwise contend on the same output files on Windows.
/// </summary>
public sealed class MirrorGlobalExecutionGate
{
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public Task WaitAsync(CancellationToken cancellationToken) => _mutex.WaitAsync(cancellationToken);

    public void Release() => _mutex.Release();
}
