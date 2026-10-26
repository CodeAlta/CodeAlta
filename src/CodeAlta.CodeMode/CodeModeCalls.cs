namespace CodeAlta.CodeMode;

/// <summary>A place in the source of a script, 1-based.</summary>
/// <param name="Line">The line, starting at 1.</param>
/// <param name="Column">The column, starting at 1.</param>
public readonly record struct CodeModeSourceLocation(int Line, int Column);

/// <summary>One call a script makes to its host.</summary>
/// <param name="Index">The order of the call in its run, starting at 0.</param>
/// <param name="Path">The path of the function, such as <c>tools.read_file</c>.</param>
/// <param name="ArgumentsJson">The argument object as JSON.</param>
/// <param name="StepId">The step the call belongs to, or null.</param>
/// <param name="Location">Where the script made the call, when the engine could tell.</param>
public sealed record CodeModeCall(int Index, string Path, string ArgumentsJson, string? StepId, CodeModeSourceLocation? Location);

/// <summary>An error a call gives back to the script, which throws it as an <c>Error</c> it can catch.</summary>
/// <param name="Name">The name of the error in JavaScript, such as <c>ToolError</c>, <c>AltaError</c> or <c>TypeError</c>.</param>
/// <param name="Message">The message.</param>
public sealed record CodeModeCallError(string Name, string Message)
{
    /// <summary>Gets a stable code, such as <c>session.notFound</c>.</summary>
    public string? Code { get; init; }

    /// <summary>Gets more data as JSON, given to the script as <c>error.data</c>.</summary>
    public string? DataJson { get; init; }
}

/// <summary>The outcome of one call: a JSON value, or an error.</summary>
public sealed record CodeModeCallResult
{
    private CodeModeCallResult(string? valueJson, CodeModeCallError? error)
    {
        ValueJson = valueJson;
        Error = error;
    }

    /// <summary>Gets the value as JSON when the call succeeded; <c>null</c> stands for JavaScript <c>undefined</c>.</summary>
    public string? ValueJson { get; }

    /// <summary>Gets the error when the call failed.</summary>
    public CodeModeCallError? Error { get; }

    /// <summary>Gets whether the call succeeded.</summary>
    public bool Succeeded => Error is null;

    /// <summary>Creates a successful result.</summary>
    /// <param name="valueJson">The value as JSON, or null for <c>undefined</c>.</param>
    /// <returns>The result.</returns>
    public static CodeModeCallResult Success(string? valueJson) => new(valueJson, null);

    /// <summary>Creates a successful result whose value is a string.</summary>
    /// <param name="text">The string.</param>
    /// <returns>The result.</returns>
    public static CodeModeCallResult Text(string text) => new(System.Text.Json.JsonSerializer.Serialize(text, CodeModeJsonContext.Default.String), null);

    /// <summary>Creates a failed result.</summary>
    /// <param name="error">The error the script receives.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    public static CodeModeCallResult Failure(CodeModeCallError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(null, error);
    }
}

/// <summary>What runs the calls of a script: the tools, <c>alta</c> and the other bindings of its session.</summary>
public interface ICodeModeCallHandler
{
    /// <summary>Runs one call. An exception becomes an error the script can catch; it never ends the run.</summary>
    /// <param name="call">The call.</param>
    /// <param name="cancellationToken">Cancelled when the run ends or is stopped.</param>
    /// <returns>The outcome of the call.</returns>
    ValueTask<CodeModeCallResult> InvokeAsync(CodeModeCall call, CancellationToken cancellationToken);
}

/// <summary>Follows a run while it goes: calls, steps and output. Every method is called outside the engine's lock and must return quickly.</summary>
public interface ICodeModeRunObserver
{
    /// <summary>A call started.</summary>
    /// <param name="call">The call.</param>
    void CallStarted(CodeModeCall call) { }

    /// <summary>A call ended.</summary>
    /// <param name="record">What the call did.</param>
    void CallEnded(CodeModeCallRecord record) { }

    /// <summary>A step started.</summary>
    /// <param name="stepId">The step.</param>
    void StepStarted(string stepId) { }

    /// <summary>A step ended.</summary>
    /// <param name="record">What the step did.</param>
    void StepEnded(CodeModeStepRecord record) { }

    /// <summary>The script wrote output.</summary>
    /// <param name="entry">The output.</param>
    void Output(CodeModeOutputEntry entry) { }
}
