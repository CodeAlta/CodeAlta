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
}
