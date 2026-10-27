namespace CodeAlta.CodeMode;

/// <summary>
/// The limits of one run of a script. They protect the process (memory, stack, CPU) and bound what a run may ask
/// of its host; the rights of the calls themselves are those of the session that runs the script.
/// </summary>
public sealed record CodeModeLimits
{
    /// <summary>Gets the limits used when a host gives none.</summary>
    public static CodeModeLimits Default { get; } = new();

    /// <summary>Gets the memory the script may allocate during one synchronous slice, in bytes.</summary>
    public long MemoryBytes { get; init; } = 256_000_000;

    /// <summary>Gets the statements the script may execute during one synchronous slice.</summary>
    public int MaxStatements { get; init; } = 50_000_000;

    /// <summary>Gets the CPU time of one synchronous slice; time spent awaiting host calls does not count.</summary>
    public TimeSpan CpuTimePerSlice { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets the depth of nested JavaScript calls.</summary>
    public int MaxRecursionDepth { get; init; } = 256;

    /// <summary>Gets the time one regular expression may take to match.</summary>
    public TimeSpan RegexTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Gets the largest array the script may create.</summary>
    public uint MaxArraySize { get; init; } = 1_000_000;

    /// <summary>
    /// Gets the calls to the host one run may make: no limit by default, since a run that researches for days makes many,
    /// and the user and other sessions can stop a run.
    /// </summary>
    public int MaxCalls { get; init; } = int.MaxValue;

    /// <summary>
    /// Gets the calls to the host that run at the same time; the next ones wait for a slot, none fails. A session a script
    /// started (<c>agent()</c>) holds its slot while it works, so this leaves room beside the sessions at work.
    /// </summary>
    public int MaxCallsInFlight { get; init; } = 32;

    /// <summary>Gets the size of the log of a run (console output and <c>text()</c>), in characters.</summary>
    public int MaxLogCharacters { get; init; } = 64 * 1024;

    /// <summary>
    /// Gets the wall time of a run, awaited time included: none by default (<see cref="System.Threading.Timeout.InfiniteTimeSpan"/>),
    /// since a run may research for hours or sleep for days, and awaiting costs no CPU. A script sets one with <c>timeout_ms</c>.
    /// </summary>
    public TimeSpan Timeout { get; init; } = System.Threading.Timeout.InfiniteTimeSpan;

    /// <summary>
    /// Gets how long a script may wait with no call in flight and no JavaScript running before the run fails as
    /// stalled: nothing could ever resume it.
    /// </summary>
    public TimeSpan StallDelay { get; init; } = TimeSpan.FromMilliseconds(250);
}
