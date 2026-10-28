using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

namespace CodeAlta.Plugins;

/// <summary>What the tool-call hooks of the plugins decided for a call.</summary>
public sealed record PluginToolCallAdapterResult
{
    /// <summary>
    /// Gets the decision: <see cref="PluginToolCallDisposition.Block"/> with the reason of the plugin that blocked,
    /// <see cref="PluginToolCallDisposition.ReplaceArguments"/> with the arguments the hooks left, or <see langword="null"/>
    /// to let the call run as it is.
    /// </summary>
    public PluginToolCallResult? Result { get; init; }

    /// <summary>Gets what the model reads when the call is blocked: <c>Blocked by &lt;plugin&gt;: &lt;reason&gt;</c>.</summary>
    public string? BlockMessage { get; init; }

    /// <summary>Gets the plugin that blocked the call.</summary>
    public PluginDescriptor? BlockedBy { get; init; }

    /// <summary>Gets diagnostics raised while invoking plugins.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>What the tool-result hooks of the plugins made of a result.</summary>
public sealed record PluginToolResultAdapterResult
{
    /// <summary>Gets the result to give the model.</summary>
    public required AgentToolResult Result { get; init; }

    /// <summary>Gets whether a hook changed the result.</summary>
    public bool Changed { get; init; }

    /// <summary>Gets diagnostics raised while invoking plugins.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics { get; init; } = [];
}

// The hooks of tool calls: each plugin in turn, each with its own time limit; a hook that fails or
// does not answer is logged, and the operation goes on without it (or the call is blocked, for a plugin that fails closed).
public sealed partial class PluginContributionAdapterService
{
    // The plugin that gave each tool of a before-run hook, by the tool as it was given: a tool hook is told who provides a tool.
    private readonly ConditionalWeakTable<AgentToolSpec, string> _toolPlugins = new();

