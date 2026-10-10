using System.Security.Cryptography;
using System.Text;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;
using XenoAtom.Logging;

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

    /// <summary>Gets the current session id, when known.</summary>
    public string? SessionId { get; init; }

    /// <summary>Gets the current run id, when known.</summary>
    public string? RunId { get; init; }

    /// <summary>Gets the active provider id, when known.</summary>
    public string? ProviderId { get; init; }

    /// <summary>Gets the active model, when known.</summary>
    public string? Model { get; init; }

    /// <summary>Gets a value indicating whether the active provider is CodeAlta-managed local/raw.</summary>
    public bool IsCodeAltaManagedProvider { get; init; }

    /// <summary>Gets allowed provider family names, when known.</summary>
    public IReadOnlyList<string> ProviderFamilies { get; init; } = [];

    /// <summary>Gets a value indicating whether an interactive UI is available.</summary>
    public bool HasInteractiveUi { get; init; }

    /// <summary>Gets whether this call explicitly supports optional terminal visuals. Defaults to false.</summary>
    public bool SupportsTerminalVisuals { get; init; }

    /// <summary>Gets a value indicating whether the caller is running without a frontend UI.</summary>
    public bool IsHeadless { get; init; }

    /// <summary>Gets configuration paths visible to provider factories.</summary>
    public IReadOnlyList<string> ConfigurationPaths { get; init; } = [];

    /// <summary>Gets environment values visible to startup/provider contributions.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
}

/// <summary>A status item and the contribution it comes from.</summary>
/// <param name="Registration">The status contribution.</param>
/// <param name="Item">The status item.</param>
public sealed record PluginStatusEntry(PluginContributionRegistration Registration, PluginStatusItem Item);

/// <summary>A button of a plugin, with its state for a context.</summary>
/// <param name="Registration">The button contribution.</param>
/// <param name="Button">The button.</param>
/// <param name="State">The state the plugin gave for the context, or the default state.</param>
public sealed record PluginButtonEntry(PluginContributionRegistration Registration, PluginButtonContribution Button, PluginButtonState State);

/// <summary>A card of the landing page, with what its plugin gave for a space.</summary>
/// <param name="Registration">The card contribution.</param>
/// <param name="Contribution">The card as the plugin declares it.</param>
/// <param name="Context">The space and the project the card was asked about.</param>
/// <param name="Card">What the card shows, or <see langword="null"/> when the plugin leaves it out or could not write it.</param>
/// <param name="Failed">Whether the plugin threw or did not answer in time: the card is shown as one that could not be read.</param>
public sealed record PluginLandingCardEntry(PluginContributionRegistration Registration, PluginLandingCardContribution Contribution, PluginLandingCardContext Context,
    PluginLandingCard? Card, bool Failed);

/// <summary>The content of a UI region and the contribution it comes from.</summary>
/// <param name="Registration">The content contribution.</param>
/// <param name="Content">The content.</param>
public sealed record PluginContentEntry(PluginContributionRegistration Registration, PluginRenderResult Content);

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
/// Describes final instruction processing output from plugin adapters.
/// </summary>
public sealed record PluginInstructionProcessingAdapterResult
{
    /// <summary>Gets the final system message.</summary>
    public string? SystemMessage { get; init; }

    /// <summary>Gets the final developer instructions.</summary>
    public string? DeveloperInstructions { get; init; }

    /// <summary>Gets a value indicating whether processing changed instructions.</summary>
    public bool WasChanged { get; init; }

    /// <summary>Gets a value indicating whether processing cancelled the run.</summary>
    public bool WasCancelled { get; init; }

    /// <summary>Gets the cancellation reason, when cancelled.</summary>
    public string? CancelReason { get; init; }

