using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;
using XenoAtom.Terminal.UI;

namespace CodeAlta.Plugins;

/// <summary>
/// Describes common operation metadata used when invoking plugin contribution adapters.
/// </summary>
public sealed record PluginAdapterOperationOptions
{
    /// <summary>Gets the current project id, when known.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Gets the current project path, when known.</summary>
    public string? ProjectPath { get; init; }

    /// <summary>Gets the current thread id, when known.</summary>
    public string? ThreadId { get; init; }

    /// <summary>Gets the current run id, when known.</summary>
    public string? RunId { get; init; }

    /// <summary>Gets the active backend id, when known.</summary>
    public string? BackendId { get; init; }

    /// <summary>Gets the active model, when known.</summary>
    public string? Model { get; init; }

    /// <summary>Gets a value indicating whether the active backend is CodeAlta-managed local/raw.</summary>
    public bool IsCodeAltaManagedBackend { get; init; }

    /// <summary>Gets allowed backend family names, when known.</summary>
    public IReadOnlyList<string> BackendFamilies { get; init; } = [];

    /// <summary>Gets a value indicating whether an interactive UI is available.</summary>
    public bool HasInteractiveUi { get; init; }

    /// <summary>Gets a value indicating whether the caller is running without a frontend UI.</summary>
    public bool IsHeadless { get; init; }

    /// <summary>Gets configuration paths visible to backend factories.</summary>
    public IReadOnlyList<string> ConfigurationPaths { get; init; } = [];

    /// <summary>Gets environment values visible to startup/backend contributions.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
}

/// <summary>
/// Describes prompt processing output from plugin adapters.
/// </summary>
public sealed record PluginPromptAdapterResult
{
    /// <summary>Gets the current prompt result.</summary>
    public required PluginPromptResult Result { get; init; }

    /// <summary>Gets diagnostics raised while invoking plugins.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// Describes system/developer prompt content materialized from plugin prompt contributions.
/// </summary>
public sealed record PluginPromptPart
{
    /// <summary>Gets the owning contribution handle.</summary>
    public required PluginContributionHandle Handle { get; init; }

    /// <summary>Gets the prompt contribution metadata.</summary>
    public required PluginSystemPromptContribution Contribution { get; init; }

    /// <summary>Gets the materialized prompt content.</summary>
    public required string Content { get; init; }
}

/// <summary>
/// Describes before-agent-run aggregation output from plugin callbacks.
/// </summary>
public sealed record PluginBeforeAgentRunAdapterResult
{
    /// <summary>Gets the aggregated before-agent-run result.</summary>
    public required PluginBeforeAgentRunResult Result { get; init; }

    /// <summary>Gets diagnostics raised while invoking plugins.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// Describes resource roots resolved from plugin resource contributions.
/// </summary>
public sealed record PluginResolvedResourceContribution
{
    /// <summary>Gets the owning contribution handle.</summary>
    public required PluginContributionHandle Handle { get; init; }

    /// <summary>Gets the resource kind.</summary>
    public required PluginResourceKind Kind { get; init; }

    /// <summary>Gets the resolved resource path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the resource precedence.</summary>
    public int Precedence { get; init; }
}

/// <summary>
/// Describes compaction adapter output.
/// </summary>
public sealed record PluginCompactionAdapterResult
{
    /// <summary>Gets before-compaction results.</summary>
    public IReadOnlyList<PluginBeforeCompactionResult> BeforeResults { get; init; } = [];

    /// <summary>Gets instruction results.</summary>
    public IReadOnlyList<PluginCompactionInstructionResult> InstructionResults { get; init; } = [];

    /// <summary>Gets reducer results.</summary>
    public IReadOnlyList<PluginCompactionReducerResult> ReducerResults { get; init; } = [];

    /// <summary>Gets diagnostics raised while invoking plugins.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// Describes startup adapter output.
/// </summary>
public sealed record PluginStartupAdapterResult
{
    /// <summary>Gets early resources exposed by startup contributions.</summary>
    public IReadOnlyList<PluginResourceContribution> Resources { get; init; } = [];

    /// <summary>Gets diagnostics raised while invoking plugins.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// Adapts registered plugin contributions and active-plugin callbacks into host-callable runtime operations.
/// </summary>
public sealed class PluginContributionAdapterService
{
    private readonly PluginContributionRegistry _registry;
    private readonly PluginRuntimeDiagnosticStore? _diagnostics;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginContributionAdapterService"/> class.
    /// </summary>
    /// <param name="registry">The contribution registry.</param>
    /// <param name="diagnostics">The optional runtime diagnostic store.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="registry"/> is <see langword="null"/>.</exception>
    public PluginContributionAdapterService(PluginContributionRegistry registry, PluginRuntimeDiagnosticStore? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _diagnostics = diagnostics;
    }