    /// <summary>Gets whether a plugin of a snapshot hooks tool calls or their results: without one, a call is not intercepted.</summary>
    /// <param name="activePlugins">The active plugins.</param>
    /// <returns><see langword="true"/> when a plugin overrides <see cref="PluginBase.OnToolCallAsync"/> or <see cref="PluginBase.OnToolResultAsync"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="activePlugins"/> is <see langword="null"/>.</exception>
    public static bool HasToolHooks(IReadOnlyList<ActivePluginInstance> activePlugins)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        foreach (var active in activePlugins)
        {
            if (active.Hooks.ToolCalls || active.Hooks.ToolResults) return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the id of the plugin that provides a tool: one of its agent tools, or a tool its before-run hook gave a run.
    /// </summary>
    /// <param name="spec">The specification of the tool, as the plugin made it.</param>
    /// <returns>The id of the plugin (<c>mcp</c> for an MCP server), or <see langword="null"/> for a tool no plugin provides.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="spec"/> is <see langword="null"/>.</exception>
    public string? FindToolPlugin(AgentToolSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (_toolPlugins.TryGetValue(spec, out var given)) return given;
        PluginContributionRegistration? named = null;
        foreach (var registration in _registry.GetSnapshot())
        {
            if (registration.Contribution is not PluginAgentToolContribution tool) continue;
            if (ReferenceEquals(tool.Definition.Spec, spec)) return IdOf(registration);
            // A session keeps the tool of a plugin that was built again since: the tool of the same name is the plugin's.
            if (named is null && string.Equals(tool.Definition.Spec.Name, spec.Name, StringComparison.Ordinal)) named = registration;
        }

        return named is null ? null : IdOf(named);

        static string IdOf(PluginContributionRegistration registration)
            => registration.PluginId ?? PluginContributionRegistry.PluginIdOf(registration.Handle.PluginRuntimeKey, null);
    }

    /// <summary>Runs the tool-call hooks, each with the arguments the hooks before it left, until one blocks the call.</summary>
    /// <param name="activePlugins">The active plugins.</param>
    /// <param name="template">The context of the call: its invocation, its tool, what applies to it.</param>
    /// <param name="options">Operation scope options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The block, the arguments the hooks left, or nothing to do.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async ValueTask<PluginToolCallAdapterResult> RunToolCallHooksAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        PluginToolCallContext template,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentNullException.ThrowIfNull(template);
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        var invocation = template.Invocation;
        var replaced = false;
        foreach (var active in GetApplicableActivePlugins(activePlugins, options))
        {
            if (!active.Hooks.ToolCalls) continue;
            var context = CreateToolCallContext(active, template, invocation, options, cancellationToken);
            var outcome = await InvokeHookAsync(active, token => active.Instance!.OnToolCallAsync(context, token), cancellationToken).ConfigureAwait(false);
            context.Invalidate();
            if (outcome.Failure is { } failure)
            {
                var message = outcome.TimedOut ? "Tool-call hook did not answer in time." : "Tool-call hook failed.";
                diagnostics.Add(ReportHookFailure(active, message, failure));
                if (active.Hooks.FailClosed && template.CanBlock)
                {
                    var why = outcome.TimedOut
                        ? $"its check did not answer within {Seconds(active)} seconds"
                        : $"its check failed ({failure.Message})";
                    return Blocked(active, why, diagnostics);
                }

                continue;
            }

            switch (outcome.Value)
            {
                case { Disposition: PluginToolCallDisposition.Block } block when template.CanBlock:
                    return Blocked(active, string.IsNullOrWhiteSpace(block.BlockReason) ? "the call was blocked" : block.BlockReason, diagnostics);
                case { Disposition: PluginToolCallDisposition.ReplaceArguments, ReplacementArguments: { } arguments } when template.CanModifyArguments:
                    invocation = invocation with { Arguments = arguments.Clone() };
                    replaced = true;
                    break;
                case { Disposition: PluginToolCallDisposition.Block or PluginToolCallDisposition.ReplaceArguments }:
                    diagnostics.Add(ReportHookWarning(active, $"Tool-call hook asked for what does not apply to the call of '{template.ToolName}' ({template.Capabilities}); it was ignored."));
                    break;
            }
        }

        return new PluginToolCallAdapterResult
        {
            Result = replaced ? PluginToolCallResult.ReplaceArguments(invocation.Arguments) : null,
            Diagnostics = diagnostics,
        };

        PluginToolCallAdapterResult Blocked(ActivePluginInstance active, string reason, List<PluginRuntimeDiagnostic> raised)
            => new()
            {
                Result = PluginToolCallResult.Block(reason),
                BlockMessage = $"Blocked by {NameOf(active)}: {reason}",
                BlockedBy = active.Descriptor,
                Diagnostics = raised,
            };
    }

    /// <summary>Runs tool-call interception callbacks: see <see cref="RunToolCallHooksAsync"/>.</summary>
    /// <param name="activePlugins">The active plugins.</param>
    /// <param name="template">The context of the call.</param>
    /// <param name="options">Operation scope options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The block or the arguments the hooks left, or <see langword="null"/>, and diagnostics.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public async ValueTask<(PluginToolCallResult? Result, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> OnToolCallAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        PluginToolCallContext template,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunToolCallHooksAsync(activePlugins, template, options, cancellationToken).ConfigureAwait(false);
        return (result.Result, result.Diagnostics);
    }