    /// <summary>Gets diagnostics raised while invoking plugins.</summary>
    public IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Gets audit-safe transformation records.</summary>
    public IReadOnlyList<PluginInstructionTransformationRecord> Transformations { get; init; } = [];
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
    // The calls of landing cards that run, by the card as its plugin declared it: a card that is gone with its plugin takes its calls with it.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<PluginLandingCardContribution, LandingReads> _landingReads = new();

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
    /// Executes a command contribution by contribution snapshot.
    /// </summary>
    /// <param name="activePlugins">Active plugins used to build operation contexts.</param>
    /// <param name="contribution">The command contribution.</param>
    /// <param name="options">Operation options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The command result and diagnostics.</returns>
    public async ValueTask<(PluginCommandResult Result, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> ExecuteCommandAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        PluginCommandContribution contribution,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentNullException.ThrowIfNull(contribution);
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        foreach (var registration in GetRegistrations(PluginPoint.Command, options))
        {
            if (!ReferenceEquals(registration.Contribution, contribution) ||
                registration.Contribution is not PluginCommandContribution command)
            {
                continue;
            }

            if (!TryGetActivePlugin(activePlugins, registration, out var active))
            {
                continue;
            }

            try
            {
                var context = CreateCommandContext(active, options, cancellationToken);
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
    /// <param name="supportsDirectInjection">Whether the provider supports direct injection.</param>
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
        var additionalTools = new List<AgentToolDefinition>();
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
                additionalTools.AddRange(result.AdditionalTools);
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
                AdditionalTools = additionalTools,
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

    /// <summary>
    /// Runs final system/developer instruction processors and returns transformed instructions.
    /// </summary>
    /// <param name="activePlugins">Active plugins.</param>
    /// <param name="template">The instruction processing context template.</param>
    /// <param name="options">Operation options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The instruction processing result.</returns>
    public async ValueTask<PluginInstructionProcessingAdapterResult> ProcessInstructionsAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        PluginInstructionProcessingContext template,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentNullException.ThrowIfNull(template);
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        var transformations = new List<PluginInstructionTransformationRecord>(template.PriorTransformations);
        var systemMessage = template.Instructions.SystemMessage;
        var developerInstructions = template.Instructions.DeveloperInstructions;
        var wasChanged = false;

        foreach (var registration in GetRegistrations(PluginPoint.InstructionProcessor, options))
        {
            if (registration.Contribution is not PluginInstructionProcessorContribution processor ||
                !InstructionProcessorApplies(processor.Target, template.Stage, options) ||
                !TryGetActivePlugin(activePlugins, registration, out var active))
            {
                continue;
            }

            var beforeSystem = systemMessage;
            var beforeDeveloper = developerInstructions;
            var context = CreateInstructionProcessingContext(active, template, options, systemMessage, developerInstructions, transformations, cancellationToken);
            PluginInstructionProcessingResult? result;
            try
            {
                result = await processor.Handler(context, cancellationToken).ConfigureAwait(false);
                context.Invalidate();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Invalidate();
                LogCallbackFailure(active, "Instruction processor contribution failed.", ex);
                diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(registration, "Instruction processor contribution failed.", ex)));
                continue;
            }

            result ??= PluginInstructionProcessingResult.Continue;
            if (result.Disposition == PluginInstructionProcessingDisposition.Replace)
            {
                if (result.ReplacementSystemMessage is not null)
                {
                    systemMessage = result.ReplacementSystemMessage;
                }

                if (result.ReplacementDeveloperInstructions is not null)
                {
                    developerInstructions = result.ReplacementDeveloperInstructions;
                }
            }

            var changedChannels = GetChangedInstructionChannels(beforeSystem, beforeDeveloper, systemMessage, developerInstructions);
            wasChanged |= changedChannels.Count > 0;
            transformations.Add(CreateTransformationRecord(registration, processor, template.Stage, result, changedChannels, systemMessage, developerInstructions));
            if (result.Disposition == PluginInstructionProcessingDisposition.Cancel)
            {
                return new PluginInstructionProcessingAdapterResult
                {
                    SystemMessage = systemMessage,
                    DeveloperInstructions = developerInstructions,
                    WasChanged = wasChanged,
                    WasCancelled = true,
                    CancelReason = result.UserMessage ?? result.ChangeSummary ?? "Plugin cancelled the agent run.",
                    Diagnostics = diagnostics,
                    Transformations = transformations,
                };
            }
        }

        return new PluginInstructionProcessingAdapterResult
        {
            SystemMessage = systemMessage,
            DeveloperInstructions = developerInstructions,
            WasChanged = wasChanged,
            Diagnostics = diagnostics,
            Transformations = transformations,
        };
    }

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
        // Eligibility is not liveness: stale supplied candidates must receive explicit Closing admission.
        foreach (var active in activePlugins.Where(plugin => plugin.RuntimeContext.AppliesToProject(options?.ProjectId, options?.ProjectPath)))
        {
            var admission = await active.ObserveOwnedAgentEventAsync(async () =>
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
            }).ConfigureAwait(false);
            if (admission != PluginAgentEventAdmission.Admitted)
            {
                // Return the outcome, but do not grow the manager's diagnostic store for rejected events.
                diagnostics.Add(PluginRuntimeDiagnostic.Warning(PluginRuntimeDiagnosticSource.Callback,
                    $"Agent-event admission: {admission}.", active.SourcePackage?.PackageId, active.SourcePackage?.PackageDirectory));
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
        => GetStatusEntries(activePlugins, region, options).Select(static entry => entry.Item).ToArray();

    /// <summary>Gets status items with the contribution each one comes from.</summary>
    /// <param name="activePlugins">Active plugins used to build operation contexts.</param>
    /// <param name="region">The region to read, or <see langword="null"/> for every region.</param>
    /// <param name="options">Operation options.</param>
    /// <returns>The status items in contribution order.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="activePlugins"/> is null.</exception>
    public IReadOnlyList<PluginStatusEntry> GetStatusEntries(IReadOnlyList<ActivePluginInstance> activePlugins, PluginUiRegion? region, PluginAdapterOperationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        if (IsHeadlessOrNonInteractive(options))
        {
            return [];
        }

        var items = new List<PluginStatusEntry>();
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
                items.Add(new PluginStatusEntry(registration, item));
            }
        }

