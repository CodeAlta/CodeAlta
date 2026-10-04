using System.Collections;
using CodeAlta.Agent;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Prompts;

namespace CodeAlta.Desktop;

/// <summary>
/// Gives the sessions of the desktop host the same <c>alta</c> tool as the terminal UI: notes, sessions and
/// sub-sessions, reminders, skills, projects, models and prompts, all served in-process by the host.
/// </summary>
/// <remarks>
/// <para>
/// Three things are specific to the desktop. <c>alta ask</c> goes to the window's ask panel through the run
/// that asks, so only a run started from the window can ask, once. Sessions that alta commands create or
/// drive use the host's own permission defaults, which lets the window keep sending to them. Reminders are
/// the ones the Reminders view shows and delivers.
/// </para>
/// <para>
/// The commands of the host's active plugins (<c>alta mcp</c>, <c>alta statistics</c>) are part of the tool,
/// and sessions that alta commands send to get the same plugin tools and instructions as sends from the window.
/// </para>
/// </remarks>
internal static class DesktopAltaTools
{
    /// <summary>Composes the alta services over a host and makes them the session tool of its owned sessions.</summary>
    /// <param name="host">The running host.</param>
    /// <param name="reminders">The reminder service the window lists and delivers from.</param>
    /// <param name="pluginAlta">The bridge through which the host's plugins invoke alta commands, if they can.</param>
    /// <exception cref="ArgumentNullException">The host or the reminders are null.</exception>
    internal static void Attach(CodeAltaHost host, AltaReminderService reminders, PluginAltaServiceBridge? pluginAlta = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(reminders);
        var dispatcher = Compose(host, reminders);
        pluginAlta?.SetDispatcher(dispatcher);
        host.Commands.SessionTools = CreateSessionTools(dispatcher);
    }

    /// <summary>Builds the dispatcher of the alta commands over a host's services.</summary>
    internal static AltaCommandDispatcher Compose(CodeAltaHost host, AltaReminderService reminders)
    {
        var permissions = host.RuntimeService.Permissions;
        var services = new AltaServiceCollection()
            .Add(host.CatalogOptions)
            .Add(host.SessionViewCatalog.TextFiles)
            .Add(host.ProjectCatalog)
            .Add(host.SessionViewCatalog)
            .Add(host.RuntimeService)
            .Add(host.RuntimeService.SkillCatalog)
            .Add(host.AgentHub)
            .Add(host.ModelProviderInitializationService)
            .Add(host.ModelProviderRegistry)
            .Add(host.ProjectFileSearchService)
            .Add<IAltaAskService>(new OwnedSessionAltaAskService(host.Commands.Asks))
            .Add<IAltaNotesService>(new RuntimeAltaNotesService(host.RuntimeService))
            .Add<IReadOnlyList<ModelProviderDescriptor>>(new RegisteredProviders(host.ModelProviderRegistry))
            .Add<IAltaSessionToolProviderPolicy>(new AltaSessionToolProviderPolicy())
            .Add<IAltaSessionInteractionDefaults>(new AltaSessionInteractionDefaults(
                permissions.OwnedDefaultPermissionHandler, permissions.OwnedDefaultUserInputHandler))
            .Add(reminders)
            // The plugins as they are when a command runs; without an active plugin both add nothing.
            .Add<IAltaPluginCatalog>(new RuntimeAltaPluginCatalog(host.PluginRuntime))
            .AddPluginRuntimeHooks(host.PluginRuntime);
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, services);
        services.Add(registry).Add(dispatcher);
        return dispatcher;
    }

    /// <summary>
    /// Returns the supplier of an owned session's tools: one <c>alta</c> tool per session, identical in name,
    /// description and schema to the one alta commands give the sessions they create.
    /// </summary>
    internal static Func<OwnedSessionToolRequest, IReadOnlyList<AgentToolDefinition>> CreateSessionTools(AltaCommandDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        return request =>
        {
            // A session being created has no id yet: its tool learns it from its first call.
            var session = request.SessionId;
            var tool = AltaSessionToolFactory.Create(dispatcher, new AltaSessionToolOptions
            {
                SourceSessionIdProvider = () => Volatile.Read(ref session),
                SourceProjectId = request.ProjectId,
                WorkingDirectory = request.WorkingDirectory,
                DefaultMaxOutputRecords = 200,
                DefaultMaxOutputBytes = 64 * 1024,
                DefaultTimeout = TimeSpan.FromSeconds(120),
            });
            return
            [
                tool with
                {
                    Handler = (invocation, cancellationToken) =>
                    {
                        Interlocked.CompareExchange(ref session, invocation.SessionId, null);
                        return tool.Handler(invocation, cancellationToken);
                    },
                },
            ];
        };
    }

    // The providers as the registry lists them now: Settings can add, remove and re-register providers while
    // the host runs.
    private sealed class RegisteredProviders(ModelProviderRegistry registry) : IReadOnlyList<ModelProviderDescriptor>
    {
        public int Count => registry.ListProviders().Count;

        public ModelProviderDescriptor this[int index] => registry.ListProviders()[index];

        public IEnumerator<ModelProviderDescriptor> GetEnumerator() => registry.ListProviders().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
