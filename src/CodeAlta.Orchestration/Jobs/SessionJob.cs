namespace CodeAlta.Orchestration.Jobs;

/// <summary>How far a background job of a session is.</summary>
public enum SessionJobState
{
    /// <summary>Its command runs.</summary>
    Running,

    /// <summary>Its command ended with the exit code 0.</summary>
    Succeeded,

    /// <summary>Its command ended with another exit code.</summary>
    Failed,

    /// <summary>Its command was ended before it finished.</summary>
    Cancelled,

    /// <summary>Its command was ended by the host because the time it was given ran out.</summary>
    TimedOut,
}

/// <summary>When the end of a background job is told to its session with a prompt.</summary>
public enum SessionJobNotification
{
    /// <summary>
    /// Whenever the command ends by itself, whatever its exit code, when its time ran out, and when the user stops
    /// it: the session decides what to do with the result. A job its own session cancels says nothing.
    /// </summary>
    Always,

    /// <summary>Only when the command succeeded: a command that fails, runs out of time or is cancelled says nothing.</summary>
    Success,

    /// <summary>Never: the session reads the job when it wants to.</summary>
    Never,
}
/// <summary>What a session asks a background job for.</summary>
public sealed record SessionJobRequest
{
    /// <summary>Gets the session the job belongs to, which is told its end.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the project of that session, when it has one.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the command line, as a shell tool would take it.</summary>
    public required string Command { get; init; }

    /// <summary>Gets the folder the command runs in: a full path.</summary>
    public required string Folder { get; init; }

    /// <summary>Gets what the job is for, in a few words; null to show the command.</summary>
    public string? Title { get; init; }

    /// <summary>Gets when the end of the job is told to the session.</summary>
    public SessionJobNotification Notification { get; init; }

    /// <summary>Gets how long the command may run before the host ends it; null to let it run as long as it takes.</summary>
    public TimeSpan? Timeout { get; init; }
}

/// <summary>What is known of a background job at one moment.</summary>
public sealed record SessionJob
{
    /// <summary>Gets the identity of the job, which always starts with <see cref="SessionJobService.IdPrefix"/>.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the session the job belongs to.</summary>
    public required string SessionId { get; init; }

    /// <summary>Gets the project of that session, when it has one.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the command line.</summary>
    public required string Command { get; init; }

    /// <summary>Gets the folder the command runs in.</summary>
    public required string Folder { get; init; }

    /// <summary>Gets what the job is for, when its session said it.</summary>
    public string? Title { get; init; }

    /// <summary>Gets when the end of the job is told to the session.</summary>
    public SessionJobNotification Notification { get; init; }

    /// <summary>Gets how long the command may run before the host ends it; null when it has no limit.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Gets how far the job is.</summary>
    public SessionJobState State { get; init; }

    /// <summary>Gets when the command was started.</summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Gets when the command ended; null while it runs.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Gets the exit code of the command; null while it runs, and when the command was ended (cancelled, out of time).</summary>
    public int? ExitCode { get; init; }

    /// <summary>Gets the identifier of the process of the shell, when the host knows it.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Gets how much the command wrote so far, in UTF-16 code units.</summary>
    public long OutputCharacters { get; init; }

    /// <summary>
    /// Gets what became of the prompt that tells the end of the job: null while nothing was decided, <c>none</c>
    /// when no prompt is due, <c>steered</c> when it was given to the turn that ran, <c>queued</c> when it starts
    /// the next turn, <c>failed</c> when the session could not be given it.
    /// </summary>
    public string? Delivery { get; init; }

    /// <summary>Gets what the session and the user see the job as: its title, or its command on one line.</summary>
    public string Label => string.IsNullOrWhiteSpace(Title) ? SessionJobService.OneLine(Command, 160) : Title!;
}

/// <summary>The outcome of asking for a background job.</summary>
/// <param name="Status">
/// <c>ok</c>; <c>no_folder</c> when the folder does not exist; <c>limit</c> when too many jobs run already;
/// <c>failed</c> when the shell could not be started; <c>closed</c> while the host exits.
/// </param>
/// <param name="Job">The job, with <c>ok</c>.</param>
/// <param name="Message">What went wrong, with <c>failed</c>.</param>
public sealed record SessionJobStart(string Status, SessionJob? Job, string? Message = null);

/// <summary>What a background job wrote, as far as it is kept.</summary>
/// <param name="Job">The job as it was when its output was read.</param>
/// <param name="Text">The newest part of what it wrote.</param>
/// <param name="Start">The position of the first unit of <paramref name="Text"/> in everything the job wrote, in UTF-16 code units.</param>
/// <param name="Truncated">Whether the job wrote something before <paramref name="Text"/> that is not in it.</param>
public sealed record SessionJobOutput(SessionJob Job, string Text, long Start, bool Truncated);