        return items;
    }

    /// <summary>Gets the buttons that active plugins put at a place, with their state for a context.</summary>
    /// <param name="activePlugins">Active plugins used to build operation contexts.</param>
    /// <param name="place">The place to read, or <see langword="null"/> for every place.</param>
    /// <param name="spaceId">The shown space, or <see langword="null"/>.</param>
    /// <param name="options">Operation options: the project and session the buttons are asked about.</param>
    /// <returns>The buttons in contribution order. A button whose plugin throws from its state callback has the default state and the failure is recorded.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="activePlugins"/> is null.</exception>
    public IReadOnlyList<PluginButtonEntry> GetButtonEntries(IReadOnlyList<ActivePluginInstance> activePlugins, PluginButtonPlace? place, string? spaceId, PluginAdapterOperationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        if (IsHeadlessOrNonInteractive(options)) return [];
        var entries = new List<PluginButtonEntry>();
        foreach (var registration in GetRegistrations(PluginPoint.Ui, options))
        {
            if (registration.Contribution is not PluginButtonContribution button || place is { } wanted && button.Place != wanted
                || !TryGetActivePlugin(activePlugins, registration, out var active))
            {
                continue;
            }

            var state = PluginButtonState.Default;
            if (button.GetState is { } getState)
            {
                try
                {
                    state = getState(new PluginButtonContext(button.Place, spaceId, options?.ProjectId, options?.SessionId)) ?? PluginButtonState.Default;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogCallbackFailure(active, "Button state callback failed.", ex);
                    AddDiagnostic(CreateCallbackDiagnostic(registration, "Button state callback failed.", ex));
                }
            }

            entries.Add(new PluginButtonEntry(registration, button, state));
        }

        return entries;
    }

    /// <summary>
    /// Asks the active plugins for the cards they pin on the landing page of a space. The cards are asked at the same time, each
    /// with its own time limit: a card that throws or does not answer is returned as failed, and the others are not affected.
    /// </summary>
    /// <param name="activePlugins">Active plugins.</param>
    /// <param name="spaceId">The space the landing page is shown in, or <see langword="null"/>.</param>
    /// <param name="spaceProjectIds">The projects of that space, or <see langword="null"/> when every project is in it: the cards of a plugin of a project are asked for only when its project is in the space.</param>
    /// <param name="timeout">How long one card may take.</param>
    /// <param name="cancellationToken">A token to cancel the reading.</param>
    /// <returns>The cards in contribution order; a card that its plugin leaves out is not in the list.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="activePlugins"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="timeout"/> is not positive.</exception>
    public async ValueTask<IReadOnlyList<PluginLandingCardEntry>> GetLandingCardEntriesAsync(IReadOnlyList<ActivePluginInstance> activePlugins, string? spaceId,
        IReadOnlySet<string>? spaceProjectIds, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var pending = new List<Task<PluginLandingCardEntry?>>();
        foreach (var registration in _registry.GetSnapshot())
        {
            if (registration.Handle.Point != PluginPoint.LandingCard || registration.Contribution is not PluginLandingCardContribution card
                || !TryGetActivePlugin(activePlugins, registration, out var active))
            {
                continue;
            }

            string? projectId = null;
            if (registration.Scope != PluginScope.Global)
            {
                // A plugin of a project speaks of that project: its card is on the landing page of the spaces the project is in.
                projectId = registration.ScopeProjectId;
                if (string.IsNullOrWhiteSpace(projectId) || spaceProjectIds is not null && !spaceProjectIds.Contains(projectId)) continue;
            }

            pending.Add(ReadLandingCardAsync(registration, card, active, new PluginLandingCardContext(spaceId, projectId), timeout, cancellationToken));
        }

        var entries = await Task.WhenAll(pending).ConfigureAwait(false);
        return [.. entries.OfType<PluginLandingCardEntry>()];
    }

    private async Task<PluginLandingCardEntry?> ReadLandingCardAsync(PluginContributionRegistration registration, PluginLandingCardContribution card, ActivePluginInstance active,
        PluginLandingCardContext context, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (reading, started) = StartOrJoinLandingRead(card, active, context, timeout);
        if (!started)
        {
            // A call of the card runs, started before this reading: what it returns may be older than the question. The reading waits for
            // it to end and asks again, so that calls of a card follow one another. A call that does not end within the limit is not
            // followed by another one: the card failed, and its handler holds what it holds until it returns.
            try
            {
                await reading.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                if (active.RuntimeContext.LifetimeCancellationToken.IsCancellationRequested) return null;
                if (!reading.IsCompleted) return new PluginLandingCardEntry(registration, card, context, null, Failed: true);
            }

            (reading, started) = StartOrJoinLandingRead(card, active, context, timeout);
        }

        try
        {
            // The wait ends at the limit even when the plugin ignores its token: one card never holds the page.
            var shown = await reading.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return shown is null ? null : new PluginLandingCardEntry(registration, card, context, shown, Failed: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The plugin stopped while it was asked: it has no card any more.
            if (active.RuntimeContext.LifetimeCancellationToken.IsCancellationRequested) return null;
            // The failure of one call of the plugin is told once, by the reading that made the call, not by each one that waited for it.
            if (started)
            {
                LogCallbackFailure(active, "Landing card callback failed.", ex);
                AddDiagnostic(CreateCallbackDiagnostic(registration, ex is TimeoutException or OperationCanceledException ? "Landing card callback did not answer in time." : "Landing card callback failed.", ex));
            }

            return new PluginLandingCardEntry(registration, card, context, null, Failed: true);
        }
    }

    // The call of a card for a space that is running, or a new one. A card is asked once at a time for the same space and project, so a
    // handler that never returns holds one thread and one token, however often the page asks. The call ends with its time limit or with
    // its plugin, not with the reading that started it, since other readings may wait for it.
    private (Task<PluginLandingCard?> Reading, bool Started) StartOrJoinLandingRead(PluginLandingCardContribution card, ActivePluginInstance active, PluginLandingCardContext context, TimeSpan timeout)
    {
        var reads = _landingReads.GetValue(card, static _ => new LandingReads());
        lock (reads.Gate)
        {
            // A call that ended is not an answer to a new question, whether or not it was taken out of the table yet.
            if (reads.Running.TryGetValue(context, out var running) && !running.IsCompleted) return (running, false);
            var limit = CancellationTokenSource.CreateLinkedTokenSource(active.RuntimeContext.LifetimeCancellationToken);
            limit.CancelAfter(timeout);
            var reading = Task.Run(() => card.GetCard(context, limit.Token).AsTask(), CancellationToken.None);
            reads.Running[context] = reading;
            // The token is the plugin's for as long as its handler runs: a handler that outlives the wait is not left with a disposed source,
            // and what it throws afterwards is looked at here, since nobody may wait for it any more.
            _ = reading.ContinueWith(task =>
            {
                _ = task.Exception;
                limit.Dispose();
                lock (reads.Gate)
                {
                    if (reads.Running.TryGetValue(context, out var current) && ReferenceEquals(current, task)) reads.Running.Remove(context);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return (reading, true);
        }
    }

    private sealed class LandingReads
    {
        public Lock Gate { get; } = new();

        public Dictionary<PluginLandingCardContext, Task<PluginLandingCard?>> Running { get; } = [];
    }

    /// <summary>Creates the content of a UI region with the contribution each one comes from.</summary>
    /// <param name="activePlugins">Active plugins used to build operation contexts.</param>
    /// <param name="region">The region to read.</param>
    /// <param name="options">Operation options.</param>
    /// <returns>The content in contribution order; a contribution that returns no content is left out.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="activePlugins"/> is null.</exception>
    public IReadOnlyList<PluginContentEntry> CreateContentEntries(IReadOnlyList<ActivePluginInstance> activePlugins, PluginUiRegion region, PluginAdapterOperationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        if (IsHeadlessOrNonInteractive(options)) return [];
        return PluginUiContentRouting.CreateContent<ActivePluginInstance, PluginContentEntry>(GetRegistrations(PluginPoint.Ui, options), region, options,
            registration => TryGetActivePlugin(activePlugins, registration, out var active) ? active : null,
            (registration, content, active) => content.CreateContent(CreateVisualContext(active, options, region, default)) is { } result
                ? new PluginContentEntry(registration, result) : null);
    }

    /// <summary>Searches the items of a prompt picker.</summary>
    /// <param name="activePlugins">Active plugins used to build operation contexts.</param>
    /// <param name="contribution">The prompt picker contribution.</param>
    /// <param name="query">The text typed after the trigger character.</param>
    /// <param name="options">Operation options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The items and diagnostics; no item when the contribution is not registered or its plugin is not active.</returns>
    /// <exception cref="ArgumentNullException">Thrown when a required argument is null.</exception>
    public async ValueTask<(IReadOnlyList<PluginPromptPickerItem> Items, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> SearchPromptPickerAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        PluginPromptPickerContribution contribution,
        string query,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentNullException.ThrowIfNull(contribution);
        ArgumentNullException.ThrowIfNull(query);
        var diagnostics = new List<PluginRuntimeDiagnostic>();
        foreach (var registration in GetRegistrations(PluginPoint.PromptPicker, options))
        {
            if (!ReferenceEquals(registration.Contribution, contribution) || !TryGetActivePlugin(activePlugins, registration, out var active))
            {
                continue;
            }

            try
            {
                var context = new PluginPromptPickerContext
                {
                    Plugin = active.Descriptor,
                    Services = active.RuntimeContext.Services,
                    Scope = active.RuntimeContext.Scope,
                    ScopeProjectId = active.RuntimeContext.ScopeProjectId,
                    ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
                    ProjectId = options?.ProjectId,
                    ProjectPath = options?.ProjectPath,
                    SessionId = options?.SessionId,
                    ProviderId = options?.ProviderId,
                    Model = options?.Model,
                    CancellationToken = cancellationToken,
                    Query = query,
                };
                var items = await contribution.SearchAsync(context, cancellationToken).ConfigureAwait(false);
                context.Invalidate();
                return (items ?? [], diagnostics);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogCallbackFailure(active, "Prompt picker contribution failed.", ex);
                diagnostics.Add(AddDiagnostic(CreateCallbackDiagnostic(registration, "Prompt picker contribution failed.", ex)));
                return ([], diagnostics);
            }
        }

        return ([], diagnostics);
    }

    /// <summary>Creates portable content from applicable UI-region contributions.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="activePlugins"/> is null.</exception>
    public IReadOnlyList<PluginRenderResult> CreateContent(IReadOnlyList<ActivePluginInstance> activePlugins, PluginUiRegion region, PluginAdapterOperationOptions? options = null)
        => CreateContent(activePlugins, region, static (content, createContext) =>
        {
            ArgumentNullException.ThrowIfNull(content.CreateContent);
            return content.CreateContent(createContext());
        }, options);

    /// <summary>Materializes applicable content through the shared traversal, with lazy operation context creation.</summary>
    /// <typeparam name="TResult">The frontend's materialized result type.</typeparam>
    /// <exception cref="ArgumentNullException">Thrown when the active plugins or materializer is null.</exception>
    public IReadOnlyList<TResult> CreateContent<TResult>(IReadOnlyList<ActivePluginInstance> activePlugins, PluginUiRegion region,
        Func<PluginContentContribution, Func<PluginVisualContext>, TResult?> materialize, PluginAdapterOperationOptions? options = null) where TResult : class
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentNullException.ThrowIfNull(materialize);
        if (IsHeadlessOrNonInteractive(options)) return [];
        return PluginUiContentRouting.CreateContent<ActivePluginInstance, TResult>(GetRegistrations(PluginPoint.Ui, options), region, options,
            registration => TryGetActivePlugin(activePlugins, registration, out var active) ? active : null,
            (_, content, active) => materialize(content, () => CreateVisualContext(active, options, region, default)));
    }

    /// <summary>Runs renderer contributions for a UI region/target and returns rendered payloads.</summary>
    public async ValueTask<(IReadOnlyList<PluginRenderResult> Results, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> RenderAsync(
        IReadOnlyList<ActivePluginInstance> activePlugins,
        PluginUiRegion region,
        string? target,
        object? payload,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
        => await RenderAsync(activePlugins, region, target, payload, static (renderer, context, token) => renderer.Renderer(context, token), options, cancellationToken).ConfigureAwait(false);

    /// <summary>Invokes frontend renderer selection through the shared traversal and diagnostic policy.</summary>
    /// <typeparam name="TResult">The frontend's renderer result type.</typeparam>
    /// <exception cref="ArgumentNullException">Thrown when the active plugins or renderer selector is null.</exception>
    public ValueTask<(IReadOnlyList<TResult> Results, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> RenderAsync<TResult>(
        IReadOnlyList<ActivePluginInstance> activePlugins, PluginUiRegion region, string? target, object? payload,
        Func<PluginRendererContribution, PluginRendererContext, CancellationToken, ValueTask<TResult?>> render,
        PluginAdapterOperationOptions? options = null, CancellationToken cancellationToken = default) where TResult : class
    {
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentNullException.ThrowIfNull(render);
        if (IsHeadlessOrNonInteractive(options)) return ValueTask.FromResult<(IReadOnlyList<TResult>, IReadOnlyList<PluginRuntimeDiagnostic>)>(([], []));
        return PluginUiContentRouting.RenderAsync<ActivePluginInstance, TResult>(GetRegistrations(PluginPoint.Ui, options), region, target, options,
            registration => TryGetActivePlugin(activePlugins, registration, out var active)
                ? (active, CreateRendererContext(active, options, target, payload, cancellationToken)) : null,
            render,
            (registration, active, ex) =>
            {
                LogCallbackFailure(active, "Renderer contribution failed.", ex);
                return AddDiagnostic(CreateCallbackDiagnostic(registration, "Renderer contribution failed.", ex));
            }, cancellationToken);
    }

    private IReadOnlyList<PluginContributionRegistration> GetRegistrations(PluginPoint point, PluginAdapterOperationOptions? options)
        => _registry.GetSnapshot()
            .Where(registration => registration.Handle.Point == point && PluginContributionRegistry.AppliesToProject(registration, options?.ProjectId, options?.ProjectPath))
            .ToArray();

    private static bool TryGetActivePlugin(IReadOnlyList<ActivePluginInstance> activePlugins, PluginContributionRegistration registration, out ActivePluginInstance active)
    {
        active = activePlugins.FirstOrDefault(plugin => string.Equals(plugin.Descriptor.RuntimeKey, registration.Handle.PluginRuntimeKey, StringComparison.Ordinal))!;
        return active is not null && active.Instance is not null && active.RuntimeContext.IsValid;
    }

    private static IEnumerable<ActivePluginInstance> GetApplicableActivePlugins(IReadOnlyList<ActivePluginInstance> activePlugins, PluginAdapterOperationOptions? options)
        => activePlugins.Where(plugin => plugin.Instance is not null && plugin.RuntimeContext.IsValid && plugin.RuntimeContext.AppliesToProject(options?.ProjectId, options?.ProjectPath));

    private static bool ToolApplies(PluginToolActivationPolicy policy, PluginAdapterOperationOptions? options)
    {
        if (policy.RequiresCodeAltaManagedProvider && options?.IsCodeAltaManagedProvider != true)
        {
            return false;
        }

        if (policy.ProviderNames.Count > 0 && !policy.ProviderNames.Contains(options?.ProviderId ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (policy.ProviderFamilies.Count > 0 && !(options?.ProviderFamilies ?? []).Intersect(policy.ProviderFamilies, StringComparer.OrdinalIgnoreCase).Any())
        {
            return false;
        }

        return true;
    }

    private static bool InstructionProcessorApplies(PluginInstructionProcessingTarget target, PluginInstructionProcessingStages stage, PluginAdapterOperationOptions? options)
    {
        if ((target.Stages & stage) == 0 || target.Channels == PluginInstructionChannels.None)
        {
            return false;
        }

        if (target.RequiresCodeAltaManagedProvider && options?.IsCodeAltaManagedProvider != true)
        {
            return false;
        }

        if (target.ProviderIds.Count > 0 && !target.ProviderIds.Contains(options?.ProviderId ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (target.ProviderFamilies.Count > 0 && !(options?.ProviderFamilies ?? []).Intersect(target.ProviderFamilies, StringComparer.OrdinalIgnoreCase).Any())
        {
            return false;
        }

        if (target.ModelIds.Count > 0 && !target.ModelIds.Contains(options?.Model ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsHeadlessOrNonInteractive(PluginAdapterOperationOptions? options)
        => options is not null && (options.IsHeadless || !options.HasInteractiveUi);

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
        active.RuntimeContext.Logger.Error(exception, message);
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

    private static IReadOnlyList<string> GetChangedInstructionChannels(string? beforeSystem, string? beforeDeveloper, string? afterSystem, string? afterDeveloper)
    {
        var channels = new List<string>(2);
        if (!string.Equals(beforeSystem, afterSystem, StringComparison.Ordinal))
        {
            channels.Add("system");
        }

        if (!string.Equals(beforeDeveloper, afterDeveloper, StringComparison.Ordinal))
        {
            channels.Add("developer");
        }

        return channels;
    }

    private static PluginInstructionTransformationRecord CreateTransformationRecord(
        PluginContributionRegistration registration,
        PluginInstructionProcessorContribution processor,
        PluginInstructionProcessingStages stage,
        PluginInstructionProcessingResult result,
        IReadOnlyList<string> changedChannels,
        string? systemMessage,
        string? developerInstructions)
        => new()
        {
            PluginRuntimeKey = registration.Handle.PluginRuntimeKey,
            RuntimeContributionKey = registration.Handle.RuntimeContributionKey,
            NaturalName = registration.Handle.NaturalName,
            Order = processor.Order,
            Stage = stage,
            Disposition = result.Disposition,
            ChangedChannels = changedChannels,
            ChangeSummary = result.ChangeSummary,
            ResultInstructionHash = CreateInstructionSnapshot(systemMessage, developerInstructions).InstructionHash,
            Metadata = result.Metadata,
        };

    private static PluginInstructionSnapshot CreateInstructionSnapshot(string? systemMessage, string? developerInstructions)
    {
        var systemChars = systemMessage?.Length ?? 0;
        var developerChars = developerInstructions?.Length ?? 0;
        return new PluginInstructionSnapshot
        {
            SystemMessage = systemMessage,
            DeveloperInstructions = developerInstructions,
            InstructionHash = HashText(string.Join("\n---\n", systemMessage ?? string.Empty, developerInstructions ?? string.Empty, "native-system-and-developer")),
            SystemCharacterCount = systemChars,
            DeveloperCharacterCount = developerChars,
            SystemApproxTokens = EstimateApproxTokens(systemChars),
            DeveloperApproxTokens = EstimateApproxTokens(developerChars),
        };
    }

    private static int EstimateApproxTokens(int characterCount)
        => characterCount == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(characterCount / 4.0));

    private static string HashText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

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

    private static PluginCommandContext CreateCommandContext(ActivePluginInstance active, PluginAdapterOperationOptions? options, CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options?.ProjectId,
            ProjectPath = options?.ProjectPath,
            SessionId = options?.SessionId,
            RunId = options?.RunId,
            ProviderId = options?.ProviderId,
            Model = options?.Model,
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
            SessionId = options?.SessionId,
            RunId = options?.RunId,
            ProviderId = options?.ProviderId,
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
            SessionId = options?.SessionId,
            RunId = options?.RunId,
            ProviderId = options?.ProviderId,
            Model = options?.Model,
            Text = text,
            Attachments = attachments,
            IsCodeAltaManagedProvider = options?.IsCodeAltaManagedProvider ?? false,
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
            SessionId = options?.SessionId,
            RunId = options?.RunId,
            ProviderId = options?.ProviderId,
            Model = options?.Model,
            Channel = channel,
            SupportsDirectInjection = supportsDirectInjection,
            CancellationToken = cancellationToken,
        };

    private static PluginInstructionProcessingContext CreateInstructionProcessingContext(
        ActivePluginInstance active,
        PluginInstructionProcessingContext template,
        PluginAdapterOperationOptions? options,
        string? systemMessage,
        string? developerInstructions,
        IReadOnlyList<PluginInstructionTransformationRecord> transformations,
        CancellationToken cancellationToken)
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
            RunId = options?.RunId ?? template.RunId,
            ProviderId = options?.ProviderId ?? template.ProviderId,
            Model = options?.Model ?? template.Model,
            Stage = template.Stage,
            Purpose = template.Purpose,
            Instructions = CreateInstructionSnapshot(systemMessage, developerInstructions),
            Manifest = template.Manifest,
            Parts = template.Parts,
            ActiveToolNames = template.ActiveToolNames,
            PriorTransformations = transformations.ToArray(),
            Metadata = template.Metadata,
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
            SessionId = options?.SessionId ?? template.SessionId,
            RunId = options?.RunId ?? template.RunId,
            ProviderId = options?.ProviderId ?? template.ProviderId,
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
            SessionId = options?.SessionId ?? template.SessionId,
            RunId = options?.RunId ?? template.RunId,
            ProviderId = options?.ProviderId ?? template.ProviderId,
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
            SessionId = options?.SessionId ?? template.SessionId,
            RunId = options?.RunId ?? template.RunId,
            ProviderId = options?.ProviderId ?? template.ProviderId,
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
            SessionId = options?.SessionId ?? template.SessionId,
            RunId = options?.RunId ?? template.RunId,
            ProviderId = options?.ProviderId ?? template.ProviderId,
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
            SessionId = options?.SessionId,
            RunId = options?.RunId,
            ProviderId = options?.ProviderId,
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
            SessionId = options?.SessionId,
            RunId = options?.RunId,
            ProviderId = options?.ProviderId,
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
            SessionId = options?.SessionId,
            RunId = options?.RunId,
            ProviderId = options?.ProviderId,
            Model = options?.Model,
            Target = target,
            Payload = payload,
            CancellationToken = cancellationToken,
        };

}