    /// <summary>
    /// Gets filtered contribution registrations for a point and project scope.
    /// </summary>
    /// <typeparam name="TContribution">The expected contribution type.</typeparam>
    /// <param name="point">The contribution point.</param>
    /// <param name="options">Operation options.</param>
    /// <returns>Matching contributions in registry order.</returns>
    public IReadOnlyList<PluginContributionRegistration> GetContributions<TContribution>(PluginPoint point, PluginAdapterOperationOptions? options = null)
        where TContribution : class
        => GetRegistrations(point, options).Where(static registration => registration.Contribution is TContribution).ToArray();

    /// <summary>
    /// Executes a command contribution by name or alias.
    /// </summary>
    /// <param name="activePlugins">Active plugins used to build operation contexts.</param>
    /// <param name="name">The command name or alias.</param>
    /// <param name="arguments">Invocation arguments.</param>
    /// <param name="rawText">Raw invocation text, when known.</param>
    /// <param name="options">Operation options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The command result and diagnostics.</returns>
    public async ValueTask<(PluginCommandResult Result, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> ExecuteCommandAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        string name,
        IReadOnlyList<string>? arguments = null,
        string? rawText = null,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        foreach (var registration in GetRegistrations(PluginPoint.Command, options))
        {
            if (registration.Contribution is not PluginCommandContribution command ||
                !MatchesCommandName(command, name))
            {
                continue;
            }

            if (!TryGetActivePlugin(activePlugins, registration, out var active))
            {
                continue;
            }

            try
            {
                var context = CreateCommandContext(active, options, arguments ?? [], rawText, cancellationToken);
                var result = await command.Handler(context, cancellationToken).ConfigureAwait(false);
                context.Invalidate();
                return (result, diagnostics);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCallbackFailure(active, "Command contribution failed.", ex);
                diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(registration, "Command contribution failed.", ex)));
                return (PluginCommandResult.NotHandled, diagnostics);
            }
        }

        return (PluginCommandResult.NotHandled, diagnostics);
    }

    /// <summary>
    /// Runs prompt processors and <see cref="PluginBase.OnPromptSubmittingAsync"/> callbacks in runtime order.
    /// </summary>
    /// <param name="activePlugins">Active plugins.</param>
    /// <param name="text">Prompt text.</param>
    /// <param name="attachments">Prompt attachments.</param>
    /// <param name="options">Operation options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The prompt processing result.</returns>
    public async ValueTask<PluginPromptAdapterResult> ProcessPromptSubmittingAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        string text,
        IReadOnlyList<PluginPromptAttachment>? attachments = null,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentNullException.ThrowIfNull(text);
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        var current = PluginPromptResult.Replace(text, attachments ?? []);

        foreach (var registration in GetRegistrations(PluginPoint.PromptProcessor, options))
        {
            if (registration.Contribution is not PluginPromptProcessorContribution processor ||
                !TryGetActivePlugin(activePlugins, registration, out var active))
            {
                continue;
            }

            var context = CreatePromptContext(active, options, current.ReplacementText ?? text, current.ReplacementAttachments.Count == 0 ? attachments ?? [] : current.ReplacementAttachments, cancellationToken);
            var result = await InvokePromptProcessorAsync(registration, processor, context, diagnostics, cancellationToken).ConfigureAwait(false);
            context.Invalidate();
            if (result is not null)
            {
                current = MergePromptResult(current, result);
                if (current.Disposition is PluginPromptDisposition.Handled or PluginPromptDisposition.Cancel)
                {
                    return new PluginPromptAdapterResult { Result = current, Diagnostics = diagnostics };
                }
            }
        }

        foreach (var active in GetApplicableActivePlugins(activePlugins, options))
        {
            var context = CreatePromptContext(active, options, current.ReplacementText ?? text, current.ReplacementAttachments.Count == 0 ? attachments ?? [] : current.ReplacementAttachments, cancellationToken);
            try
            {
                var result = active.Instance is null ? null : await active.Instance.OnPromptSubmittingAsync(context, cancellationToken).ConfigureAwait(false);
                context.Invalidate();
                if (result is not null)
                {
                    current = MergePromptResult(current, result);
                    if (current.Disposition is PluginPromptDisposition.Handled or PluginPromptDisposition.Cancel)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCallbackFailure(active, "Prompt submission callback failed.", ex);
                diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(active, PluginRuntimeDiagnosticSource.Callback, "Prompt submission callback failed.", ex)));
            }
        }

