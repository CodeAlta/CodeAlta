using System.Collections.Concurrent;
using CodeAlta.Agent;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Orchestration.Runtime.Plugins;

/// <summary>
/// Calls the run-end hooks of the plugins (<see cref="PluginBase.OnAgentRunEndedAsync"/>) when a run of a session ends
/// normally, and gives the session the prompt of the first hook that asks it to continue, as a prompt of a plugin.
/// </summary>
/// <remarks>
/// It follows the events the runtime publishes: a run ends normally with its idle event, never when it fails or is
/// stopped. A session continues at most <see cref="MaxContinuations"/> times in a row: a prompt of anybody but a plugin
/// starts the count again. The hooks run beside the events, never in their way: an event is not held while a hook
/// thinks.
/// </remarks>
public sealed class PluginRunEndHooks
{
    /// <summary>How many runs in a row plugins may continue.</summary>
    public const int MaxContinuations = 3;

    private const string LoggerName = "CodeAlta.Plugins.RunEnd";

    private readonly PluginContributionAdapterService _adapter;
    private readonly Func<IReadOnlyList<ActivePluginInstance>> _getActivePlugins;
    private readonly Func<string, string, CancellationToken, Task<string>> _deliver;
    private readonly ConcurrentDictionary<string, SessionRuns> _sessions = new(StringComparer.Ordinal);

    /// <summary>Initializes the hooks.</summary>
    /// <param name="adapter">The plugin contribution adapter.</param>
    /// <param name="getActivePlugins">Gets the current active plugin snapshot.</param>
    /// <param name="deliver">Gives a session a prompt of a plugin: the session, the prompt; returns how it was delivered.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public PluginRunEndHooks(PluginContributionAdapterService adapter, Func<IReadOnlyList<ActivePluginInstance>> getActivePlugins,
        Func<string, string, CancellationToken, Task<string>> deliver)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(getActivePlugins);
        ArgumentNullException.ThrowIfNull(deliver);
        _adapter = adapter;
        _getActivePlugins = getActivePlugins;
        _deliver = deliver;
    }

    /// <summary>Gets or sets what tracks the work the hooks start, so that a host can wait for it; the default runs it as it is.</summary>
    public Func<Func<Task>, Task> Track { get; set; } = static work => Task.Run(work);

    /// <summary>
    /// Follows a published event of a session. It returns at once: a run that ended starts its hooks beside the events.
    /// </summary>
    /// <param name="envelope">The event and its session.</param>
    /// <returns>The work of the hooks the event started, or a completed task.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="envelope"/> is <see langword="null"/>.</exception>
    public Task Observe(RuntimePluginAgentEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        switch (envelope.Event)
        {
            case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Shutdown }:
                _sessions.TryRemove(envelope.SessionId, out _);
                return Task.CompletedTask;
            case AgentContentCompletedEvent or AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle } when !PluginContributionAdapterService.HasRunEndHooks(_getActivePlugins()):
                // Nothing is kept for sessions while no plugin hooks the end of runs.
                return Task.CompletedTask;
            case AgentContentCompletedEvent { Kind: AgentContentKind.User } user:
            {
                var runs = _sessions.GetOrAdd(envelope.SessionId, static _ => new SessionRuns());
                lock (runs)
                {
                    // A prompt of anybody but a plugin starts the count of continuations again.
                    if (!string.Equals(user.Provenance, AgentPromptProvenance.Plugin, StringComparison.Ordinal)) runs.Continuations = 0;
                    runs.LastAnswer = null;
                }

                return Task.CompletedTask;
            }
            case AgentContentCompletedEvent { Kind: AgentContentKind.Assistant } answer when _sessions.TryGetValue(envelope.SessionId, out var runs):
                lock (runs) runs.LastAnswer = answer.Content;
                return Task.CompletedTask;
            case AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle, RunId: { } runId }:
                return RunEnded(envelope, runId);
            default:
                return Task.CompletedTask;
        }
    }

    private Task RunEnded(RuntimePluginAgentEventEnvelope envelope, AgentRunId runId)
    {
        var activePlugins = _getActivePlugins();
        var runs = _sessions.GetOrAdd(envelope.SessionId, static _ => new SessionRuns());
        int continuations;
        string? lastAnswer;
        lock (runs)
        {
            // An idle event is published once per run; a run already seen is not asked about twice.
            if (runs.LastEndedRun == runId.Value) return Task.CompletedTask;
            runs.LastEndedRun = runId.Value;
            continuations = runs.Continuations;
            lastAnswer = runs.LastAnswer;
        }

        if (continuations >= MaxContinuations)
        {
            Info($"Session {envelope.SessionId} was continued {continuations} times in a row by plugins: its run-end hooks are not asked again until another prompt.");
            return Task.CompletedTask;
        }

        return Track(() => RunHooksAsync(activePlugins, envelope, runId, runs, continuations, lastAnswer));
    }

    private async Task RunHooksAsync(IReadOnlyList<ActivePluginInstance> activePlugins, RuntimePluginAgentEventEnvelope envelope, AgentRunId runId,
        SessionRuns runs, int continuations, string? lastAnswer)
    {
        try
        {
            var seed = activePlugins[0];
            var options = envelope.CreateOptions() with { RunId = runId.Value, IsHeadless = true, HasInteractiveUi = false };
            var result = await _adapter.OnAgentRunEndedAsync(activePlugins, new PluginAgentRunEndedContext
            {
                Plugin = seed.Descriptor,
                Services = seed.RuntimeContext.Services,
                LastAssistantMessage = lastAnswer,
                Continuations = continuations,
                MaxContinuations = MaxContinuations,
            }, options, CancellationToken.None).ConfigureAwait(false);
            if (result.Prompt is not { } prompt) return;
            lock (runs) runs.Continuations = continuations + 1;
            var status = await _deliver(envelope.SessionId, prompt, CancellationToken.None).ConfigureAwait(false);
            Info($"Plugin {result.ContinuedBy?.DisplayName ?? result.ContinuedBy?.RuntimeKey ?? "?"} continued session {envelope.SessionId} ({status}).");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var message = "The run-end hooks of session " + envelope.SessionId + " failed.";
            LogManager.GetLogger(LoggerName).Error(exception, message);
        }
    }

    // A plain text: what is logged is decided by the host, and a host without logs (a test) logs nothing.
    private static void Info(string message) => LogManager.GetLogger(LoggerName).Info(message);

    private sealed class SessionRuns
    {
        public int Continuations { get; set; }

        public string? LastAnswer { get; set; }

        public string? LastEndedRun { get; set; }
    }
}
