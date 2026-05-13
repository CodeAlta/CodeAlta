using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Plugins;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Presentation.Prompting;
using XenoAtom.Terminal.UI;

namespace CodeAlta.App;

internal sealed class PluginHostBridge
{
    private readonly PluginRuntimeManager _runtime;
    private readonly Func<ProjectDescriptor?> _getCurrentProject;
    private readonly PluginFrontendBridge _frontend;
    private readonly PluginAltaServiceBridge? _alta;
    private readonly PluginPromptContributionScope _promptContributionScope = new();

    public PluginHostBridge(PluginRuntimeManager runtime, Func<ProjectDescriptor?> getCurrentProject, PluginAltaServiceBridge? alta = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(getCurrentProject);
        _runtime = runtime;
        _getCurrentProject = getCurrentProject;
        _alta = alta;
        _frontend = new PluginFrontendBridge(runtime, getCurrentProject);
    }

    public PluginFrontendBridge Frontend => _frontend;

    public PluginRuntimeManager Runtime => _runtime;

    public PluginAltaServiceBridge? Alta => _alta;

    public IReadOnlyList<PluginResolvedResourceContribution> GetResources()
        => _frontend.GetResources();

    public IReadOnlyList<PluginCommandContribution> GetCommandContributions()
        => _frontend.GetCommandContributions();

    public IReadOnlyList<PluginStatusItem> GetStatusItems(PluginUiRegion region)
        => _frontend.GetStatusItems(region);

    public IReadOnlyList<Visual> CreateVisuals(PluginUiRegion region)
        => _frontend.CreateVisuals(region);

    public Task<(IReadOnlyList<PluginRenderResult> Results, IReadOnlyList<PluginRuntimeDiagnostic> Diagnostics)> RenderAsync(
        PluginUiRegion region,
        string? target,
        object? payload,
        CancellationToken cancellationToken = default)
        => _frontend.RenderAsync(region, target, payload, cancellationToken);

    public async Task<PromptSubmission?> ProcessPromptSubmittingAsync(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        PromptSubmission prompt,
        bool isCodeAltaManagedBackend,
        CancellationToken cancellationToken)
    {
        var result = await _runtime.Adapter.ProcessPromptSubmittingAsync(
                _runtime.ActivePlugins,
                prompt.Text,
                prompt.Images.Select(static image => new PluginPromptAttachment
                {
                    Kind = PluginPromptAttachmentKind.Image,
                    Path = image.Id,
                    DisplayName = image.Title,
                    MediaType = image.MediaType,
                }).ToArray(),
                CreateOptions(thread, tab, isCodeAltaManagedBackend),
                cancellationToken);

        _promptContributionScope.Add(CreatePromptContributionScopeKey(thread, tab), result.Result.TemporaryPromptContributions);

        if (result.Result.Disposition is PluginPromptDisposition.Cancel or PluginPromptDisposition.Handled)
        {
            return null;
        }

        return ApplyPromptProcessingResult(prompt, result.Result);
    }

    internal static PromptSubmission ApplyPromptProcessingResult(PromptSubmission prompt, PluginPromptResult result)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(result);

        if (result.Disposition == PluginPromptDisposition.Continue)
        {
            return prompt;
        }

        var replacementText = result.ReplacementText ?? prompt.Text;
        var replacementImages = ResolveReplacementPromptImages(prompt.Images, result.ReplacementAttachments, out var imagesChanged);
        if (!imagesChanged && string.Equals(replacementText, prompt.Text, StringComparison.Ordinal))
        {
            return prompt;
        }

