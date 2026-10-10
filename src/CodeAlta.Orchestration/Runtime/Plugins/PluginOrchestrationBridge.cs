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
    private static readonly AsyncLocal<PluginAdapterOperationOptions?> ToolOperation = new();
    private readonly PluginContributionAdapterService _adapter;
    private readonly Func<IReadOnlyList<ActivePluginInstance>> _getActivePlugins;

    /// <summary>
    /// Gets the scope (project, session, provider) of the plugin tool call running on the current execution
    /// flow, or <see langword="null"/> outside one.
    /// </summary>
    /// <remarks>
    /// A tool handler only receives its invocation. A host that runs several sessions at once has no single
    /// selected project, so its plugin services answer "the selected project" from this scope.
    /// </remarks>
    public static PluginAdapterOperationOptions? CurrentToolOperation => ToolOperation.Value;

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
    /// Runs final instruction processor plugin hooks for orchestration.
    /// </summary>
    /// <param name="template">The instruction processing context template.</param>
    /// <param name="options">Operation scope options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The instruction processing adapter result.</returns>
    public ValueTask<PluginInstructionProcessingAdapterResult> ProcessInstructionsAsync(
        PluginInstructionProcessingContext template,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
        => _adapter.ProcessInstructionsAsync(_getActivePlugins(), template, MarkHeadless(options), cancellationToken);

    /// <summary>
    /// Gets plugin-contributed agent tools applicable to an orchestration scope.
    /// </summary>
    /// <param name="options">Operation scope options.</param>
    /// <returns>Applicable agent tool contributions.</returns>
    public IReadOnlyList<PluginAgentToolContribution> GetAgentTools(PluginAdapterOperationOptions? options = null)
        => _adapter.GetAgentTools(MarkHeadless(options));

    /// <summary>
    /// Returns the agent tools that plugins contribute to a scope, as a run gets them: each one runs the tool call
    /// hooks of the plugins. A turn that is running registers them to use, at once, the tools of a plugin that
    /// was just started or built again.
    /// </summary>
    /// <param name="options">Operation scope options.</param>
    /// <param name="pluginRuntimeKeys">The plugins whose tools are returned; null for those of every plugin.</param>
    /// <returns>The tools, ready for a run.</returns>
    public IReadOnlyList<AgentToolDefinition> CreateAgentTools(PluginAdapterOperationOptions? options = null, IReadOnlyCollection<string>? pluginRuntimeKeys = null)
    {
        var effectiveOptions = MarkHeadless(options);
        // The tools the scope is given, then those of the plugins that were asked for.
        var applicable = _adapter.GetAgentTools(effectiveOptions);
        return [.. _adapter.GetContributions<PluginAgentToolContribution>(PluginPoint.AgentTool, effectiveOptions)
            .Where(registration => pluginRuntimeKeys is null || pluginRuntimeKeys.Contains(registration.Handle.PluginRuntimeKey))
            .Select(static registration => registration.Contribution)
            .OfType<PluginAgentToolContribution>()
            .Where(tool => applicable.Contains(tool))
            .Select(tool => WrapPluginTool(tool.Definition, effectiveOptions, contributed: true))];
    }

    /// <summary>
    /// Builds per-run plugin prompt and tool augmentation for a headless orchestration run.
    /// </summary>
    /// <param name="executionOptions">The current session execution options.</param>
    /// <param name="input">The current agent input.</param>
    /// <param name="options">Operation scope options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plugin run augmentation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when an argument is <see langword="null" />.</exception>
    public Task<PluginAgentRunAugmentation> BuildAgentRunAugmentationAsync(
        SessionExecutionOptions executionOptions,
        AgentInput input,
        PluginAdapterOperationOptions? options = null,
        CancellationToken cancellationToken = default)
        => BuildAugmentationAsync(executionOptions, input, options, isRun: true, cancellationToken);

    // A session that is only being created gets what plugins contribute to every run of its scope (their
    // tools and prompt parts); the before-run hooks wait for a run.
    private async Task<PluginAgentRunAugmentation> BuildAugmentationAsync(
        SessionExecutionOptions executionOptions,
        AgentInput input,
        PluginAdapterOperationOptions? options,
        bool isRun,
        CancellationToken cancellationToken)
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
        var before = isRun
            ? (await _adapter.BeforeAgentRunAsync(activePlugins, beforeTemplate, effectiveOptions, cancellationToken).ConfigureAwait(false)).Result
            : PluginBeforeAgentRunResult.Empty;
        if (before.Cancel)
        {
            return new PluginAgentRunAugmentation
            {
                CancelReason = before.CancelReason ?? "Plugin cancelled the agent run.",
            };
        }

        activeTools = MergeRunTools(activeTools, before.AdditionalTools, effectiveOptions);
        var systemParts = await _adapter.BuildSystemPromptPartsAsync(activePlugins, PluginPromptChannel.System, effectiveOptions.IsCodeAltaManagedProvider, effectiveOptions, cancellationToken).ConfigureAwait(false);
        var developerParts = await _adapter.BuildSystemPromptPartsAsync(activePlugins, PluginPromptChannel.Developer, effectiveOptions.IsCodeAltaManagedProvider, effectiveOptions, cancellationToken).ConfigureAwait(false);
        var systemText = await BuildPromptTextAsync(
            systemParts.Parts,
            before.TemporaryPromptContributions.Where(static part => part.Channel == PluginPromptChannel.System),
            seed,
            effectiveOptions,
            PluginPromptChannel.System,
            cancellationToken).ConfigureAwait(false);
        var developerText = await BuildPromptTextAsync(
            developerParts.Parts,
            before.TemporaryPromptContributions.Where(static part => part.Channel == PluginPromptChannel.Developer),
            seed,
            effectiveOptions,
            PluginPromptChannel.Developer,
            cancellationToken).ConfigureAwait(false);
        var instructionProcessor = _adapter.GetContributions<PluginInstructionProcessorContribution>(PluginPoint.InstructionProcessor, effectiveOptions).Count == 0
            ? null
            : new SessionInstructionProcessor((request, token) => ProcessFinalInstructionsAsync(request, effectiveOptions, token));

        return new PluginAgentRunAugmentation
        {
            Input = AppendAdditionalMessages(input, before.AdditionalMessages),
            Tools = activeTools,
            AdditionalSystemMessage = systemText,
            AdditionalDeveloperInstructions = developerText,
            InstructionProcessor = instructionProcessor,
            PreferredToolNames = before.PreferredToolNames,
        };
    }

    /// <summary>
    /// Builds the plugin augmentation of a session run and applies it to the run's execution options.
    /// </summary>
    /// <remarks>
    /// Every host path that sends to a session calls this with the same arguments for the same session, so
    /// the paths produce equal options: the runtime replaces a session's attachment when its tools or
    /// instructions differ from the previous send.
    /// </remarks>
    /// <param name="executionOptions">The execution options of the run, before plugins.</param>
    /// <param name="input">The input of the run.</param>
    /// <param name="projectId">The project of the session, or <see langword="null"/> for a global session.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The options and input to run with, or the reason a plugin cancelled the run.</returns>
    /// <exception cref="ArgumentNullException">Thrown when the options or the input are <see langword="null" />.</exception>
    public async Task<PluginAugmentedRun> AugmentRunAsync(
        SessionExecutionOptions executionOptions,
        AgentInput input,
        string? projectId,
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);
        var augmentation = await BuildAugmentationAsync(executionOptions, input, ScopeOf(executionOptions, projectId, sessionId), isRun: true, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(augmentation.CancelReason)
            ? new PluginAugmentedRun(augmentation.ApplyTo(executionOptions), augmentation.Input ?? input)
            : new PluginAugmentedRun(executionOptions, input, augmentation.CancelReason);
    }

    /// <summary>
    /// Returns the execution options of a session that is being created, with what plugins contribute to
    /// every run of its scope: the options its first run has when no before-run hook adds anything, so that
    /// run keeps the attachment the creation made.
    /// </summary>
    /// <remarks>No before-run hook is called: nothing runs yet.</remarks>
    /// <param name="executionOptions">The execution options of the new session, before plugins.</param>
    /// <param name="projectId">The project of the session, or <see langword="null"/> for a global session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The options to create the session with.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="executionOptions"/> is <see langword="null" />.</exception>
    public async Task<SessionExecutionOptions> AugmentNewSessionAsync(
        SessionExecutionOptions executionOptions,
        string? projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);
        var augmentation = await BuildAugmentationAsync(executionOptions, new AgentInput([]), ScopeOf(executionOptions, projectId, sessionId: null), isRun: false, cancellationToken).ConfigureAwait(false);
        return augmentation.ApplyTo(executionOptions);
    }

    /// <summary>
    /// Returns the execution options of an existing session that is attached without a run (to turn on its remote
    /// control), with what plugins contribute to every run of its scope: the options its next run has when no
    /// before-run hook adds anything, so that run keeps this attachment.
    /// </summary>
    /// <remarks>No before-run hook is called: nothing runs yet.</remarks>
    /// <param name="executionOptions">The execution options of the session, before plugins.</param>
    /// <param name="projectId">The project of the session, or <see langword="null"/> for a global session.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The options to attach the session with.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="executionOptions"/> is <see langword="null" />.</exception>
    public async Task<SessionExecutionOptions> AugmentAttachmentAsync(
        SessionExecutionOptions executionOptions,
        string? projectId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);
        var augmentation = await BuildAugmentationAsync(executionOptions, new AgentInput([]), ScopeOf(executionOptions, projectId, sessionId), isRun: false, cancellationToken).ConfigureAwait(false);
        return augmentation.ApplyTo(executionOptions);
    }

    private static PluginAdapterOperationOptions ScopeOf(SessionExecutionOptions executionOptions, string? projectId, string? sessionId)
        => new()
        {
            ProjectId = projectId,
            ProjectPath = executionOptions.WorkingDirectory,
            SessionId = sessionId,
            ProviderId = executionOptions.ProviderId.Value,
            Model = executionOptions.Model,
            IsCodeAltaManagedProvider = IsCodeAltaManagedProvider(executionOptions.ProviderId),
        };

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
            tools.Add(WrapPluginTool(contribution.Definition, options, contributed: true));
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

    // A session keeps the tools it was given while they look the same (name, description, parameters). A plugin
    // that is built again has a new tool that looks the same: a contributed tool is the one its plugin has when it
    // is called, never the tool of a version that was stopped.
    private AgentToolDefinition WrapPluginTool(AgentToolDefinition given, PluginAdapterOperationOptions options, bool contributed = false)
    {
        return given with
        {
            Handler = async (invocation, cancellationToken) =>
            {
                // Scoped to this call: an async method's changes to the flow do not reach its caller.
                ToolOperation.Value = options;
                var definition = contributed ? CurrentTool(given, options) : given;
                if (definition is null)
                {
                    var stopped = $"The plugin that provides the tool '{given.Spec.Name}' is not running.";
                    return new AgentToolResult(false, [new AgentToolResultItem.Text(stopped)], stopped);
                }

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

    private AgentToolDefinition? CurrentTool(AgentToolDefinition given, PluginAdapterOperationOptions options)
    {
        AgentToolDefinition? named = null;
        foreach (var contribution in _adapter.GetAgentTools(options))
        {
            if (ReferenceEquals(contribution.Definition, given)) return given;
            if (named is null && string.Equals(contribution.Definition.Spec.Name, given.Spec.Name, StringComparison.Ordinal)) named = contribution.Definition;
        }

        return named;
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

    // Providers that run their own agent loop (Codex, Copilot) take no direct prompt injection from plugins.
    private static bool IsCodeAltaManagedProvider(ModelProviderId providerId)
        => !string.Equals(providerId.Value, ModelProviderIds.Codex.Value, StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(providerId.Value, ModelProviderIds.Copilot.Value, StringComparison.OrdinalIgnoreCase);

    private static PluginAdapterOperationOptions MarkHeadless(PluginAdapterOperationOptions? options)
        => options is null
            ? new PluginAdapterOperationOptions { IsHeadless = true, HasInteractiveUi = false }
            : options with { IsHeadless = true, HasInteractiveUi = false };

    private ValueTask<SessionInstructionProcessingResult> ProcessFinalInstructionsAsync(
        SessionInstructionProcessingRequest request,
        PluginAdapterOperationOptions options,
        CancellationToken cancellationToken)
        => PluginInstructionProcessingRunner.ProcessFinalInstructionsAsync(_adapter, _getActivePlugins(), request, options, cancellationToken);
}

/// <summary>
/// Shared adapter for invoking final instruction processors from orchestration hosts.
/// </summary>
public static class PluginInstructionProcessingRunner
{
    /// <summary>
    /// Processes final instructions through active plugin processors.
    /// </summary>
    /// <param name="adapter">The plugin adapter service.</param>
    /// <param name="activePlugins">Active plugins.</param>
    /// <param name="request">The session instruction processing request.</param>
    /// <param name="options">Plugin operation options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The processed session instructions.</returns>
    public static async ValueTask<SessionInstructionProcessingResult> ProcessFinalInstructionsAsync(
        PluginContributionAdapterService adapter,
        IReadOnlyList<ActivePluginInstance> activePlugins,
        SessionInstructionProcessingRequest request,
        PluginAdapterOperationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(activePlugins);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        if (activePlugins.Count == 0)
        {
            return new SessionInstructionProcessingResult
            {
                SystemMessage = request.SystemMessage,
                DeveloperInstructions = request.DeveloperInstructions,
            };
        }

        var seed = activePlugins[0];
        var template = new PluginInstructionProcessingContext
        {
            Plugin = seed.Descriptor,
            Services = seed.RuntimeContext.Services,
            Scope = seed.RuntimeContext.Scope,
            ScopeProjectId = seed.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = seed.RuntimeContext.ScopeProjectPath,
            ProjectId = request.ProjectId ?? options.ProjectId,
            ProjectPath = request.ProjectPath ?? options.ProjectPath,
            SessionId = request.SessionId ?? options.SessionId,
            ProviderId = request.ProviderId ?? options.ProviderId,
            Model = request.Model ?? options.Model,
            Stage = PluginInstructionProcessingStages.FinalBeforeProviderRequest,
            Instructions = new PluginInstructionSnapshot
            {
                SystemMessage = request.SystemMessage,
                DeveloperInstructions = request.DeveloperInstructions,
                InstructionHash = string.Empty,
            },
            Manifest = new PluginInstructionManifestView { AgentPromptName = request.Manifest.TryGetValue("agentPromptId", out var agentPromptId) ? agentPromptId : null },
            Metadata = request.Manifest,
            ActiveToolNames = request.ActiveToolNames,
        };
        var result = await adapter.ProcessInstructionsAsync(activePlugins, template, options, cancellationToken).ConfigureAwait(false);
        return new SessionInstructionProcessingResult
        {
            SystemMessage = result.SystemMessage,
            DeveloperInstructions = result.DeveloperInstructions,
            CancelReason = result.CancelReason,
            Transformations = result.Transformations.Select(ToAgentTransformation).ToArray(),
        };
    }

    private static AgentInstructionTransformationInfo ToAgentTransformation(PluginInstructionTransformationRecord record)
        => new()
        {
            PluginRuntimeKey = record.PluginRuntimeKey,
            RuntimeContributionKey = record.RuntimeContributionKey,
            NaturalName = record.NaturalName,
            Order = record.Order,
            Stage = record.Stage.ToString(),
            Disposition = record.Disposition.ToString(),
            ChangedChannels = record.ChangedChannels,
            ChangeSummary = record.ChangeSummary,
            ResultInstructionHash = record.ResultInstructionHash,
            Metadata = record.Metadata,
        };
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

    /// <summary>Gets the final instruction processor callback.</summary>
    public SessionInstructionProcessor? InstructionProcessor { get; init; }

    /// <summary>Gets preferred tool names for the run.</summary>
    public IReadOnlyList<string> PreferredToolNames { get; init; } = [];

    /// <summary>Gets the plugin cancellation reason, when the run was cancelled.</summary>
    public string? CancelReason { get; init; }

    /// <summary>Returns execution options with this augmentation's tools, instructions and hooks added.</summary>
    /// <param name="source">The execution options of the run, before plugins.</param>
    /// <returns>The augmented options; what this augmentation does not carry is kept from <paramref name="source"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> is <see langword="null" />.</exception>
    public SessionExecutionOptions ApplyTo(SessionExecutionOptions source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new()
        {
            ProviderId = source.ProviderId,
            ProviderKey = source.ProviderKey,
            WorkingDirectory = source.WorkingDirectory,
            ProjectRoots = source.ProjectRoots,
            Model = source.Model,
            ReasoningEffort = source.ReasoningEffort,
            AgentPromptId = source.AgentPromptId,
            Tools = Tools ?? source.Tools,
            AdditionalSystemMessage = AppendPromptText(source.AdditionalSystemMessage, AdditionalSystemMessage),
            AdditionalDeveloperInstructions = AppendPromptText(source.AdditionalDeveloperInstructions, AdditionalDeveloperInstructions),
            PreferredToolNames = source.PreferredToolNames.Concat(PreferredToolNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            InstructionProcessor = InstructionProcessor ?? source.InstructionProcessor,
            OnPermissionRequest = source.OnPermissionRequest,
            OnUserInputRequest = source.OnUserInputRequest,
        };
    }

    private static string? AppendPromptText(string? existing, string? additional)
    {
        if (string.IsNullOrWhiteSpace(existing))
        {
            return string.IsNullOrWhiteSpace(additional) ? null : additional;
        }

        return string.IsNullOrWhiteSpace(additional) ? existing : existing.TrimEnd() + "\n\n" + additional.Trim();
    }
}

/// <summary>The options and input of a session run after plugins, or the reason a plugin cancelled it.</summary>
/// <param name="ExecutionOptions">The execution options to run with; the original ones when cancelled.</param>
/// <param name="Input">The input to run with; the original one when cancelled.</param>
/// <param name="CancelReason">Why a plugin cancelled the run, or <see langword="null"/>.</param>
public sealed record PluginAugmentedRun(SessionExecutionOptions ExecutionOptions, AgentInput Input, string? CancelReason = null);
