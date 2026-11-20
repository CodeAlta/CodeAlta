using CodeAlta.Automations;
using CodeAlta.LiveTool;
using CodeAlta.LiveTool.Scripts;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop;

/// <summary>Runs the scripts of automations without a model, with the <c>alta</c> commands of the window and the plugins that run.</summary>
internal static class DesktopUnattendedScripts
{
    /// <summary>Creates what runs them for the automations of a host.</summary>
    /// <param name="dispatcher">The <c>alta</c> commands of the host.</param>
    /// <param name="host">The host.</param>
    /// <param name="notifications">Where the notifications of the scripts (<c>notify()</c>) reach the user.</param>
    /// <returns>What the runner of the automations calls.</returns>
    internal static Func<AutomationScriptRun, CancellationToken, Task<AutomationScriptOutcome>> Create(AltaCommandDispatcher dispatcher, CodeAltaHost host,
        IUserNotifications? notifications = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(host);
        var plugins = new RuntimeAltaPluginCatalog(host.PluginRuntime);
        var runner = new UnattendedScriptRunner(dispatcher, host.CodeModeScripts, host.RuntimeService,
            () => [.. plugins.ListCommandContributions().Select(static contribution => contribution.Command.Path)]) { Notifications = notifications };
        return async (run, cancellationToken) =>
        {
            var result = await runner.RunAsync(new UnattendedScriptRequest(run.RunId, run.Script)
            {
                ArgumentsJson = run.ArgumentsJson, EventText = run.EventText, EventJson = run.EventJson, ProjectId = run.ProjectId, ProjectPath = run.ProjectPath,
                Spaces = run.Spaces, Sandbox = run.Sandbox, Write = run.Write, Environment = run.Environment,
            }, cancellationToken).ConfigureAwait(false);
            return new AutomationScriptOutcome(result.Succeeded, result.Value, result.Error, result.Presented);
        };
    }
}
