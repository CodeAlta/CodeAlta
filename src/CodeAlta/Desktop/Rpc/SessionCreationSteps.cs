using System.Diagnostics;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The steps of one creation of a session, as the page shows them while it waits: the worktree, its files, the
/// session, then ready. The creation writes them as it goes; the page reads them by the identifier of its request.
/// </summary>
internal sealed class SessionCreationSteps
{
    /// <summary>Git adds the worktree; its detail is the branch.</summary>
    public const string Worktree = "worktree";
    /// <summary>Git checks the files of the worktree out; its detail is the last progress line of git.</summary>
    public const string Checkout = "checkout";
    /// <summary>The session is created and attached.</summary>
    public const string Session = "session";
    /// <summary>The session exists.</summary>
    public const string Ready = "ready";

    private readonly Lock _gate = new();
    private readonly List<Step> _steps = [];

    /// <summary>Starts a step: the one that runs is done.</summary>
    /// <param name="kind">The step.</param>
    /// <param name="detail">What it is about, such as the branch of a worktree.</param>
    public void Start(string kind, string? detail = null)
    {
        lock (_gate)
        {
            End("done");
            _steps.Add(new Step(kind, detail, Stopwatch.GetTimestamp()));
        }
    }

    /// <summary>Changes what the step that runs says: a progress line of git.</summary>
    /// <param name="detail">The new detail.</param>
    public void Detail(string detail)
    {
        lock (_gate)
        {
            if (_steps.Count > 0 && _steps[^1].State == "running") _steps[^1].Detail = detail;
        }
    }

    /// <summary>Ends the step that runs as failed, with what went wrong.</summary>
    /// <param name="message">What git or the host said; null when nothing can be said.</param>
    public void Fail(string? message)
    {
        lock (_gate)
        {
            if (_steps.Count == 0 || _steps[^1].State != "running") return;
            _steps[^1].Message = message;
            End("failed");
        }
    }

    /// <summary>Ends the step that runs as done, and says that the session is ready.</summary>
    public void Finish()
    {
        lock (_gate)
        {
            End("done");
            var now = Stopwatch.GetTimestamp();
            _steps.Add(new Step(Ready, null, now) { State = "done", Ended = now });
        }
    }

    /// <summary>Reads the steps as they are now: a step that runs says how long it has run so far.</summary>
    /// <returns>The steps, in their order.</returns>
    public WorkspaceCreationStep[] Read()
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            return [.. _steps.Select(step => new WorkspaceCreationStep(step.Kind, step.State, step.Detail,
                (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(step.Started, step.Ended ?? now).TotalMilliseconds), step.Message))];
        }
    }

    // Called under _gate.
    private void End(string state)
    {
        if (_steps.Count == 0 || _steps[^1].State != "running") return;
        _steps[^1].State = state;
        _steps[^1].Ended = Stopwatch.GetTimestamp();
    }

    private sealed class Step(string kind, string? detail, long started)
    {
        public string Kind { get; } = kind;
        public string? Detail { get; set; } = detail;
        public long Started { get; } = started;
        public long? Ended { get; set; }
        public string State { get; set; } = "running";
        public string? Message { get; set; }
    }
}

/// <summary>A step of the creation of a session.</summary>
/// <param name="Kind"><c>worktree</c>, <c>checkout</c>, <c>session</c> or <c>ready</c>.</param>
/// <param name="State"><c>running</c>, <c>done</c> or <c>failed</c>.</param>
/// <param name="Detail">The branch of the worktree, or the last progress line of git while it checks the files out.</param>
/// <param name="ElapsedMilliseconds">How long the step ran, or has run so far.</param>
/// <param name="Message">With <c>failed</c>: what git or the host said.</param>
internal sealed record WorkspaceCreationStep(string Kind, string State, string? Detail, int ElapsedMilliseconds, string? Message);

/// <param name="Status"><c>ok</c>, <c>unknown</c> (no creation with that identifier), or a refusal of the request.</param>
/// <param name="HostEpoch">The host that answers.</param>
/// <param name="Steps">The steps so far, in their order.</param>
internal sealed record WorkspaceCreateSessionProgressResponse(string Status, string? HostEpoch, IReadOnlyList<WorkspaceCreationStep> Steps);
