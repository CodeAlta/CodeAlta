namespace CodeAlta.CodeMode;

/// <summary>How a run ended.</summary>
public enum CodeModeRunStatus
{
    /// <summary>The script returned.</summary>
    Completed,

    /// <summary>The script threw, did not parse, or broke a limit.</summary>
    Failed,

    /// <summary>The run was stopped.</summary>
    Cancelled,
}

/// <summary>Why a run failed.</summary>
public enum CodeModeErrorKind
{
    /// <summary>The source does not parse, or uses what scripts cannot use (TypeScript, <c>import</c>).</summary>
    Syntax,

    /// <summary>The script threw or rejected.</summary>
    Script,

    /// <summary>A synchronous slice ran longer than its CPU time.</summary>
    CpuTime,

    /// <summary>A synchronous slice executed too many statements.</summary>
    Statements,

    /// <summary>A synchronous slice allocated too much memory.</summary>
    Memory,

    /// <summary>The script recursed too deeply.</summary>
    Recursion,

    /// <summary>A regular expression took too long.</summary>
    Regex,

    /// <summary>The run reached its wall time.</summary>
    Timeout,

    /// <summary>The script waits for something that nothing can resolve: no call is in flight.</summary>
    Stalled,

    /// <summary>The run was stopped.</summary>
    Cancelled,
}

/// <summary>An error of a run, with where it happened.</summary>
/// <param name="Kind">Why the run failed.</param>
/// <param name="Name">The name of the JavaScript error (<c>TypeError</c>), or of the limit.</param>
/// <param name="Message">The message.</param>
public sealed record CodeModeRunError(CodeModeErrorKind Kind, string Name, string Message)
{
    /// <summary>Gets where the error happened in the source of the script.</summary>
    public CodeModeSourceLocation? Location { get; init; }

    /// <summary>Gets the JavaScript stack, when there is one.</summary>
    public string? Stack { get; init; }
}

/// <summary>How a call ended.</summary>
public enum CodeModeCallStatus
{
    /// <summary>The call succeeded.</summary>
    Succeeded,

    /// <summary>The call failed; the script received an error.</summary>
    Failed,

    /// <summary>The call was still running when the script ended, or when the run was stopped.</summary>
    Cancelled,

    /// <summary>The run had made all the calls it may make.</summary>
    Refused,
}

/// <summary>What one call did.</summary>
/// <param name="Index">The order of the call in its run.</param>
/// <param name="Path">The path of the function.</param>
/// <param name="StepId">The step of the call, or null.</param>
/// <param name="Location">Where the script made the call.</param>
/// <param name="StartedAt">When the call started, from the start of the run.</param>
/// <param name="Duration">How long it took.</param>
/// <param name="Status">How it ended.</param>
public sealed record CodeModeCallRecord(int Index, string Path, string? StepId, CodeModeSourceLocation? Location, TimeSpan StartedAt, TimeSpan Duration, CodeModeCallStatus Status)
{
    /// <summary>Gets the error of a failed call.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Gets how long the call waited for a slot before it ran.</summary>
    public TimeSpan Queued { get; init; }
}

/// <summary>What one step did.</summary>
/// <param name="StepId">The step.</param>
/// <param name="StartedAt">When it started, from the start of the run.</param>
/// <param name="Duration">How long it took.</param>
/// <param name="Succeeded">Whether its function resolved.</param>
public sealed record CodeModeStepRecord(string StepId, TimeSpan StartedAt, TimeSpan Duration, bool Succeeded)
{
    /// <summary>Gets the error of a failed step.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>The kind of an output entry.</summary>
public enum CodeModeOutputKind
{
    /// <summary><c>text()</c>: output meant for the model.</summary>
    Text,

    /// <summary><c>console.log</c> and <c>console.info</c>, or <c>log()</c>.</summary>
    Log,

    /// <summary><c>console.warn</c>.</summary>
    Warning,

    /// <summary><c>console.error</c>.</summary>
    Error,
}

/// <summary>One piece of output of a script.</summary>
/// <param name="Kind">Its kind.</param>
/// <param name="Text">Its text.</param>
/// <param name="StepId">The step that wrote it, or null.</param>
public sealed record CodeModeOutputEntry(CodeModeOutputKind Kind, string Text, string? StepId);

/// <summary>What a run did.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Duration">Its wall time.</param>
public sealed record CodeModeRunResult(CodeModeRunStatus Status, TimeSpan Duration)
{
    /// <summary>Gets the value the script returned, as JSON; null when it returned nothing.</summary>
    public string? ValueJson { get; init; }

    /// <summary>Gets the error of a failed or cancelled run.</summary>
    public CodeModeRunError? Error { get; init; }

    /// <summary>Gets the calls the script made, in their order.</summary>
    public IReadOnlyList<CodeModeCallRecord> Calls { get; init; } = [];

    /// <summary>Gets the steps the script ran, in the order they ended.</summary>
    public IReadOnlyList<CodeModeStepRecord> Steps { get; init; } = [];

    /// <summary>Gets the output of the script, in its order.</summary>
    public IReadOnlyList<CodeModeOutputEntry> Output { get; init; } = [];

    /// <summary>Gets how many characters of output were dropped beyond the log limit.</summary>
    public int DroppedOutputCharacters { get; init; }

    /// <summary>Gets the most calls that ran at the same time.</summary>
    public int MaxCallsAtOnce { get; init; }

    /// <summary>Gets the time the engine spent running the script's own code.</summary>
    public TimeSpan ScriptTime { get; init; }

    /// <summary>Gets the source that ran, after <see cref="CodeModeSource"/> prepared it.</summary>
    public CodeModeSource? Source { get; init; }
}
