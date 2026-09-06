namespace CodeAlta.Orchestration.Runtime.Prompts;

/// <summary>Knowledge of admission through the ask response's normal-submit route, not provider success or durability.</summary>
public enum SessionPromptResponseAdmission
{
    /// <summary>No runtime invocation was made by this route; plugin side effects are not covered.</summary>
    DefinitelyNotAdmittedByThisRoute,
    /// <summary>A recognized submitted result supplies positive late evidence of admission.</summary>
    Admitted,
    /// <summary>Runtime invocation began but its admission cannot be established. Do not offer replay.</summary>
    Indeterminate,
}

/// <summary>Immutable admission evidence for a single ask-response attempt.</summary>
public sealed record SessionPromptResponseResult
{
    private SessionPromptResponseResult(SessionPromptResponseAdmission admission, string? runId, string? diagnostic)
        => (Admission, RunId, Diagnostic) = (admission, runId, diagnostic);

    /// <summary>Gets what is known about admission through this route.</summary>
    public SessionPromptResponseAdmission Admission { get; }
    /// <summary>Gets the runtime run identifier supporting positive late evidence, when available.</summary>
    public string? RunId { get; }
    /// <summary>Gets an optional scalar diagnostic; feedback failure never revokes positive evidence.</summary>
    public string? Diagnostic { get; }

    /// <summary>Creates definite non-admission by this route, without guaranteeing absence of plugin side effects.</summary>
    public static SessionPromptResponseResult NotAdmitted(string? diagnostic = null)
        => new(SessionPromptResponseAdmission.DefinitelyNotAdmittedByThisRoute, null, diagnostic);

    /// <summary>Creates an unresolved result which must not authorize retry.</summary>
    public static SessionPromptResponseResult Indeterminate(string? diagnostic = null)
        => new(SessionPromptResponseAdmission.Indeterminate, null, diagnostic);

    /// <summary>Creates positive admission evidence supplied by the trusted dispatch adapter.</summary>
    /// <exception cref="ArgumentException">The run identifier is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">The run identifier is null.</exception>
    public static SessionPromptResponseResult Admitted(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return new(SessionPromptResponseAdmission.Admitted, runId, null);
    }

    /// <summary>Appends scalar feedback without changing admission evidence.</summary>
    /// <exception cref="ArgumentNullException">The diagnostic is null.</exception>
    public SessionPromptResponseResult WithDiagnostic(string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new(Admission, RunId, Diagnostic is null ? diagnostic : Diagnostic + "\n" + diagnostic);
    }
}

/// <summary>
/// Classifies one normal ask-response submission, preserving positive late evidence across later projection failures.
/// This is not an early runtime receipt, execution-task owner, or queue/steer policy.
/// </summary>
public sealed class SessionPromptResponseDispatch
{
    private SessionPromptResponseResult _result = SessionPromptResponseResult.NotAdmitted();
    private bool _invoked;

    private SessionPromptResponseDispatch() { }

    /// <summary>Runs preparation, invocation and feedback, retaining the last established admission evidence on failure.</summary>
    /// <remarks>The callback owns its UI affinity. Only this method's result processing continues off that context.</remarks>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    public static async Task<SessionPromptResponseResult> RunAsync(Func<SessionPromptResponseDispatch, Task> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        var attempt = new SessionPromptResponseDispatch();
        try
        {
            await dispatch(attempt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            attempt._result = attempt._result.WithDiagnostic(ex.Message);
        }
        return attempt._result;
    }

    /// <summary>Invokes the backend once, latching uncertainty before entry and positive evidence before returning to presentation.</summary>
    /// <remarks>Only Submitted with a nonblank run ID is positive for this normal-submit route. All other backend outcomes remain uncertain.</remarks>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    /// <exception cref="InvalidOperationException">This attempt has already invoked the backend.</exception>
    public async ValueTask<SessionPromptResponseResult> InvokeAsync(Func<ValueTask<SessionCommandResult>> dispatch)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        if (_invoked)
        {
            throw new InvalidOperationException("The ask response has already invoked the runtime.");
        }
        _invoked = true;
        _result = SessionPromptResponseResult.Indeterminate();
        var result = await dispatch().ConfigureAwait(false);
        if (result is { Outcome: SessionCommandOutcomeKind.Submitted } && !string.IsNullOrWhiteSpace(result.RunId))
        {
            _result = SessionPromptResponseResult.Admitted(result.RunId);
        }
        return _result;
    }
}
