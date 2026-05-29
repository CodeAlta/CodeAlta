using System.Text;
using CodeAlta.Agent;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Orchestration.Runtime.Plugins;

/// <summary>
/// Headless bridge that exposes plugin runtime contributions to orchestration pipelines.
/// </summary>
public sealed class PluginOrchestrationBridge
{
    private readonly PluginContributionAdapterService _adapter;
    private readonly Func<IReadOnlyList<ActivePluginInstance>> _getActivePlugins;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginOrchestrationBridge"/> class.
    /// </summary>
    /// <param name="adapter">The plugin contribution adapter.</param>
    /// <param name="getActivePlugins">Gets the current active plugin snapshot.</param>
    /// <exception cref="ArgumentNullException">Thrown when an argument is <see langword="null"/>.</exception>
    public PluginOrchestrationBridge(
        PluginContributionAdapterService adapter,
        Func<IReadOnlyList<ActivePluginInstance>> getActivePlugins)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(getActivePlugins);
        _adapter = adapter;
        _getActivePlugins = getActivePlugins;
    }

    /// <summary>
    /// Runs prompt submission hooks for a headless orchestration prompt.
    /// </summary>
    /// <param name="text">The prompt text.</param>
    /// <param name="attachments">Prompt attachments.</param>
    /// <param name="options">Operation scope options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plugin prompt adapter result.</returns>
    public ValueTask<PluginPromptAdapterResult> ProcessPromptSubmittingAsync(
        string text,
        IReadOnlyList<PluginPromptAttachment>? attachments = null,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
        => _adapter.ProcessPromptSubmittingAsync(
            _getActivePlugins(),
            text,
            attachments,
            MarkHeadless(options),
            cancellationToken);

    /// <summary>
    /// Runs before-agent-run plugin hooks for orchestration.
    /// </summary>
    /// <param name="template">The before-run context template.</param>
    /// <param name="options">Operation scope options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The aggregated before-run adapter result.</returns>
    public ValueTask<PluginBeforeAgentRunAdapterResult> BeforeAgentRunAsync(
        PluginBeforeAgentRunContext template,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
        => _adapter.BeforeAgentRunAsync(_getActivePlugins(), template, MarkHeadless(options), cancellationToken);

    /// <summary>
    /// Gets plugin-contributed agent tools applicable to an orchestration scope.
    /// </summary>
    /// <param name="options">Operation scope options.</param>
    /// <returns>Applicable agent tool contributions.</returns>
    public IReadOnlyList<PluginAgentToolContribution> GetAgentTools(PluginAdapterOperationOptions? options = null)
        => _adapter.GetAgentTools(MarkHeadless(options));

    /// <summary>
    /// Builds per-run plugin prompt and tool augmentation for a headless orchestration run.
    /// </summary>
    /// <param name="executionOptions">The current session execution options.</param>
    /// <param name="input">The current agent input.</param>
    /// <param name="options">Operation scope options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plugin run augmentation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is <see langword="null" />.</exception>
    public async Task<PluginAgentRunAugmentation> BuildAgentRunAugmentationAsync(
        SessionExecutionOptions executionOptions,
        AgentInput input,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);
        ArgumentNullException.ThrowIfNull(input);

        var activePlugins = _getActivePlugins();
        if (activePlugins.Count == 0)
        {
            return new PluginAgentRunAugmentation();
        }

        var effectiveOptions = MarkHeadless(options);
        var activeTools = MergeTools(executionOptions.Tools, effectiveOptions);
        var seed = activePlugins[0];
        var beforeTemplate = new PluginBeforeAgentRunContext
        {
            Plugin = seed.Descriptor,
            Services = seed.RuntimeContext.Services,
            PromptText = ExtractText(input),
            Input = input,
            ActiveToolNames = (activeTools ?? []).Select(static tool => tool.Spec.Name).ToArray(),
        };
        var before = await _adapter.BeforeAgentRunAsync(activePlugins, beforeTemplate, effectiveOptions, cancellationToken).ConfigureAwait(false);
        if (before.Result.Cancel)
        {
            return new PluginAgentRunAugmentation
            {
                CancelReason = before.Result.CancelReason ?? "Plugin cancelled the agent run.",
            };
        }

        activeTools = MergeRunTools(activeTools, before.Result.AdditionalTools, effectiveOptions);
        var systemParts = await _adapter.BuildSystemPromptPartsAsync(activePlugins, PluginPromptChannel.System, effectiveOptions.IsCodeAltaManagedProvider, effectiveOptions, cancellationToken).ConfigureAwait(false);
        var developerParts = await _adapter.BuildSystemPromptPartsAsync(activePlugins, PluginPromptChannel.Developer, effectiveOptions.IsCodeAltaManagedProvider, effectiveOptions, cancellationToken).ConfigureAwait(false);
        var systemText = await BuildPromptTextAsync(
            systemParts.Parts,
            before.Result.TemporaryPromptContributions.Where(static part => part.Channel == PluginPromptChannel.System),
            seed,
            effectiveOptions,
            PluginPromptChannel.System,
            cancellationToken).ConfigureAwait(false);
        var developerText = await BuildPromptTextAsync(
            developerParts.Parts,
            before.Result.TemporaryPromptContributions.Where(static part => part.Channel == PluginPromptChannel.Developer),
            seed,
            effectiveOptions,
            PluginPromptChannel.Developer,
            cancellationToken).ConfigureAwait(false);

        return new PluginAgentRunAugmentation
        {
            Input = AppendAdditionalMessages(input, before.Result.AdditionalMessages),
            Tools = activeTools,
            AdditionalSystemMessage = systemText,
            AdditionalDeveloperInstructions = developerText,
            PreferredToolNames = before.Result.PreferredToolNames,
        };
    }

    /// <summary>
    /// Gets plugin-contributed transient session event projectors applicable to an orchestration scope.
    /// </summary>
    /// <param name="options">Operation scope options.</param>
    /// <returns>Applicable transient session event projection contributions.</returns>
    public IReadOnlyList<PluginContributionRegistration> GetSessionEventProjectors(PluginAdapterOperationOptions? options = null)
        => _adapter.GetContributions<PluginSessionEventProjectionContribution>(PluginPoint.SessionEventProjection, MarkHeadless(options));

    /// <summary>
    /// Broadcasts an agent event to headless orchestration plugin hooks.
    /// </summary>
    /// <param name="template">The agent event context template.</param>
    /// <param name="options">Operation scope options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Diagnostics raised while observing the event.</returns>
    public ValueTask<IReadOnlyList<PluginRuntimeDiagnostic>> ObserveAgentEventAsync(
        PluginAgentEventContext template,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
        => _adapter.ObserveAgentEventAsync(_getActivePlugins(), template, MarkHeadless(options), cancellationToken);

    /// <summary>
    /// Runs compaction plugin hooks for orchestration.
    /// </summary>
    /// <param name="before">Optional before-compaction context.</param>
    /// <param name="instructions">Optional instruction context.</param>
    /// <param name="reducer">Optional reducer context.</param>
    /// <param name="after">Optional after-compaction context.</param>
    /// <param name="options">Operation scope options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The compaction adapter result.</returns>
    public ValueTask<PluginCompactionAdapterResult> RunCompactionAsync(
        PluginBeforeCompactionContext? before = null,
        PluginCompactionInstructionContext? instructions = null,
        PluginCompactionReducerContext? reducer = null,
        PluginAfterCompactionContext? after = null,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
        => _adapter.RunCompactionAsync(before, instructions, reducer, after, MarkHeadless(options), cancellationToken);

    private IReadOnlyList<AgentToolDefinition>? MergeTools(IReadOnlyList<AgentToolDefinition>? existingTools, PluginAdapterOperationOptions options)
    {
        var tools = new List<AgentToolDefinition>();
        if (existingTools is not null)
        {
            tools.AddRange(existingTools);
        }

        foreach (var contribution in _adapter.GetAgentTools(options))
        {
            tools.Add(WrapPluginTool(contribution.Definition, options));
        }

        return tools.Count == 0 ? null : tools;
    }

    private IReadOnlyList<AgentToolDefinition>? MergeRunTools(
        IReadOnlyList<AgentToolDefinition>? existingTools,
        IReadOnlyList<AgentToolDefinition> additionalTools,
        PluginAdapterOperationOptions options)
    {
        if (additionalTools.Count == 0)
        {
            return existingTools;
        }

        var tools = new List<AgentToolDefinition>();
        var toolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existingTools is not null)
        {
            foreach (var tool in existingTools)
            {
                if (toolNames.Add(tool.Spec.Name))
                {
                    tools.Add(tool);
                }
            }
        }

        foreach (var tool in additionalTools)
        {
            if (toolNames.Add(tool.Spec.Name))
            {
                tools.Add(WrapPluginTool(tool, options));
            }
        }

        return tools.Count == 0 ? null : tools;
    }

    private AgentToolDefinition WrapPluginTool(AgentToolDefinition definition, PluginAdapterOperationOptions options)
    {
        return definition with
        {
            Handler = async (invocation, cancellationToken) =>
            {
                var activePlugins = _getActivePlugins();
                if (activePlugins.Count == 0)
                {
                    return await definition.Handler(invocation, cancellationToken).ConfigureAwait(false);
                }

                var seed = activePlugins[0];
                var call = await _adapter.OnToolCallAsync(activePlugins, new PluginToolCallContext { Plugin = seed.Descriptor, Services = seed.RuntimeContext.Services, Invocation = invocation }, options, cancellationToken).ConfigureAwait(false);
                if (call.Result?.Disposition == PluginToolCallDisposition.Block)
                {
                    return new AgentToolResult(false, [new AgentToolResultItem.Text(call.Result.BlockReason ?? "Plugin blocked tool call.")], call.Result.BlockReason ?? "Plugin blocked tool call.");
                }

                var effectiveInvocation = call.Result?.Disposition == PluginToolCallDisposition.ReplaceArguments && call.Result.ReplacementArguments is { } replacement
                    ? invocation with { Arguments = replacement }
                    : invocation;
                var result = await definition.Handler(effectiveInvocation, cancellationToken).ConfigureAwait(false);
                var toolResult = await _adapter.OnToolResultAsync(activePlugins, new PluginToolResultContext { Plugin = seed.Descriptor, Services = seed.RuntimeContext.Services, Invocation = effectiveInvocation, Result = result }, options, cancellationToken).ConfigureAwait(false);
                return toolResult.Result?.Disposition == PluginToolResultDisposition.Replace && toolResult.Result.ReplacementResult is not null
                    ? toolResult.Result.ReplacementResult
                    : result;
            },
        };
    }

    private static string? ExtractText(AgentInput input)
    {
        var text = string.Join("\n", input.Items.OfType<AgentInputItem.Text>().Select(static item => item.Value));
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static AgentInput AppendAdditionalMessages(AgentInput input, IReadOnlyList<PluginPromptMessage> messages)
    {
        if (messages.Count == 0)
        {
            return input;
        }

        var builder = new StringBuilder();
        builder.AppendLine("Plugin-provided per-turn context:");
        foreach (var message in messages)
        {
            builder.Append("- ").Append(message.Role).Append(": ").AppendLine(message.Content);
        }

        var items = input.Items.Concat([new AgentInputItem.Text(builder.ToString().TrimEnd())]).ToArray();
        return new AgentInput(items);
    }

    private static async Task<string?> BuildPromptTextAsync(
        IEnumerable<PluginPromptPart> parts,
        IEnumerable<PluginSystemPromptContribution> temporaryContributions,
        ActivePluginInstance contextSeed,
        PluginAdapterOperationOptions options,
        PluginPromptChannel channel,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            AppendPromptPart(builder, part.Contribution.Title, part.Content);
        }

        foreach (var contribution in temporaryContributions)
        {
            var context = CreateTemporarySystemPromptContext(contextSeed, options, channel, cancellationToken);
            var content = await contribution.Content(context, cancellationToken).ConfigureAwait(false);
            context.Invalidate();
            AppendPromptPart(builder, contribution.Title, content);
        }

        var text = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static void AppendPromptPart(StringBuilder builder, string? title, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            builder.AppendLine(title);
        }

        builder.AppendLine(content.Trim());
        builder.AppendLine();
    }

    private static PluginSystemPromptContext CreateTemporarySystemPromptContext(
        ActivePluginInstance active,
        PluginAdapterOperationOptions options,
        PluginPromptChannel channel,
        CancellationToken cancellationToken)
        => new()
        {
            Plugin = active.Descriptor,
            Services = active.RuntimeContext.Services,
            Scope = active.RuntimeContext.Scope,
            ScopeProjectId = active.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = active.RuntimeContext.ScopeProjectPath,
            ProjectId = options.ProjectId,
            ProjectPath = options.ProjectPath,
            SessionId = options.SessionId,
            RunId = options.RunId,
            ProviderId = options.ProviderId,
            Model = options.Model,
            Channel = channel,
            SupportsDirectInjection = options.IsCodeAltaManagedProvider,
            CancellationToken = cancellationToken,
        };

    private static PluginAdapterOperationOptions MarkHeadless(PluginAdapterOperationOptions? options)
        => options is null
            ? new PluginAdapterOperationOptions { IsHeadless = true, HasInteractiveUi = false }
            : options with { IsHeadless = true, HasInteractiveUi = false };
}

/// <summary>
/// Describes prompt and tool augmentation produced by plugins for a headless agent run.
/// </summary>
public sealed record PluginAgentRunAugmentation
{
    /// <summary>Gets the augmented input, when plugins appended per-turn context.</summary>
    public AgentInput? Input { get; init; }

    /// <summary>Gets the augmented tool list.</summary>
    public IReadOnlyList<AgentToolDefinition>? Tools { get; init; }

    /// <summary>Gets additional system prompt content.</summary>
    public string? AdditionalSystemMessage { get; init; }

    /// <summary>Gets additional developer instructions.</summary>
    public string? AdditionalDeveloperInstructions { get; init; }

    /// <summary>Gets preferred tool names for the run.</summary>
    public IReadOnlyList<string> PreferredToolNames { get; init; } = [];

    /// <summary>Gets the plugin cancellation reason, when the run was cancelled.</summary>
    public string? CancelReason { get; init; }
}