        return new PluginPromptAdapterResult { Result = current, Diagnostics = diagnostics };
    }

    /// <summary>
    /// Materializes system/developer prompt contributions.
    /// </summary>
    /// <param name="activePlugins">Active plugins.</param>
    /// <param name="channel">The prompt channel.</param>
    /// <param name="supportsDirectInjection">Whether the backend supports direct injection.</param>
    /// <param name="options">Operation options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Prompt parts and diagnostics.</returns>
    public async ValueTask<(IReadOnlyList<PluginPromptPart> Parts, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> BuildSystemPromptPartsAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        PluginPromptChannel channel,
        bool supportsDirectInjection,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        var parts = new List<PluginPromptPart>();
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        foreach (var registration in GetRegistrations(PluginPoint.SystemPrompt, options))
        {
            if (registration.Contribution is not PluginSystemPromptContribution contribution || contribution.Channel != channel ||
                !TryGetActivePlugin(activePlugins, registration, out var active))
            {
                continue;
            }

            var context = CreateSystemPromptContext(active, options, channel, supportsDirectInjection, cancellationToken);
            try
            {
                var content = await contribution.Content(context, cancellationToken).ConfigureAwait(false);
                context.Invalidate();
                if (!string.IsNullOrWhiteSpace(content))
                {
                    parts.Add(new PluginPromptPart { Handle = registration.Handle, Contribution = contribution, Content = content });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCallbackFailure(active, "System prompt contribution failed.", ex);
                diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(registration, "System prompt contribution failed.", ex)));
            }
        }

