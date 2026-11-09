namespace CodeAlta.Automations;

/// <summary>
/// What an automation that runs on the machines with some tags (a pool) takes: the triggers each machine of the pool fires
/// by itself and claims at the server, one machine winning each due time or event.
/// </summary>
internal static class AutomationPools
{
    /// <summary>Gets whether a pool of machines takes a trigger: schedules, cron, issues, pull requests and Jira.</summary>
    /// <param name="trigger">The trigger.</param>
    /// <returns>Whether every machine of a pool can fire it and claim it.</returns>
    internal static bool Takes(AutomationTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return trigger.Kind is AutomationTriggerKind.Hourly or AutomationTriggerKind.Daily or AutomationTriggerKind.Weekly or AutomationTriggerKind.Cron
            or AutomationTriggerKind.Issue or AutomationTriggerKind.PullRequest or AutomationTriggerKind.Jira or AutomationTriggerKind.JiraVersion or AutomationTriggerKind.JiraQuery;
    }

    /// <summary>Says why a pool of machines does not take a trigger.</summary>
    /// <param name="trigger">The trigger.</param>
    /// <returns>Why, in a sentence.</returns>
    internal static string Why(AutomationTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return trigger.Kind switch
        {
            AutomationTriggerKind.Command => "a command trigger runs on this computer only.",
            AutomationTriggerKind.Slack => "Slack delivers its messages to one machine.",
            AutomationTriggerKind.IncidentIo or AutomationTriggerKind.Webhook => "a hook of the server is the address of one machine.",
            _ => "the events of a project or of another automation are those of one machine.",
        };
    }
}
