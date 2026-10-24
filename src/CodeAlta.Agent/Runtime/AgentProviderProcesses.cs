namespace CodeAlta.Agent.Runtime;

/// <summary>
/// The processes the provider runtimes of a registry started and still run (a CLI per session), each with a stamp of
/// when it started, so that a process that later gets the same identifier is not taken for one of them.
/// </summary>
/// <remarks>
/// The set belongs to the registry, not to a runtime: a provider registered again (its settings changed) gets a new
/// runtime, while the sessions that were working keep the runtime they had and its processes. The new runtime must
/// still know those processes as CodeAlta's own.
/// </remarks>
public sealed class AgentProviderProcesses
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Registration> _processes = [];

    /// <summary>
    /// Records a process a runtime started, until the returned registration is disposed (when the runtime closes it).
    /// </summary>
    /// <param name="pid">The identifier of the process.</param>
    /// <param name="start">A stamp of when it started, in a form of the provider's choosing; null when it could not be read.</param>
    /// <returns>The registration.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pid"/> is not positive.</exception>
    public IDisposable Track(int pid, string? start)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pid);
        var registration = new Registration(this, pid, start);
        lock (_gate)
        {
            _processes[pid] = registration;
        }

        return registration;
    }

    /// <summary>
    /// Tells whether a process is one a runtime started: its identifier is recorded, with the same start when both
    /// stamps are known.
    /// </summary>
    /// <param name="pid">The identifier of the process.</param>
    /// <param name="start">The stamp of the start of the process that has the identifier now; null when it could not be read.</param>
    public bool Contains(int pid, string? start)
    {
        Registration? registration;
        lock (_gate)
        {
            _processes.TryGetValue(pid, out registration);
        }

        return registration is not null && (registration.Start is null || start is null || string.Equals(registration.Start, start, StringComparison.Ordinal));
    }

    /// <summary>Gets the identifiers of the processes recorded.</summary>
    public IReadOnlyList<int> Snapshot()
    {
        lock (_gate)
        {
            return [.. _processes.Keys];
        }
    }

    private void Remove(Registration registration)
    {
        lock (_gate)
        {
            // Another process may have been recorded with the same identifier since.
            if (_processes.TryGetValue(registration.Pid, out var current) && ReferenceEquals(current, registration))
            {
                _processes.Remove(registration.Pid);
            }
        }
    }

    private sealed class Registration(AgentProviderProcesses owner, int pid, string? start) : IDisposable
    {
        private int _disposed;

        public int Pid { get; } = pid;

        public string? Start { get; } = start;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Remove(this);
            }
        }
    }
}