    /// <summary>Runs the tool-result hooks, each with the result the hooks before it left.</summary>
    /// <param name="activePlugins">The active plugins.</param>
    /// <param name="template">The context of the call, with the result of its tool.</param>
    /// <param name="options">Operation scope options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result to give the model.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async ValueTask<PluginToolResultAdapterResult> RunToolResultHooksAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        PluginToolResultContext template,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentNullException.ThrowIfNull(template);
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        var current = template.Result;
        var changed = false;
        List<(string Plugin, JsonElement Details)>? attached = null;
        foreach (var active in GetApplicableActivePlugins(activePlugins, options))
        {
            if (!active.Hooks.ToolResults) continue;
            var context = CreateToolResultContext(active, template, current, options, cancellationToken);
            var outcome = await InvokeHookAsync(active, token => active.Instance!.OnToolResultAsync(context, token), cancellationToken).ConfigureAwait(false);
            context.Invalidate();
            if (outcome.Failure is { } failure)
            {
                diagnostics.Add(ReportHookFailure(active, outcome.TimedOut ? "Tool-result hook did not answer in time." : "Tool-result hook failed.", failure));
                continue;
            }

            switch (outcome.Value)
            {
                case { Disposition: PluginToolResultDisposition.Replace, ReplacementResult: { } replacement } when template.CanChangeResult:
                    current = replacement;
                    changed = true;
                    break;
                case { Disposition: PluginToolResultDisposition.Append, AppendedText: { Length: > 0 } text } when template.CanChangeResult:
                    current = current with { Items = [.. current.Items, new AgentToolResultItem.Text(text)] };
                    changed = true;
                    break;
                case { Disposition: PluginToolResultDisposition.AttachDetails, Details: { } details }:
                    (attached ??= []).Add((PluginIdOf(active), details.Clone()));
                    changed = true;
                    break;
                case { Disposition: PluginToolResultDisposition.Replace or PluginToolResultDisposition.Append }:
                    diagnostics.Add(ReportHookWarning(active, $"Tool-result hook asked to change the result of '{template.ToolName}', which cannot be changed there; it was ignored."));
                    break;
            }
        }

        if (attached is not null)
        {
            current = current with { Details = MergeDetails(current.Details, attached) };
        }