        return PromptSubmission.Create(replacementText, replacementImages);
    }

    private static IReadOnlyList<PromptImageAttachment> ResolveReplacementPromptImages(
        IReadOnlyList<PromptImageAttachment> currentImages,
        IReadOnlyList<PluginPromptAttachment> replacementAttachments,
        out bool imagesChanged)
    {
        imagesChanged = false;
        if (currentImages.Count == 0 || replacementAttachments.Count == 0)
        {
            return currentImages;
        }

        var imagesById = currentImages.ToDictionary(static image => image.Id, StringComparer.Ordinal);
        var mappedImages = new List<PromptImageAttachment>(replacementAttachments.Count);
        foreach (var attachment in replacementAttachments)
        {
            if (attachment.Kind != PluginPromptAttachmentKind.Image ||
                string.IsNullOrWhiteSpace(attachment.Path) ||
                !imagesById.TryGetValue(attachment.Path, out var image))
            {
                // Prompt submissions currently only carry pasted image bytes. If a plugin returns
                // another attachment shape, keep the pasted images instead of silently dropping them.
                imagesChanged = false;
                return currentImages;
            }

            if (!string.IsNullOrWhiteSpace(attachment.DisplayName))
            {
                image = image.WithTitle(attachment.DisplayName);
            }

            if (!string.IsNullOrWhiteSpace(attachment.MediaType))
            {
                image = image with { MediaType = attachment.MediaType.Trim() };
            }

            mappedImages.Add(image);
        }

        imagesChanged = mappedImages.Count != currentImages.Count;
        if (!imagesChanged)
        {
            for (var index = 0; index < currentImages.Count; index++)
            {
                if (!string.Equals(mappedImages[index].Id, currentImages[index].Id, StringComparison.Ordinal) ||
                    !string.Equals(mappedImages[index].Title, currentImages[index].Title, StringComparison.Ordinal) ||
                    !string.Equals(mappedImages[index].MediaType, currentImages[index].MediaType, StringComparison.Ordinal))
                {
                    imagesChanged = true;
                    break;
                }
            }
        }

        return mappedImages;
    }

    public async Task<PluginAgentRunAugmentation> BuildAgentRunAugmentationAsync(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        WorkThreadExecutionOptions executionOptions,
        AgentInput input,
        CancellationToken cancellationToken)
    {
        var activePlugins = _runtime.ActivePlugins;
        if (activePlugins.Count == 0)
        {
            return new PluginAgentRunAugmentation();
        }

        var isManaged = IsCodeAltaManagedBackend(executionOptions.BackendId);
        var options = CreateOptions(thread, tab, isManaged);
        var activeTools = MergeTools(executionOptions.Tools, options);
        var seed = activePlugins[0];
        var beforeTemplate = new PluginBeforeAgentRunContext
        {
            Plugin = seed.Descriptor,
            Services = seed.RuntimeContext.Services,
            PromptText = ExtractText(input),
            Input = input,
            ActiveToolNames = (activeTools ?? []).Select(static tool => tool.Spec.Name).ToArray(),
        };
        var before = await _runtime.Adapter.BeforeAgentRunAsync(_runtime.ActivePlugins, beforeTemplate, options, cancellationToken);
        if (before.Result.Cancel)
        {
            return new PluginAgentRunAugmentation
            {
                CancelReason = before.Result.CancelReason ?? "Plugin cancelled the agent run.",
            };
        }

        var promptTemporaryContributions = _promptContributionScope.Take(CreatePromptContributionScopeKey(thread, tab));
        var temporaryPromptContributions = promptTemporaryContributions.Concat(before.Result.TemporaryPromptContributions).ToArray();
        var systemParts = await _runtime.Adapter.BuildSystemPromptPartsAsync(_runtime.ActivePlugins, PluginPromptChannel.System, supportsDirectInjection: isManaged, options, cancellationToken);
        var developerParts = await _runtime.Adapter.BuildSystemPromptPartsAsync(_runtime.ActivePlugins, PluginPromptChannel.Developer, supportsDirectInjection: isManaged, options, cancellationToken);
        var systemText = await BuildPromptTextAsync(
            systemParts.Parts,
            temporaryPromptContributions.Where(static part => part.Channel == PluginPromptChannel.System),
            seed,
            options,
            PluginPromptChannel.System,
            isManaged,
            cancellationToken);
        var developerText = await BuildPromptTextAsync(
            developerParts.Parts,
            temporaryPromptContributions.Where(static part => part.Channel == PluginPromptChannel.Developer),
            seed,
            options,
            PluginPromptChannel.Developer,
            isManaged,
            cancellationToken);
        var additionalInput = AppendAdditionalMessages(input, before.Result.AdditionalMessages);

        return new PluginAgentRunAugmentation
        {
            Input = additionalInput,
            Tools = activeTools,
            AdditionalSystemMessage = systemText,
            AdditionalDeveloperInstructions = developerText,
            PreferredToolNames = before.Result.PreferredToolNames,
        };
    }

    public IReadOnlyList<AgentToolDefinition>? MergeTools(IReadOnlyList<AgentToolDefinition>? existingTools, PluginAdapterOperationOptions options)
    {
        var tools = new List<AgentToolDefinition>();
        if (existingTools is not null)
        {
            tools.AddRange(existingTools);
        }

        foreach (var contribution in _runtime.Adapter.GetAgentTools(options))
        {
            tools.Add(WrapPluginTool(contribution.Definition, options));
        }

        return tools.Count == 0 ? null : tools;
    }

    public async Task<PluginCommandResult> ExecuteCommandAsync(string name, string? arguments, CancellationToken cancellationToken = default)
    {
        return await _frontend.ExecuteCommandAsync(name, arguments, cancellationToken);
    }

    public async Task ObserveAgentEventAsync(WorkThreadDescriptor thread, AgentEvent @event, CancellationToken cancellationToken = default)
    {
        var options = new PluginAdapterOperationOptions
        {
            ProjectId = thread.ProjectRef,
            ProjectPath = ResolveProjectPath(thread),
            ThreadId = thread.ThreadId,
            RunId = @event.RunId?.Value,
            BackendId = @event.BackendId.Value,
            IsCodeAltaManagedBackend = IsCodeAltaManagedBackend(@event.BackendId),
        };
        var activePlugins = _runtime.ActivePlugins;
        if (activePlugins.Count == 0)
        {
            return;
        }

        var seed = activePlugins[0];
        await _runtime.Adapter.ObserveAgentEventAsync(activePlugins, new PluginAgentEventContext { Plugin = seed.Descriptor, Services = seed.RuntimeContext.Services, Event = @event }, options, cancellationToken);
    }

    public async Task<WorkThreadPluginDerivedEventProjectionResult> ProjectThreadEventsAsync(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        IReadOnlyList<AgentEvent> events,
        bool isReplay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(events);

        if (_runtime.ActivePlugins.Count == 0 || events.Count == 0)
        {
            return new WorkThreadPluginDerivedEventProjectionResult([], []);
        }

        var projectPath = ResolveProjectPath(thread) ?? _getCurrentProject()?.ProjectPath ?? Environment.CurrentDirectory;
        var projector = new WorkThreadPluginDerivedEventProjector(
            options => _runtime.Adapter.GetContributions<PluginThreadEventProjectionContribution>(PluginPoint.ThreadEventProjection, options));
        return await projector.ProjectAsync(
            new WorkThreadCommandContext
            {
                ProjectId = thread.ProjectRef ?? _getCurrentProject()?.Id ?? "current",
                ProjectPath = projectPath,
                PromptSessionId = tab.ActiveRunId?.Value ?? thread.ThreadId,
                ModelProviderId = tab.BackendId.Value,
                ModelId = tab.ModelId,
                ThreadId = thread.ThreadId,
            },
            events,
            isReplay,
            cancellationToken);
    }

    public async Task<PluginCompactionAugmentation> BeforeCompactionAsync(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        CancellationToken cancellationToken = default)
    {
        var activePlugins = _runtime.ActivePlugins;
        if (activePlugins.Count == 0)
        {
            return new PluginCompactionAugmentation();
        }

        var options = CreateOptions(thread, tab, IsCodeAltaManagedBackend(new AgentBackendId(thread.BackendId)));
        var seed = activePlugins[0];
        var metadata = new Dictionary<string, string>
        {
            ["ThreadTitle"] = thread.Title,
            ["BackendId"] = thread.BackendId,
        };
        var before = new PluginBeforeCompactionContext
        {
            Plugin = seed.Descriptor,
            Services = seed.RuntimeContext.Services,
            Scope = seed.RuntimeContext.Scope,
            ScopeProjectId = seed.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = seed.RuntimeContext.ScopeProjectPath,
            ProjectId = options.ProjectId,
            ProjectPath = options.ProjectPath,
            ThreadId = options.ThreadId,
            RunId = options.RunId,
            BackendId = options.BackendId,
            Model = options.Model,
            CancellationToken = cancellationToken,
            CompactionId = tab.ActiveRunId?.Value,
            Metadata = metadata,
            PlanSummary = $"Manual compaction requested for '{thread.Title}'.",
        };
        var instructions = new PluginCompactionInstructionContext
        {
            Plugin = seed.Descriptor,
            Services = seed.RuntimeContext.Services,
            Scope = seed.RuntimeContext.Scope,
            ScopeProjectId = seed.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = seed.RuntimeContext.ScopeProjectPath,
            ProjectId = options.ProjectId,
            ProjectPath = options.ProjectPath,
            ThreadId = options.ThreadId,
            RunId = options.RunId,
            BackendId = options.BackendId,
            Model = options.Model,
            CancellationToken = cancellationToken,
            CompactionId = tab.ActiveRunId?.Value,
            Metadata = metadata,
            PreferredMaximumCharacters = 4_000,
        };
        var reducer = new PluginCompactionReducerContext
        {
            Plugin = seed.Descriptor,
            Services = seed.RuntimeContext.Services,
            Scope = seed.RuntimeContext.Scope,
            ScopeProjectId = seed.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = seed.RuntimeContext.ScopeProjectPath,
            ProjectId = options.ProjectId,
            ProjectPath = options.ProjectPath,
            ThreadId = options.ThreadId,
            RunId = options.RunId,
            BackendId = options.BackendId,
            Model = options.Model,
            CancellationToken = cancellationToken,
            CompactionId = tab.ActiveRunId?.Value,
            Metadata = metadata,
            PayloadKind = "manual-compaction-plan",
            Payload = before.PlanSummary,
        };
        var result = await _runtime.Adapter.RunCompactionAsync(before, instructions, reducer, options: options, cancellationToken: cancellationToken);
        if (result.BeforeResults.FirstOrDefault(static item => item.Cancel)?.Reason is { } reason)
        {
            return new PluginCompactionAugmentation { CancelReason = reason };
        }

        var instructionText = string.Join(
            "\n\n",
            result.InstructionResults
                .Select(static item => item.Instructions)
                .Where(static item => !string.IsNullOrWhiteSpace(item))!);
        return new PluginCompactionAugmentation
        {
            AdditionalDeveloperInstructions = string.IsNullOrWhiteSpace(instructionText) ? null : instructionText,
        };
    }

    public async Task AfterCompactionAsync(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        bool succeeded,
        string? summary,
        CancellationToken cancellationToken = default)
    {
        var activePlugins = _runtime.ActivePlugins;
        if (activePlugins.Count == 0)
        {
            return;
        }

        var options = CreateOptions(thread, tab, IsCodeAltaManagedBackend(new AgentBackendId(thread.BackendId)));
        var seed = activePlugins[0];
        var after = new PluginAfterCompactionContext
        {
            Plugin = seed.Descriptor,
            Services = seed.RuntimeContext.Services,
            Scope = seed.RuntimeContext.Scope,
            ScopeProjectId = seed.RuntimeContext.ScopeProjectId,
            ScopeProjectPath = seed.RuntimeContext.ScopeProjectPath,
            ProjectId = options.ProjectId,
            ProjectPath = options.ProjectPath,
            ThreadId = options.ThreadId,
            RunId = options.RunId,
            BackendId = options.BackendId,
            Model = options.Model,
            CancellationToken = cancellationToken,
            CompactionId = tab.ActiveRunId?.Value,
            Metadata = new Dictionary<string, string>
            {
                ["ThreadTitle"] = thread.Title,
                ["BackendId"] = thread.BackendId,
            },
            Succeeded = succeeded,
            Summary = summary,
        };
        await _runtime.Adapter.RunCompactionAsync(after: after, options: options, cancellationToken: cancellationToken);
    }

    private AgentToolDefinition WrapPluginTool(AgentToolDefinition definition, PluginAdapterOperationOptions options)
    {
        return definition with
        {
            Handler = async (invocation, cancellationToken) =>
            {
                var activePlugins = _runtime.ActivePlugins;
                if (activePlugins.Count == 0)
                {
                    return await definition.Handler(invocation, cancellationToken);
                }

                var seed = activePlugins[0];
                var call = await _runtime.Adapter.OnToolCallAsync(activePlugins, new PluginToolCallContext { Plugin = seed.Descriptor, Services = seed.RuntimeContext.Services, Invocation = invocation }, options, cancellationToken);
                if (call.Result?.Disposition == PluginToolCallDisposition.Block)
                {
                    return new AgentToolResult(false, [new AgentToolResultItem.Text(call.Result.BlockReason ?? "Plugin blocked tool call.")], call.Result.BlockReason ?? "Plugin blocked tool call.");
                }

                var effectiveInvocation = call.Result?.Disposition == PluginToolCallDisposition.ReplaceArguments && call.Result.ReplacementArguments is { } replacement
                    ? invocation with { Arguments = replacement }
                    : invocation;
                var result = await definition.Handler(effectiveInvocation, cancellationToken);
                var toolResult = await _runtime.Adapter.OnToolResultAsync(activePlugins, new PluginToolResultContext { Plugin = seed.Descriptor, Services = seed.RuntimeContext.Services, Invocation = effectiveInvocation, Result = result }, options, cancellationToken);
                return toolResult.Result?.Disposition == PluginToolResultDisposition.Replace && toolResult.Result.ReplacementResult is not null
                    ? toolResult.Result.ReplacementResult
                    : result;
            },
        };
    }

    private PluginAdapterOperationOptions CreateOptions(WorkThreadDescriptor? thread, OpenThreadState? tab, bool isCodeAltaManagedBackend = false)
        => new()
        {
            ProjectId = thread?.ProjectRef ?? _getCurrentProject()?.Id,
            ProjectPath = ResolveProjectPath(thread) ?? _getCurrentProject()?.ProjectPath,
            ThreadId = thread?.ThreadId,
            RunId = tab?.ActiveRunId?.Value,
            BackendId = tab?.BackendId.Value ?? thread?.BackendId,
            Model = tab?.ModelId,
            IsCodeAltaManagedBackend = isCodeAltaManagedBackend,
            HasInteractiveUi = true,
        };

    private string? ResolveProjectPath(WorkThreadDescriptor? thread)
    {
        if (thread is null)
        {
            return null;
        }

        var project = _getCurrentProject();
        if (project is not null && string.Equals(project.Id, thread.ProjectRef, StringComparison.OrdinalIgnoreCase))
        {
            return project.ProjectPath;
        }

        return thread.WorkingDirectory;
    }

    private static bool IsCodeAltaManagedBackend(AgentBackendId backendId)
        => !string.Equals(backendId.Value, AgentBackendIds.Codex.Value, StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(backendId.Value, AgentBackendIds.Copilot.Value, StringComparison.OrdinalIgnoreCase);

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

    private static PluginPromptContributionScopeKey CreatePromptContributionScopeKey(WorkThreadDescriptor thread, OpenThreadState tab)
        => new(thread.ThreadId, tab.ActiveRunId?.Value);

    private static async Task<string?> BuildPromptTextAsync(
        IEnumerable<PluginPromptPart> parts,
        IEnumerable<PluginSystemPromptContribution> temporaryContributions,
        ActivePluginInstance contextSeed,
        PluginAdapterOperationOptions options,
        PluginPromptChannel channel,
        bool supportsDirectInjection,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            AppendPromptPart(builder, part.Contribution.Title, part.Content);
        }

        foreach (var contribution in temporaryContributions)
        {
            var context = CreateTemporarySystemPromptContext(contextSeed, options, channel, supportsDirectInjection, cancellationToken);
            var content = await contribution.Content(context, cancellationToken);
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
        bool supportsDirectInjection,
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
            ThreadId = options.ThreadId,
            RunId = options.RunId,
            BackendId = options.BackendId,
            Model = options.Model,
            Channel = channel,
            SupportsDirectInjection = supportsDirectInjection,
            CancellationToken = cancellationToken,
        };

}

internal sealed record PluginAgentRunAugmentation
{
    public AgentInput? Input { get; init; }

    public IReadOnlyList<AgentToolDefinition>? Tools { get; init; }

    public string? AdditionalSystemMessage { get; init; }

    public string? AdditionalDeveloperInstructions { get; init; }

    public IReadOnlyList<string> PreferredToolNames { get; init; } = [];

    public string? CancelReason { get; init; }
}

internal sealed record PluginCompactionAugmentation
{
    public string? CancelReason { get; init; }

    public string? AdditionalDeveloperInstructions { get; init; }
}
