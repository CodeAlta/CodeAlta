namespace CodeAlta.CodeMode;

/// <summary>The options of one run.</summary>
public sealed record CodeModeRunOptions
{
    /// <summary>Gets the limits of the run.</summary>
    public CodeModeLimits Limits { get; init; } = CodeModeLimits.Default;

    /// <summary>Gets the arguments of the script as a JSON object, given to it as <c>args</c>.</summary>
    public string? ArgumentsJson { get; init; }

    /// <summary>Gets the clock behind <c>Date.now()</c> and <c>new Date()</c>, or null for the real one. A run that is replayed gives the times it recorded.</summary>
    public Func<DateTimeOffset>? Clock { get; init; }

    /// <summary>Gets the source of <c>Math.random()</c>, or null for a random generator of the run.</summary>
    public Func<double>? Random { get; init; }

    /// <summary>
    /// Gets what finds the script of <c>workflow(name | { scriptPath }, args)</c>, or null when a run cannot start another
    /// script. It returns the prepared source; an exception becomes an error the script can catch.
    /// </summary>
    public Func<CodeModeScriptReference, CancellationToken, ValueTask<CodeModeSource>>? ResolveScript { get; init; }

    /// <summary>
    /// Gets the steps the run replays from a previous run of the script, or null to run every step. The steps of the
    /// scripts that <c>workflow()</c> starts are replayed too: their keys start with the path of those scripts, each with the number of its call (<c>lib#0/read#0</c>).
    /// </summary>
    public CodeModeReplay? Replay { get; init; }

    /// <summary>Gets how deep scripts may start scripts: a nested run of this depth cannot start another one.</summary>
    public int MaxWorkflowDepth { get; init; } = 3;

    /// <summary>Gets the depth of this run: 0 for the script <c>exec</c> runs.</summary>
    internal int Depth { get; init; }

    /// <summary>Gets the scope of the calls of this run (<see cref="CodeModeCall.Scope"/>).</summary>
    internal int Scope { get; init; }

    /// <summary>Gets what numbers the scripts <c>workflow()</c> starts in the run <c>exec</c> runs, shared by them all.</summary>
    internal System.Runtime.CompilerServices.StrongBox<int>? Scopes { get; init; }

    /// <summary>Gets the step of the <c>workflow()</c> call that started this run: the parent of its steps, and of its calls made outside them.</summary>
    internal CodeModeRun.ActiveStep? OuterStep { get; init; }

    /// <summary>Gets the run whose <c>workflow()</c> call started this run: its time limit stops too while this one waits for the user.</summary>
    internal CodeModeRun? ParentRun { get; init; }

    /// <summary>Gets the steps of the run <c>exec</c> runs and of the scripts it starts, shared by them all; null for a run that starts them.</summary>
    internal CodeModeStepSpace? Steps { get; init; }

    /// <summary>Gets the path of the scripts that start this run, before the ids of its steps: empty for the run <c>exec</c> runs, <c>lib#0/</c> below.</summary>
    internal string StepPrefix { get; init; } = string.Empty;
}

/// <summary>The script that <c>workflow()</c> names: a script of the library by its name, or a file by its path.</summary>
/// <param name="Name">The name of a library script, or null.</param>
/// <param name="ScriptPath">The path of a script file, or null.</param>
public sealed record CodeModeScriptReference(string? Name, string? ScriptPath);

/// <summary>
/// Runs the scripts of one session: it holds the API of the session's tools and the cache of prepared scripts, and
/// gives each run an engine of its own. Runs share nothing that changes, so the scripts of every session, and
/// several scripts of one session, run at the same time.
/// </summary>
public sealed class CodeModeHost
{
    private readonly CodeModeScriptCache _cache;

    /// <summary>Creates a host.</summary>
    /// <param name="api">The functions the scripts can call.</param>
    /// <param name="cache">The cache of prepared scripts, shared by the hosts of a process.</param>
    /// <exception cref="ArgumentNullException"><paramref name="api"/> or <paramref name="cache"/> is null.</exception>
    public CodeModeHost(CodeModeApi api, CodeModeScriptCache cache)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(cache);
        Api = api;
        _cache = cache;
    }

    /// <summary>Gets the functions the scripts can call.</summary>
    public CodeModeApi Api { get; }

    /// <summary>
    /// Runs a script to its end. It never throws for what the script does: a failure, a broken limit or a stop is a
    /// result. The run starts on the thread pool, so the caller's thread never runs the script.
    /// </summary>
    /// <param name="source">The prepared source.</param>
    /// <param name="handler">What runs the calls of the script.</param>
    /// <param name="options">The options of the run.</param>
    /// <param name="observer">What follows the run, or null.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>What the run did.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="handler"/> is null.</exception>
    public Task<CodeModeRunResult> RunAsync(
        CodeModeSource source,
        ICodeModeCallHandler handler,
        CodeModeRunOptions? options = null,
        ICodeModeRunObserver? observer = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handler);
        if (source.Error is { } error)
        {
            return Task.FromResult(new CodeModeRunResult(CodeModeRunStatus.Failed, TimeSpan.Zero) { Error = error, Source = source });
        }

        var run = new CodeModeRun(Api, _cache, source, handler, options ?? new CodeModeRunOptions(), observer);
        return Task.Run(() => run.ExecuteAsync(cancellationToken), CancellationToken.None);
    }
}
