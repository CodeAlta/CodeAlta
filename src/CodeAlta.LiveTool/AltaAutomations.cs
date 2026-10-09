namespace CodeAlta.LiveTool;

/// <summary>
/// The automations of a host that has some: in the desktop app, the prompts that start a session by themselves, on
/// a schedule or when the user asks. A host without automations registers no such service, and
/// <c>alta automation</c> is then not part of its commands.
/// </summary>
public interface IAltaAutomations
{
    /// <summary>Lists the automations, by name.</summary>
    /// <returns>The automations as their configuration files define them now.</returns>
    IReadOnlyList<AltaAutomation> List();

    /// <summary>Lists the runs of an automation, or of all, newest first.</summary>
    /// <param name="id">The identifier of the automation; null for the runs of all.</param>
    /// <param name="limit">The most runs to return.</param>
    /// <returns>The runs this instance of the application remembers.</returns>
    IReadOnlyList<AltaAutomationRun> ListRuns(string? id, int limit);

    /// <summary>Finds the run that started a session.</summary>
    /// <param name="sessionId">The identifier of the session.</param>
    /// <returns>The run, or null when the session was not started by an automation.</returns>
    AltaAutomationRun? FindRunOfSession(string sessionId);

    /// <summary>Runs an automation now, whatever its triggers.</summary>
    /// <param name="id">The identifier of the automation.</param>
    /// <param name="cancellationToken">Cancels before the session is created.</param>
    /// <returns>The run once its session exists or it failed to start; null when there is no such automation.</returns>
    Task<AltaAutomationRun?> RunAsync(string id, CancellationToken cancellationToken);

    /// <summary>Writes a new automation in the configuration of the user, or of the project it runs in.</summary>
    /// <param name="request">The automation.</param>
    /// <param name="cancellationToken">Cancels before the file is written.</param>
    /// <returns>
    /// The identifier of the automation with the status <c>ok</c>, or why it was refused: <c>refused</c>, or
    /// <c>denied</c> for a command trigger in a host that has the user review the commands of its sessions.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    Task<AltaAutomationChange> CreateAsync(AltaAutomationRequest request, CancellationToken cancellationToken);

    /// <summary>Enables or disables the triggers of an automation.</summary>
    /// <param name="id">The identifier of the automation.</param>
    /// <param name="enabled">Whether its triggers start it.</param>
    /// <param name="cancellationToken">Cancels before the file is written.</param>
    /// <returns>
    /// The status <c>ok</c>, <c>not_found</c>, <c>refused</c> with the reason, or <c>denied</c> when enabling it
    /// would start a command in a host that has the user review the commands of its sessions.
    /// </returns>
    Task<AltaAutomationChange> SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken);

    /// <summary>Removes an automation from its configuration file. Its runs and their sessions stay.</summary>
    /// <param name="id">The identifier of the automation.</param>
    /// <param name="cancellationToken">Cancels before the file is written.</param>
    /// <returns>The status <c>ok</c>, <c>not_found</c>, or <c>refused</c> with the reason.</returns>
    Task<AltaAutomationChange> DeleteAsync(string id, CancellationToken cancellationToken);
}

/// <summary>An automation.</summary>
/// <param name="Id">Its identifier, a GUID.</param>
/// <param name="Name">Its name, which also names the sessions it starts.</param>
/// <param name="Enabled">Whether its triggers start it.</param>
/// <param name="Prompt">What it sends to the session it starts.</param>
/// <param name="ProjectId">The project it runs in; null when it runs as a chat.</param>
/// <param name="ProjectPath">The folder of that project.</param>
/// <param name="File">The configuration file that defines it.</param>
/// <param name="Model">The model it asks for, written <c>provider[:model][@effort]</c>; null for the default one.</param>
/// <param name="Agent">The agent prompt it asks for; null for the default one.</param>
/// <param name="Triggers">
/// Its triggers, such as <c>daily@09:00</c>, <c>hourly@15</c>, <c>weekly@mon,thu@08:30</c>, <c>cron@0 9 * * 1-5</c>,
/// <c>issue@opened</c> or <c>pull_request@updated</c>, the last two with <c>+anyone</c> when they run for every
/// author, or <c>command@&lt;command line&gt;</c> for a command it keeps running. None: it is run by hand.
/// </param>
/// <param name="Problem">Why it cannot run as defined; null when it can.</param>
/// <param name="NextRunAt">When a schedule of it is next due; null without one, when disabled or while the automations are paused.</param>
/// <param name="Running">Whether a run of it is in progress.</param>
/// <param name="Allowed">
/// Whether its triggers may start it. An automation kept with a project comes with its repository: its triggers
/// start it once the user allowed it in the application, as it is now. Running it when asked needs no allowance.
/// </param>
public sealed record AltaAutomation(string Id, string Name, bool Enabled, string Prompt, string? ProjectId, string? ProjectPath, string File,
    string? Model, string? Agent, IReadOnlyList<string> Triggers, string? Problem, DateTimeOffset? NextRunAt, bool Running, bool Allowed = true);

/// <summary>One time an automation was started.</summary>
/// <param name="Id">The identifier of the run.</param>
/// <param name="AutomationId">The automation.</param>
/// <param name="Name">The name of the automation when it ran.</param>
/// <param name="SessionId">The session the run started; null when it could not start one.</param>
/// <param name="ProjectId">The project of that session; null for a chat.</param>
/// <param name="StartedAt">When the run started.</param>
/// <param name="EndedAt">When it ended; null while it runs.</param>
/// <param name="Trigger">What started it: <c>manual</c>, or the kind of the trigger.</param>
/// <param name="Detail">What the trigger was about, such as the issue that was opened, or the last line its command wrote.</param>
/// <param name="Status"><c>running</c>, <c>completed</c>, <c>failed</c>, <c>cancelled</c>, <c>interrupted</c> or <c>skipped</c>.</param>
/// <param name="Message">Why the run failed or was skipped.</param>
public sealed record AltaAutomationRun(string Id, string AutomationId, string? Name, string? SessionId, string? ProjectId, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, string Trigger, string? Detail, string Status, string? Message);

/// <summary>A new automation.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Prompt">What it sends to the session it starts.</param>
public sealed record AltaAutomationRequest(string Name, string Prompt)
{
    /// <summary>Gets the project it runs in; null to run as a chat.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets whether it is written in the configuration of its project rather than of the user.</summary>
    public bool StoreInProject { get; init; }

    /// <summary>Gets the model it asks for, written <c>provider[:model][@effort]</c>.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the agent prompt it asks for.</summary>
    public string? Agent { get; init; }

    /// <summary>Gets whether its triggers start it.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets whether what was missed while the application was not running is run when it starts: once for a
    /// schedule, and for each issue or pull request of an event trigger.
    /// </summary>
    public bool CatchUp { get; init; }

    /// <summary>Gets its triggers, written as <see cref="AltaAutomation.Triggers"/> shows them.</summary>
    public IReadOnlyList<string> Triggers { get; init; } = [];
}

/// <summary>What a change to the automations gave.</summary>
/// <param name="Status"><c>ok</c>, <c>not_found</c>, <c>refused</c>, or <c>denied</c>.</param>
/// <param name="Id">The automation that was written.</param>
/// <param name="Message">Why the change was refused.</param>
public sealed record AltaAutomationChange(string Status, string? Id = null, string? Message = null);