        return (parts, diagnostics);
    }

    /// <summary>
    /// Runs before-agent-run callbacks and aggregates their effects.
    /// </summary>
    public async ValueTask<PluginBeforeAgentRunAdapterResult> BeforeAgentRunAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        PluginBeforeAgentRunContext template,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentNullException.ThrowIfNull(template);
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        var messages = new List<PluginPromptMessage>();
        var prompts = new List<PluginSystemPromptContribution>();
        var preferredTools = new List<string>();
        foreach (var active in GetApplicableActivePlugins(activePlugins, options))
        {
            var context = CreateBeforeAgentRunContext(active, template, options, cancellationToken);
            try
            {
                var result = active.Instance is null ? null : await active.Instance.OnBeforeAgentRunAsync(context, cancellationToken).ConfigureAwait(false);
                context.Invalidate();
                if (result is null)
                {
                    continue;
                }

                if (result.Cancel)
                {
                    return new PluginBeforeAgentRunAdapterResult { Result = result, Diagnostics = diagnostics };
                }

                messages.AddRange(result.AdditionalMessages);
                prompts.AddRange(result.TemporaryPromptContributions);
                preferredTools.AddRange(result.PreferredToolNames);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCallbackFailure(active, "Before-agent-run callback failed.", ex);
                diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(active, PluginRuntimeDiagnosticSource.Callback, "Before-agent-run callback failed.", ex)));
            }
        }

        return new PluginBeforeAgentRunAdapterResult
        {
            Result = new PluginBeforeAgentRunResult
            {
                AdditionalMessages = messages,
                TemporaryPromptContributions = prompts,
                PreferredToolNames = preferredTools.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            },
            Diagnostics = diagnostics,
        };
    }

    /// <summary>Gets applicable plugin agent tool contributions.</summary>
    public IReadOnlyList<PluginAgentToolContribution> GetAgentTools(PluginAdapterOperationOptions? options = null)
        => GetRegistrations(PluginPoint.AgentTool, options)
            .Select(static registration => registration.Contribution)
            .OfType<PluginAgentToolContribution>()
            .Where(tool => ToolApplies(tool.ActivationPolicy, options))
            .ToArray();

    /// <summary>Gets applicable plugin backend/provider contributions.</summary>
    public IReadOnlyList<PluginAgentBackendContribution> GetAgentBackends(PluginAdapterOperationOptions? options = null)
        => GetRegistrations(PluginPoint.AgentBackend, options)
            .Select(static registration => registration.Contribution)
            .OfType<PluginAgentBackendContribution>()
            .ToArray();

    /// <summary>Runs early startup contribution handlers and returns early resource contributions.</summary>
    public async ValueTask<PluginStartupAdapterResult> RunStartupAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        IReadOnlyList<string> rawArguments,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentNullException.ThrowIfNull(rawArguments);
        var resources = new List<PluginResourceContribution>();
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        foreach (var registration in GetRegistrations(PluginPoint.Startup, options))
        {
            if (registration.Contribution is not PluginStartupContribution startup ||
                !TryGetActivePlugin(activePlugins, registration, out var active))
            {
                continue;
            }

            resources.AddRange(startup.Resources);
            if (startup.Handler is null)
            {
                continue;
            }

            var context = CreateStartupContext(active, options, rawArguments, cancellationToken);
            try
            {
                await startup.Handler(context, cancellationToken).ConfigureAwait(false);
                context.Invalidate();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCallbackFailure(active, "Startup contribution failed.", ex);
                diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(registration, "Startup contribution failed.", ex)));
            }
        }

        return new PluginStartupAdapterResult { Resources = resources, Diagnostics = diagnostics };
    }

    /// <summary>Creates a plugin-contributed backend by contribution name.</summary>
    public async ValueTask<(IAgentBackend? Backend, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> CreateAgentBackendAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        string name,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        foreach (var registration in GetRegistrations(PluginPoint.AgentBackend, options))
        {
            if (registration.Contribution is not PluginAgentBackendContribution contribution ||
                !string.Equals(contribution.Name, name, StringComparison.OrdinalIgnoreCase) ||
                !TryGetActivePlugin(activePlugins, registration, out var active))
            {
                continue;
            }

            var context = CreateAgentBackendFactoryContext(active, options, cancellationToken);
            try
            {
                var backend = await contribution.Factory(context, cancellationToken).ConfigureAwait(false);
                context.Invalidate();
                return (backend, diagnostics);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCallbackFailure(active, "Agent backend contribution failed.", ex);
                diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(registration, "Agent backend contribution failed.", ex)));
                return (null, diagnostics);
            }
        }

        return (null, diagnostics);
    }

    /// <summary>Runs tool-call interception callbacks until one blocks or replaces arguments.</summary>
    public async ValueTask<(PluginToolCallResult? Result, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> OnToolCallAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        PluginToolCallContext template,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentNullException.ThrowIfNull(template);
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        foreach (var active in GetApplicableActivePlugins(activePlugins, options))
        {
            var context = CreateToolCallContext(active, template, options, cancellationToken);
            try
            {
                var result = active.Instance is null ? null : await active.Instance.OnToolCallAsync(context, cancellationToken).ConfigureAwait(false);
                context.Invalidate();
                if (result is not null && result.Disposition != PluginToolCallDisposition.Allow)
                {
                    return (result, diagnostics);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCallbackFailure(active, "Tool-call callback failed.", ex);
                diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(active, PluginRuntimeDiagnosticSource.Callback, "Tool-call callback failed.", ex)));
            }
        }

        return (null, diagnostics);
    }

    /// <summary>Runs tool-result interception callbacks and returns the last transformation.</summary>
    public async ValueTask<(PluginToolResult? Result, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> OnToolResultAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        PluginToolResultContext template,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentNullException.ThrowIfNull(template);
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        PluginToolResult? current = null;
        foreach (var active in GetApplicableActivePlugins(activePlugins, options))
        {
            var context = CreateToolResultContext(active, template, options, cancellationToken);
            try
            {
                var result = active.Instance is null ? null : await active.Instance.OnToolResultAsync(context, cancellationToken).ConfigureAwait(false);
                context.Invalidate();
                if (result is not null && result.Disposition != PluginToolResultDisposition.Unchanged)
                {
                    current = result;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCallbackFailure(active, "Tool-result callback failed.", ex);
                diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(active, PluginRuntimeDiagnosticSource.Callback, "Tool-result callback failed.", ex)));
            }
        }

        return (current, diagnostics);
    }

    /// <summary>Broadcasts a normalized agent event to applicable active plugins.</summary>
    public async ValueTask<IReadOnlyList<PluginRuntimeDiagnostic>> ObserveAgentEventAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        PluginAgentEventContext template,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentNullException.ThrowIfNull(template);
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        foreach (var active in GetApplicableActivePlugins(activePlugins, options))
        {
            var context = CreateAgentEventContext(active, template, options, cancellationToken);
            try
            {
                if (active.Instance is not null)
                {
                    await active.Instance.OnAgentEventAsync(context, cancellationToken).ConfigureAwait(false);
                }

                context.Invalidate();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCallbackFailure(active, "Agent-event callback failed.", ex);
                diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(active, PluginRuntimeDiagnosticSource.Callback, "Agent-event callback failed.", ex)));
            }
        }

        return diagnostics;
    }

    /// <summary>Runs compaction contributions for the requested stage contexts.</summary>
    public async ValueTask<PluginCompactionAdapterResult> RunCompactionAsync(
        PluginBeforeCompactionContext? before = null,
        PluginCompactionInstructionContext? instructions = null,
        PluginCompactionReducerContext? reducer = null,
        PluginAfterCompactionContext? after = null,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var beforeResults = new List<PluginBeforeCompactionResult>();
        var instructionResults = new List<PluginCompactionInstructionResult>();
        var reducerResults = new List<PluginCompactionReducerResult>();
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        foreach (var registration in GetRegistrations(PluginPoint.Compaction, options))
        {
            if (registration.Contribution is not PluginCompactionContribution contribution)
            {
                continue;
            }

            try
            {
                if (before is not null && contribution.BeforeCompaction is not null)
                {
                    beforeResults.Add(await contribution.BeforeCompaction(before, cancellationToken).ConfigureAwait(false));
                }

                if (instructions is not null && contribution.Instructions is not null)
                {
                    instructionResults.Add(await contribution.Instructions(instructions, cancellationToken).ConfigureAwait(false));
                }

                if (reducer is not null && contribution.Reducer is not null)
                {
                    reducerResults.Add(await contribution.Reducer(reducer, cancellationToken).ConfigureAwait(false));
                }

                if (after is not null && contribution.AfterCompaction is not null)
                {
                    await contribution.AfterCompaction(after, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(registration, "Compaction contribution failed.", ex)));
            }
        }

        return new PluginCompactionAdapterResult
        {
            BeforeResults = beforeResults,
            InstructionResults = instructionResults,
            ReducerResults = reducerResults,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>Resolves applicable resource contributions to host-consumable paths.</summary>
    public IReadOnlyList<PluginResolvedResourceContribution> GetResources(IReadOnlyList<ActivePluginInstance> activePlugins, PluginAdapterOperationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        var resources = new List<PluginResolvedResourceContribution>();
        foreach (var registration in GetRegistrations(PluginPoint.Resource, options))
        {
            if (registration.Contribution is not PluginResourceContribution resource ||
                !TryGetActivePlugin(activePlugins, registration, out var active))
            {
                continue;
            }

            resources.Add(new PluginResolvedResourceContribution
            {
                Handle = registration.Handle,
                Kind = resource.Kind,
                Path = resource.IsPackageRelative ? Path.GetFullPath(Path.Combine(active.RuntimeContext.PackageDirectory, resource.Path)) : Path.GetFullPath(resource.Path),
                Precedence = resource.Precedence,
            });
        }

        return resources;
    }

    /// <summary>Gets status items from applicable UI status contributions.</summary>
    public IReadOnlyList<PluginStatusItem> GetStatusItems(IReadOnlyList<ActivePluginInstance> activePlugins, PluginAdapterOperationOptions? options = null)
        => GetStatusItems(activePlugins, region: null, options);

    /// <summary>Gets status items from applicable UI status contributions for a region.</summary>
    public IReadOnlyList<PluginStatusItem> GetStatusItems(IReadOnlyList<ActivePluginInstance> activePlugins, PluginUiRegion? region, PluginAdapterOperationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        if (IsHeadlessOrNonInteractive(options))
        {
            return [];
        }

        var items = new List<PluginStatusItem>();
        foreach (var registration in GetRegistrations(PluginPoint.Ui, options))
        {
            if (registration.Contribution is not PluginStatusContribution status ||
                (region is not null && status.Region != region.Value) ||
                !TryGetActivePlugin(activePlugins, registration, out var active))
            {
                continue;
            }

            var item = status.GetStatus(CreateStatusContext(active, options, default));
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>Creates visuals from applicable visual UI contributions.</summary>
    public IReadOnlyList<Visual> CreateVisuals(IReadOnlyList<ActivePluginInstance> activePlugins, PluginUiRegion region, PluginAdapterOperationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        if (IsHeadlessOrNonInteractive(options))
        {
            return [];
        }

        var visuals = new List<Visual>();
        foreach (var registration in GetRegistrations(PluginPoint.Ui, options))
        {
            if (registration.Contribution is not PluginVisualContribution visualContribution ||
                visualContribution.Region != region ||
                !TryGetActivePlugin(activePlugins, registration, out var active))
            {
                continue;
            }

            var visual = visualContribution.Visual ?? visualContribution.CreateVisual?.Invoke(CreateVisualContext(active, options, region, default));
            if (visual is not null)
            {
                visuals.Add(visual);
            }
        }

        return visuals;
    }

    /// <summary>Runs renderer contributions for a UI region/target and returns rendered payloads.</summary>
    public async ValueTask<(IReadOnlyList<PluginRenderResult> Results, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> RenderAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        PluginUiRegion region,
        string? target,
        object? payload,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        if (IsHeadlessOrNonInteractive(options))
        {
            return ([], []);
        }

        var results = new List<PluginRenderResult>();
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        foreach (var registration in GetRegistrations(PluginPoint.Ui, options))
        {
            if (registration.Contribution is not PluginRendererContribution renderer ||
                renderer.Region != region ||
                !RendererTargetMatches(renderer.Target, target) ||
                !TryGetActivePlugin(activePlugins, registration, out var active))
            {
                continue;
            }

            var context = CreateRendererContext(active, options, target, payload, cancellationToken);
            try
            {
                var result = await renderer.Renderer(context, cancellationToken).ConfigureAwait(false);
                context.Invalidate();
                if (result is not null)
                {
                    results.Add(result);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCallbackFailure(active, "Renderer contribution failed.", ex);
                diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(registration, "Renderer contribution failed.", ex)));
            }
        }

        return (results, diagnostics);
    }

    private IReadOnlyList<PluginContributionRegistration> GetRegistrations(PluginPoint point, PluginAdapterOperationOptions? options)
        => _registry.GetSnapshot()
            .Where(registration => registration.Handle.Point == point && PluginContributionRegistry.AppliesToProject(registration, options?.ProjectId, options?.ProjectPath))
            .ToArray();

    private static bool MatchesCommandName(PluginCommandContribution command, string name)
        => string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase) ||
            command.Aliases.Any(alias => string.Equals(alias, name, StringComparison.OrdinalIgnoreCase));

    private static bool TryGetActivePlugin(IReadOnlyList<ActivePluginInstance> activePlugins, PluginContributionRegistration registration, out ActivePluginInstance active)
    {
        active = activePlugins.FirstOrDefault(plugin => string.Equals(plugin.Descriptor.RuntimeKey, registration.Handle.PluginRuntimeKey, StringComparison.Ordinal))!;
        return active is not null && active.Instance is not null && active.RuntimeContext.IsValid;
    }

    private static IEnumerable<ActivePluginInstance> GetApplicableActivePlugins(IReadOnlyList<ActivePluginInstance> activePlugins, PluginAdapterOperationOptions? options)
        => activePlugins.Where(plugin => plugin.Instance is not null && plugin.RuntimeContext.IsValid && plugin.RuntimeContext.AppliesToProject(options?.ProjectId, options?.ProjectPath));

    private static bool ToolApplies(PluginToolActivationPolicy policy, PluginAdapterOperationOptions? options)
    {
        if (policy.RequiresCodeAltaManagedBackend && options?.IsCodeAltaManagedBackend != true)
        {
            return false;
        }

        if (policy.BackendNames.Count > 0 && !policy.BackendNames.Contains(options?.BackendId ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (policy.BackendFamilies.Count > 0 && !(options?.BackendFamilies ?? []).Intersect(policy.BackendFamilies, StringComparer.OrdinalIgnoreCase).Any())
        {
            return false;
        }

        return true;
    }

    private static bool IsHeadlessOrNonInteractive(PluginAdapterOperationOptions? options)
        => options is not null && (options.IsHeadless || !options.HasInteractiveUi);

    private static bool RendererTargetMatches(string? rendererTarget, string? requestedTarget)
        => string.IsNullOrWhiteSpace(rendererTarget) ||
            string.Equals(rendererTarget, requestedTarget, StringComparison.OrdinalIgnoreCase);

    private async ValueTask<PluginPromptResult?> InvokePromptProcessorAsync(PluginContributionRegistration registration, PluginPromptProcessorContribution processor, PluginPromptSubmittingContext context, List<PluginRuntimeDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        try
        {
            return await processor.Handler(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(registration, "Prompt processor contribution failed.", ex)));
            return null;
        }
    }

    private PluginRuntimeDiagnostic AddDiagnostic(PluginRuntimeDiagnostic diagnostic)
    {
        _diagnostics?.Add(diagnostic);
        return diagnostic;
    }

    private static void LogCallbackFailure(ActivePluginInstance active, string message, Exception exception)
    {
        if (LogManager.IsInitialized && active.RuntimeContext.Logger.IsEnabled(LogLevel.Error))
        {
            active.RuntimeContext.Logger.Error(exception, message);
        }
    }

    private static PluginPromptResult MergePromptResult(PluginPromptResult current, PluginPromptResult next)
    {
        if (next.Disposition == PluginPromptDisposition.Continue)
        {
            return current with { TemporaryPromptContributions = current.TemporaryPromptContributions.Concat(next.TemporaryPromptContributions).ToArray() };
        }

        return next with
        {
            TemporaryPromptContributions = current.TemporaryPromptContributions.Concat(next.TemporaryPromptContributions).ToArray(),
        };
    }

    private static PluginRuntimeDiagnostic CreateCallbackDiagnostic(PluginContributionRegistration registration, string message, Exception exception)
        => new()
        {
            Severity = PluginDiagnosticSeverity.Error,
            Source = PluginRuntimeDiagnosticSource.Callback,
            RuntimeKey = registration.Handle.PluginRuntimeKey,
            Message = message,
            Exception = PluginExceptionInfo.FromException(exception),
            Metadata = new Dictionary<string, string>
            {
                ["Point"] = registration.Handle.Point.ToString(),
                ["NaturalName"] = registration.Handle.NaturalName ?? string.Empty,
                ["RuntimeContributionKey"] = registration.Handle.RuntimeContributionKey,
            },
        };

    private static PluginRuntimeDiagnostic CreateCallbackDiagnostic(ActivePluginInstance active, PluginRuntimeDiagnosticSource source, string message, Exception exception)
        => new()
        {
            Severity = PluginDiagnosticSeverity.Error,
            Source = source,
            RuntimeKey = active.Descriptor.RuntimeKey,
            PackageId = active.SourcePackage?.PackageId,
            Path = active.SourcePackage?.PackageDirectory,
            Message = message,
            Exception = PluginExceptionInfo.FromException(exception),
        };

    private static PluginCommandContext CreateCommandContext(ActivePluginInstance active, PluginAdapterOperationOptions? options, IReadOnlyList<string> arguments, string? rawText, CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options?.ProjectId,
            ProjectPath = options?.ProjectPath,
            ThreadId = options?.ThreadId,
            RunId = options?.RunId,
            BackendId = options?.BackendId,
            Model = options?.Model,
            Arguments = arguments,
            RawText = rawText,
            CancellationToken = cancellationToken,
        };

    private static PluginStartupContext CreateStartupContext(ActivePluginInstance active, PluginAdapterOperationOptions? options, IReadOnlyList<string> rawArguments, CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options?.ProjectId,
            ProjectPath = options?.ProjectPath,
            ThreadId = options?.ThreadId,
            RunId = options?.RunId,
            BackendId = options?.BackendId,
            Model = options?.Model,
            RawArguments = rawArguments,
            ConfigurationPaths = options?.ConfigurationPaths ?? [],
            Environment = options?.Environment ?? new Dictionary<string, string?>(),
            CancellationToken = cancellationToken,
        };

    private static PluginPromptSubmittingContext CreatePromptContext(ActivePluginInstance active, PluginAdapterOperationOptions? options, string text, IReadOnlyList<PluginPromptAttachment> attachments, CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options?.ProjectId,
            ProjectPath = options?.ProjectPath,
            ThreadId = options?.ThreadId,
            RunId = options?.RunId,
            BackendId = options?.BackendId,
            Model = options?.Model,
            Text = text,
            Attachments = attachments,
            IsCodeAltaManagedBackend = options?.IsCodeAltaManagedBackend ?? false,
            CancellationToken = cancellationToken,
        };

    private static PluginSystemPromptContext CreateSystemPromptContext(ActivePluginInstance active, PluginAdapterOperationOptions? options, PluginPromptChannel channel, bool supportsDirectInjection, CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options?.ProjectId,
            ProjectPath = options?.ProjectPath,
            ThreadId = options?.ThreadId,
            RunId = options?.RunId,
            BackendId = options?.BackendId,
            Model = options?.Model,
            Channel = channel,
            SupportsDirectInjection = supportsDirectInjection,
            CancellationToken = cancellationToken,
        };

    private static PluginBeforeAgentRunContext CreateBeforeAgentRunContext(ActivePluginInstance active, PluginBeforeAgentRunContext template, PluginAdapterOperationOptions? options, CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options?.ProjectId ?? template.ProjectId,
            ProjectPath = options?.ProjectPath ?? template.ProjectPath,
            ThreadId = options?.ThreadId ?? template.ThreadId,
            RunId = options?.RunId ?? template.RunId,
            BackendId = options?.BackendId ?? template.BackendId,
            Model = options?.Model ?? template.Model,
            PromptText = template.PromptText,
            Input = template.Input,
            ActiveToolNames = template.ActiveToolNames,
            CancellationToken = cancellationToken,
        };

    private static PluginToolCallContext CreateToolCallContext(ActivePluginInstance active, PluginToolCallContext template, PluginAdapterOperationOptions? options, CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options?.ProjectId ?? template.ProjectId,
            ProjectPath = options?.ProjectPath ?? template.ProjectPath,
            ThreadId = options?.ThreadId ?? template.ThreadId,
            RunId = options?.RunId ?? template.RunId,
            BackendId = options?.BackendId ?? template.BackendId,
            Model = options?.Model ?? template.Model,
            Invocation = template.Invocation,
            CancellationToken = cancellationToken,
        };

    private static PluginToolResultContext CreateToolResultContext(ActivePluginInstance active, PluginToolResultContext template, PluginAdapterOperationOptions? options, CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options?.ProjectId ?? template.ProjectId,
            ProjectPath = options?.ProjectPath ?? template.ProjectPath,
            ThreadId = options?.ThreadId ?? template.ThreadId,
            RunId = options?.RunId ?? template.RunId,
            BackendId = options?.BackendId ?? template.BackendId,
            Model = options?.Model ?? template.Model,
            Invocation = template.Invocation,
            Result = template.Result,
            CancellationToken = cancellationToken,
        };

    private static PluginAgentEventContext CreateAgentEventContext(ActivePluginInstance active, PluginAgentEventContext template, PluginAdapterOperationOptions? options, CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options?.ProjectId ?? template.ProjectId,
            ProjectPath = options?.ProjectPath ?? template.ProjectPath,
            ThreadId = options?.ThreadId ?? template.ThreadId,
            RunId = options?.RunId ?? template.RunId,
            BackendId = options?.BackendId ?? template.BackendId,
            Model = options?.Model ?? template.Model,
            Event = template.Event,
            Session = template.Session,
            CancellationToken = cancellationToken,
        };

    private static PluginStatusContext CreateStatusContext(ActivePluginInstance active, PluginAdapterOperationOptions? options, CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options?.ProjectId,
            ProjectPath = options?.ProjectPath,
            ThreadId = options?.ThreadId,
            RunId = options?.RunId,
            BackendId = options?.BackendId,
            Model = options?.Model,
            CancellationToken = cancellationToken,
        };

    private static PluginVisualContext CreateVisualContext(ActivePluginInstance active, PluginAdapterOperationOptions? options, PluginUiRegion region, CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options?.ProjectId,
            ProjectPath = options?.ProjectPath,
            ThreadId = options?.ThreadId,
            RunId = options?.RunId,
            BackendId = options?.BackendId,
            Model = options?.Model,
            Region = region,
            HasInteractiveUi = options?.HasInteractiveUi ?? false,
            CancellationToken = cancellationToken,
        };

    private static PluginRendererContext CreateRendererContext(ActivePluginInstance active, PluginAdapterOperationOptions? options, string? target, object? payload, CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options?.ProjectId,
            ProjectPath = options?.ProjectPath,
            ThreadId = options?.ThreadId,
            RunId = options?.RunId,
            BackendId = options?.BackendId,
            Model = options?.Model,
            Target = target,
            Payload = payload,
            CancellationToken = cancellationToken,
        };

    private static PluginAgentBackendFactoryContext CreateAgentBackendFactoryContext(ActivePluginInstance active, PluginAdapterOperationOptions? options, CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options?.ProjectId,
            ProjectPath = options?.ProjectPath,
            ThreadId = options?.ThreadId,
            RunId = options?.RunId,
            BackendId = options?.BackendId,
            Model = options?.Model,
            Logger = active.RuntimeContext.Logger,
            PackageDirectory = active.RuntimeContext.PackageDirectory,
            ConfigurationPaths = options?.ConfigurationPaths ?? [],
            Environment = options?.Environment ?? new Dictionary<string, string?>(),
            CancellationToken = cancellationToken,
        };
}