        return new PluginToolResultAdapterResult { Result = current, Changed = changed, Diagnostics = diagnostics };
    }

    /// <summary>Runs tool-result interception callbacks: see <see cref="RunToolResultHooksAsync"/>.</summary>
    /// <param name="activePlugins">The active plugins.</param>
    /// <param name="template">The context of the call, with the result of its tool.</param>
    /// <param name="options">Operation scope options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A replacement of the result when a hook changed it, or <see langword="null"/>, and diagnostics.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public async ValueTask<(PluginToolResult? Result, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> OnToolResultAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        PluginToolResultContext template,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunToolResultHooksAsync(activePlugins, template, options, cancellationToken).ConfigureAwait(false);
        return (result.Changed ? PluginToolResult.Replace(result.Result) : null, result.Diagnostics);
    }

    private void RecordToolPlugin(ActivePluginInstance active, IReadOnlyList<AgentToolDefinition> tools)
    {
        var id = PluginIdOf(active);
        foreach (var tool in tools)
        {
            _toolPlugins.AddOrUpdate(tool.Spec, id);
        }
    }

    private static string PluginIdOf(ActivePluginInstance active)
        => PluginContributionRegistry.PluginIdOf(active.Descriptor.RuntimeKey, active.SourcePackage?.PackageId);

    private static string Seconds(ActivePluginInstance active)
        => active.Hooks.Timeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private static string NameOf(ActivePluginInstance active)
        => string.IsNullOrWhiteSpace(active.Descriptor.DisplayName) ? PluginIdOf(active) : active.Descriptor.DisplayName;

    // Runs a hook within the time limit of its plugin. A hook that ignores its token is not waited for past the limit; what
    // it throws later is observed, never raised.
    private static async ValueTask<HookOutcome<T>> InvokeHookAsync<T>(ActivePluginInstance active, Func<CancellationToken, ValueTask<T?>> hook, CancellationToken cancellationToken)
        where T : class
    {
        if (active.Instance is null) return new HookOutcome<T>(null, null, false);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(active.Hooks.Timeout);
        Task<T?> task;
        try
        {
            task = hook(limit.Token).AsTask();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new HookOutcome<T>(null, exception, false);
        }

        try
        {
            return new HookOutcome<T>(await task.WaitAsync(active.Hooks.Timeout, cancellationToken).ConfigureAwait(false), null, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is TimeoutException || (exception is OperationCanceledException && limit.IsCancellationRequested))
        {
            if (!task.IsCompleted) _ = task.ContinueWith(static ended => _ = ended.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new HookOutcome<T>(null, new TimeoutException($"The hook did not answer within {Seconds(active)} seconds."), true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new HookOutcome<T>(null, exception, false);
        }
    }

    private PluginRuntimeDiagnostic ReportHookFailure(ActivePluginInstance active, string message, Exception failure)
    {
        var text = message + " " + failure.Message;
        if (failure is TimeoutException) active.RuntimeContext.Logger.Warn(text);
        else LogCallbackFailure(active, message, failure);
        return AddDiagnostic(CreateCallbackDiagnostic(active, PluginRuntimeDiagnosticSource.Callback, message, failure));
    }

    private PluginRuntimeDiagnostic ReportHookWarning(ActivePluginInstance active, string message)
    {
        active.RuntimeContext.Logger.Warn(message);
        return AddDiagnostic(PluginRuntimeDiagnostic.Warning(PluginRuntimeDiagnosticSource.Callback, message, active.SourcePackage?.PackageId, active.SourcePackage?.PackageDirectory) with
        {
            RuntimeKey = active.Descriptor.RuntimeKey,
        });
    }

    // The details of each plugin under its id, beside those already attached.
    private static JsonElement MergeDetails(JsonElement? existing, List<(string Plugin, JsonElement Details)> attached)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            var written = new HashSet<string>(attached.Select(static entry => entry.Plugin), StringComparer.Ordinal);
            if (existing is { ValueKind: JsonValueKind.Object } previous)
            {
                foreach (var property in previous.EnumerateObject())
                {
                    if (!written.Contains(property.Name)) property.WriteTo(writer);
                }
            }

            // A plugin that attached twice keeps its last details.
            foreach (var group in attached.GroupBy(static entry => entry.Plugin, StringComparer.Ordinal))
            {
                writer.WritePropertyName(group.Key);
                group.Last().Details.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static PluginToolCallContext CreateToolCallContext(ActivePluginInstance active, PluginToolCallContext template, AgentToolInvocation invocation, PluginAdapterOperationOptions? options, CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options?.ProjectId ?? template.ProjectId,
            ProjectPath = options?.ProjectPath ?? template.ProjectPath,
            SessionId = options?.SessionId ?? template.SessionId,
            SpaceId = options?.SpaceId ?? template.SpaceId,
            RunId = options?.RunId ?? template.RunId,
            ProviderId = options?.ProviderId ?? template.ProviderId,
            Model = options?.Model ?? template.Model,
            Invocation = invocation,
            ToolKind = template.ToolKind,
            ToolPluginId = template.ToolPluginId,
            WorkingDirectory = template.WorkingDirectory,
            ShellCommand = ReferenceEquals(invocation, template.Invocation) ? template.ShellCommand : ShellOf(template, invocation),
            ModifiedFiles = template.ModifiedFiles,
            ParentToolCallId = template.ParentToolCallId,
            Capabilities = template.Capabilities,
            CancellationToken = cancellationToken,
        };

    // The command a hook replaced is the one the next hook checks.
    private static PluginShellCommand? ShellOf(PluginToolCallContext template, AgentToolInvocation invocation)
        => template.ShellCommand is { } shell && invocation.Arguments.ValueKind == JsonValueKind.Object
            && invocation.Arguments.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String
                ? shell with { Command = command.GetString() ?? shell.Command }
                : template.ShellCommand;

    private static PluginToolResultContext CreateToolResultContext(ActivePluginInstance active, PluginToolResultContext template, AgentToolResult result, PluginAdapterOperationOptions? options, CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options?.ProjectId ?? template.ProjectId,
            ProjectPath = options?.ProjectPath ?? template.ProjectPath,
            SessionId = options?.SessionId ?? template.SessionId,
            SpaceId = options?.SpaceId ?? template.SpaceId,
            RunId = options?.RunId ?? template.RunId,
            ProviderId = options?.ProviderId ?? template.ProviderId,
            Model = options?.Model ?? template.Model,
            Invocation = template.Invocation,
            Result = result,
            ToolKind = template.ToolKind,
            ToolPluginId = template.ToolPluginId,
            WorkingDirectory = template.WorkingDirectory,
            ShellCommand = template.ShellCommand,
            ModifiedFiles = template.ModifiedFiles,
            ParentToolCallId = template.ParentToolCallId,
            Capabilities = template.Capabilities,
            CancellationToken = cancellationToken,
        };

    private readonly record struct HookOutcome<T>(T? Value, Exception? Failure, bool TimedOut) where T : class;
}
