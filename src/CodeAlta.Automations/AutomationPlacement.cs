namespace CodeAlta.Automations;

/// <summary>
/// Runs by hand, on the machine it is placed on, an automation of the user that runs on another machine
/// (<see cref="AutomationDefinition.RunsOn"/>): the desktop asks the paired node through its node API.
/// </summary>
internal interface IAutomationPlacement
{
    /// <summary>Runs an automation now on the machine it is placed on.</summary>
    /// <param name="entry">The automation, as this machine defines it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The run as that machine recorded it, or a failed run that says why it did not start there. Failures are results.</returns>
    Task<AutomationRun> RunAsync(AutomationEntry entry, CancellationToken cancellationToken);

    /// <summary>Runs an automation now on the machine it is placed on, or tries it there: a dry run, whose tools it calls by itself record their calls instead of making them.</summary>
    /// <param name="entry">The automation, as this machine defines it.</param>
    /// <param name="dryRun">Whether it is a dry run.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The run as that machine recorded it, or a failed run that says why it did not start there.</returns>
    Task<AutomationRun> RunAsync(AutomationEntry entry, bool dryRun, CancellationToken cancellationToken) => RunAsync(entry, cancellationToken);

    /// <summary>
    /// Why the triggers of an automation that runs on this machine do not start it yet: a machine it was placed on may
    /// still run its copy, until that machine took it away or a person starts it here anyway.
    /// </summary>
    /// <param name="id">The automation.</param>
    /// <returns>Why it waits; null when nothing does. Called while the caller holds its own lock: it calls nothing back.</returns>
    string? WaitsHere(string id);
}
