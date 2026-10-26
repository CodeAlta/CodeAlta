using Microsoft.Mxc.Sdk.V1;

namespace CodeAlta.Sandbox.Mxc;

/// <summary>A program of an mxc container.</summary>
internal sealed class MxcSandboxProcess : ISandboxProcess
{
    private readonly MxcProcess _process;
    private int _disposed;

    public MxcSandboxProcess(MxcProcess process)
    {
        _process = process;
        Id = (int)process.Id;
        StandardInput = process.StandardInput ?? Stream.Null;
        StandardOutput = process.StandardOutput ?? Stream.Null;
        StandardError = process.StandardError ?? Stream.Null;
    }

    public int Id { get; }

    public Stream StandardInput { get; }

    public Stream StandardOutput { get; }

    public Stream StandardError { get; }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        try
        {
            return (await _process.WaitAsync(cancellationToken).ConfigureAwait(false)).ExitCode;
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            // Ended by its owner, which does not read its code: a wait it did not observe must not fail later.
            return -1;
        }
    }

    public void Kill()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            _process.Kill();
        }
        catch (Exception exception) when (exception is MxcException or ObjectDisposedException or InvalidOperationException)
        {
            // Already gone.
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        try
        {
            _process.Kill();
        }
        catch (Exception exception) when (exception is MxcException or ObjectDisposedException or InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
