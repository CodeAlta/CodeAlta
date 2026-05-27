using CodeAlta.Agent;
using CodeAlta.App.Context;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.Prompts;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Shell;
using CodeAlta.Views;
using XenoAtom.Logging;

namespace CodeAlta.App;

internal sealed class SessionPromptDispatchCoordinator
{
    private readonly SessionRuntimeService _runtimeService;
    private readonly ISessionOrchestrator _orchestrator;
    private readonly SessionExecutionOptionsFactory _executionOptionsFactory;
    private readonly SessionPromptQueueCoordinator _queueCoordinator;
    private readonly ShellSessionCommandContext _commandContext;
    private readonly IProjectFileSearchService _projectFileSearchService;
    private readonly PluginHostBridge? _pluginHostBridge;
    private readonly PromptImageAttachmentStore _promptImageAttachmentStore;

    public SessionPromptDispatchCoordinator(
        SessionRuntimeService runtimeService,
        SessionExecutionOptionsFactory executionOptionsFactory,
        SessionPromptQueueCoordinator queueCoordinator,
        ShellSessionCommandContext commandContext,
        CatalogOptions catalogOptions,
        IProjectFileSearchService projectFileSearchService,
        PluginHostBridge? pluginHostBridge = null,
        ISessionOrchestrator? orchestrator = null)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(executionOptionsFactory);
        ArgumentNullException.ThrowIfNull(queueCoordinator);
        ArgumentNullException.ThrowIfNull(commandContext);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(projectFileSearchService);

        _runtimeService = runtimeService;
        _orchestrator = orchestrator ?? new RuntimeSessionOrchestratorAdapter(runtimeService, session => string.Equals(session, "__current__", StringComparison.Ordinal) ? null : null);
        _executionOptionsFactory = executionOptionsFactory;
        _queueCoordinator = queueCoordinator;
        _commandContext = commandContext;
        _projectFileSearchService = projectFileSearchService;
        _pluginHostBridge = pluginHostBridge;
        _promptImageAttachmentStore = new PromptImageAttachmentStore(catalogOptions);
    }

    public Task DispatchPromptAsync(
        SessionViewDescriptor session,
        OpenSessionState tab,
        string prompt,
        bool steer,
        CancellationToken cancellationToken = default)
        => DispatchPromptAsync(session, tab, PromptSubmission.TextOnly(prompt), steer, cancellationToken);

    public Task DispatchPromptAsync(
        SessionViewDescriptor session,
        OpenSessionState tab,
        PromptSubmission prompt,
        bool steer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(prompt);
        if (!prompt.HasContent)
        {
            throw new ArgumentException("Prompt text or image attachments are required.", nameof(prompt));
        }

        return DispatchPromptCoreAsync(session, tab, prompt, steer, cancellationToken);
    }

    public SessionExecutionOptions BuildExecutionOptions(SessionViewDescriptor session, OpenSessionState tab)
        => _executionOptionsFactory.BuildExecutionOptions(session, tab);

    public SessionExecutionOptions AppendAdditionalDeveloperInstructions(
        SessionExecutionOptions options,
        string additionalDeveloperInstructions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(additionalDeveloperInstructions);

        var combinedInstructions = string.IsNullOrWhiteSpace(options.AdditionalDeveloperInstructions)
            ? additionalDeveloperInstructions
            : $"{options.AdditionalDeveloperInstructions}\n\n{additionalDeveloperInstructions}";
        return new SessionExecutionOptions
        {
            ProviderId = options.ProviderId,
            ProviderKey = options.ProviderKey,
            WorkingDirectory = options.WorkingDirectory,
            ProjectRoots = options.ProjectRoots,
            Model = options.Model,
            ReasoningEffort = options.ReasoningEffort,
            Tools = options.Tools,
            AdditionalSystemMessage = options.AdditionalSystemMessage,
            AdditionalDeveloperInstructions = combinedInstructions,
            PreferredToolNames = options.PreferredToolNames,
            OnPermissionRequest = options.OnPermissionRequest,
            OnUserInputRequest = options.OnUserInputRequest,
        };
    }

    public SessionExecutionOptions BuildPreferredExecutionOptions(
        ModelProviderId providerId,
        string workingDirectory,
        IReadOnlyList<string> projectRoots,
        Func<string?>? sourceSessionIdProvider = null)
        => _executionOptionsFactory.BuildPreferredExecutionOptions(providerId, workingDirectory, projectRoots, sourceSessionIdProvider);

    public static string CreateInitialSessionTitle(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        return SessionPromptText.CreateInitialSessionTitle(prompt);
    }

    public static string CreateInitialSessionTitle(PromptSubmission prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return CreateInitialSessionTitle(prompt.CreateFallbackTitle());
    }

    private async Task DispatchPromptCoreAsync(
        SessionViewDescriptor session,
        OpenSessionState tab,
        PromptSubmission prompt,
        bool steer,
        CancellationToken cancellationToken)
    {
        string? pendingSteerId = null;
        var renderedOptimisticPrompt = false;
        try
        {
            tab.ActiveRunStartedAt ??= DateTimeOffset.UtcNow;
            _commandContext.SetSessionStatus(tab, StatusVisualFormatter.BuildThinkingStatusText(), true, StatusTone.Info);
            var executionOptions = _executionOptionsFactory.BuildExecutionOptions(session, tab);
            if (_pluginHostBridge is not null)
            {
                var processedPrompt = await _pluginHostBridge.ProcessPromptSubmittingAsync(session, tab, prompt, IsCodeAltaManagedProvider(executionOptions.ProviderId), cancellationToken);
                if (processedPrompt is null)
                {
                    tab.ActiveRunStartedAt = null;
                    _commandContext.SetSessionStatus(tab, "Prompt submission was handled or cancelled by a plugin.", false, StatusTone.Warning);
                    return;
                }

                prompt = processedPrompt;
            }

            var promptInput = await ProjectFilePromptInputBuilder.BuildAsync(
                    prompt.Text,
                    ResolveReferenceProjectRoot(executionOptions),
                    _projectFileSearchService,
                    cancellationToken);
            var imageReferences = await _promptImageAttachmentStore.SaveAsync(session, prompt.Images, cancellationToken);
            var agentInput = prompt.AppendImageItems(promptInput.Input, imageReferences);
            if (_pluginHostBridge is not null)
            {
                var augmentation = await _pluginHostBridge.BuildAgentRunAugmentationAsync(session, tab, executionOptions, agentInput, cancellationToken);
                if (!string.IsNullOrWhiteSpace(augmentation.CancelReason))
                {
                    tab.ActiveRunStartedAt = null;
                    _commandContext.SetSessionStatus(tab, augmentation.CancelReason, false, StatusTone.Warning);
                    return;
                }

                agentInput = augmentation.Input ?? agentInput;
                executionOptions = CopyExecutionOptions(executionOptions, augmentation);
            }

            var dispatchAsSteer = steer && await _runtimeService.HasActiveRunAsync(session, cancellationToken).ConfigureAwait(false);
            _ = RecordResolvedReferenceUsageAsync(promptInput.ResolvedReferences);
            AgentRunId runId;
            if (dispatchAsSteer)
            {
                pendingSteerId = _queueCoordinator.AddPendingSteer(tab, prompt);
                var oldSessionId = session.SessionId;
                try
                {
                    var result = await _orchestrator.SteerAsync(CreateSteerRequest(session, executionOptions, agentInput, prompt), cancellationToken);
                    runId = new AgentRunId(result.RunId ?? throw new InvalidOperationException("The orchestrator did not return a run id for the steered prompt."));
                }
                finally
                {
                    RekeySessionIfNeeded(oldSessionId, session, tab);
                }
            }
            else
            {
                renderedOptimisticPrompt = true;
                tab.Timeline.RenderOptimisticUserPrompt(promptInput.NormalizedPromptText, imageReferences, DateTimeOffset.UtcNow);

                var oldSessionId = session.SessionId;
                try
                {
                    var result = await _orchestrator.SubmitPromptAsync(CreateSubmitRequest(session, executionOptions, agentInput, prompt), cancellationToken);
                    runId = new AgentRunId(result.RunId ?? throw new InvalidOperationException("The orchestrator did not return a run id for the submitted prompt."));
                }
                finally
                {
                    RekeySessionIfNeeded(oldSessionId, session, tab);
                }
            }

            // Agent runtime sessions can complete and publish Idle before SendAsync returns.
            // Do not revive a run that the event stream has already marked idle.
            if (tab.ActiveRunStartedAt is not null)
            {
                tab.ActiveRunId = runId;
            }

            session.MarkStarted(DateTimeOffset.UtcNow);
            tab.HistoryLoaded = true;
            _commandContext.ApplyHeaderProjection();
        }
        catch (NotSupportedException ex) when (steer)
        {
            if (renderedOptimisticPrompt)
            {
                tab.Timeline.RollbackOptimisticUserPrompt();
            }

            if (!string.IsNullOrWhiteSpace(pendingSteerId))
            {
                _queueCoordinator.RemovePendingSteer(tab, pendingSteerId);
            }

            _queueCoordinator.EnqueuePrompt(tab, prompt);
            _commandContext.SetSessionStatus(
                tab,
                $"Live steering is not supported by '{session.ProviderId}'; queued the prompt for the next turn.",
                false,
                StatusTone.Warning);

            CodeAltaApp.UiLogger.Debug(ex, $"Queued prompt after unsupported steering attempt for session {session.SessionId}");
        }
        catch (OperationCanceledException ex)
        {
            if (renderedOptimisticPrompt)
            {
                tab.Timeline.RollbackOptimisticUserPrompt();
            }

            if (steer && !string.IsNullOrWhiteSpace(pendingSteerId))
            {
                _queueCoordinator.RemovePendingSteer(tab, pendingSteerId);
            }

            CodeAltaApp.UiLogger.Debug(ex, $"Cancelled prompt dispatch for session {session.SessionId}");

            tab.ActiveRunId = null;
            tab.ActiveRunStartedAt = null;
            _commandContext.SetSessionStatus(tab, "Prompt cancelled.", false, StatusTone.Warning);
        }
        catch (Exception ex)
        {
            if (renderedOptimisticPrompt)
            {
                tab.Timeline.RollbackOptimisticUserPrompt();
            }

            if (steer && !string.IsNullOrWhiteSpace(pendingSteerId))
            {
                _queueCoordinator.RemovePendingSteer(tab, pendingSteerId);
            }

            CodeAltaApp.UiLogger.Error(ex, $"Failed to send prompt for session {session.SessionId}");

            var restoredToDraft = false;
            if (_commandContext.IsSessionInputEmpty())
            {
                _commandContext.RestoreSessionInput(prompt);
                restoredToDraft = true;
            }

            tab.Timeline.RenderFailure(BuildPromptDispatchFailureMarkdown(ex.Message, prompt, restoredToDraft));
            tab.ActiveRunStartedAt = null;
            _commandContext.SetSessionStatus(tab, $"Failed to send prompt: {ex.Message}", false, StatusTone.Error);
        }
    }

    private static SessionExecutionOptions CopyExecutionOptions(SessionExecutionOptions source, PluginAgentRunAugmentation augmentation)
        => new()
        {
            ProviderId = source.ProviderId,
            ProviderKey = source.ProviderKey,
            WorkingDirectory = source.WorkingDirectory,
            ProjectRoots = source.ProjectRoots,
            Model = source.Model,
            ReasoningEffort = source.ReasoningEffort,
            Tools = augmentation.Tools ?? source.Tools,
            AdditionalSystemMessage = augmentation.AdditionalSystemMessage ?? source.AdditionalSystemMessage,
            AdditionalDeveloperInstructions = augmentation.AdditionalDeveloperInstructions ?? source.AdditionalDeveloperInstructions,
            PreferredToolNames = augmentation.PreferredToolNames.Count == 0 ? source.PreferredToolNames : augmentation.PreferredToolNames,
            OnPermissionRequest = source.OnPermissionRequest,
            OnUserInputRequest = source.OnUserInputRequest,
        };

    private static bool IsCodeAltaManagedProvider(ModelProviderId providerId)
        => !string.Equals(providerId.Value, ModelProviderIds.Codex.Value, StringComparison.OrdinalIgnoreCase) &&
           !string.Equals(providerId.Value, ModelProviderIds.Copilot.Value, StringComparison.OrdinalIgnoreCase);

    private static SubmitSessionPromptRequest CreateSubmitRequest(
        SessionViewDescriptor session,
        SessionExecutionOptions executionOptions,
        AgentInput input,
        PromptSubmission prompt)
        => new()
        {
            Context = CreateCommandContext(session, executionOptions),
            Prompt = prompt.Text,
            PreparedInput = input,
        };

    private static SteerSessionRequest CreateSteerRequest(
        SessionViewDescriptor session,
        SessionExecutionOptions executionOptions,
        AgentInput input,
        PromptSubmission prompt)
        => new()
        {
            Context = CreateCommandContext(session, executionOptions),
            Prompt = prompt.Text,
            PreparedInput = input,
        };

    private static CodeAlta.Orchestration.Runtime.SessionCommandContext CreateCommandContext(
        SessionViewDescriptor session,
        SessionExecutionOptions executionOptions)
        => new()
        {
            ProjectId = session.ProjectRef ?? "legacy-global",
            ProjectPath = executionOptions.ProjectRoots.FirstOrDefault() ?? executionOptions.WorkingDirectory ?? string.Empty,
            PromptSessionId = session.SessionId,
            ModelProviderId = executionOptions.ProviderKey ?? executionOptions.ProviderId.Value,
            ModelId = executionOptions.Model,
            SessionId = session.SessionId,
            ExecutionOptions = executionOptions,
        };

    private void RekeySessionIfNeeded(string oldSessionId, SessionViewDescriptor session, OpenSessionState tab)
    {
        if (string.IsNullOrWhiteSpace(oldSessionId) ||
            string.Equals(oldSessionId, session.SessionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        tab.SessionView = session;
        tab.ViewModel.SessionId = session.SessionId;
        _commandContext.RekeySessionIdentity(oldSessionId, session);
    }

    private static string? ResolveReferenceProjectRoot(SessionExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return executionOptions.ProjectRoots.FirstOrDefault() ?? executionOptions.WorkingDirectory;
    }

    private static string BuildPromptDispatchFailureMarkdown(string errorMessage, PromptSubmission prompt, bool restoredToDraft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        ArgumentNullException.ThrowIfNull(prompt);

        var normalizedPrompt = prompt.Text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        var builder = new System.Text.StringBuilder();
        builder.Append("Failed to send prompt: ").Append(errorMessage);
        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine(restoredToDraft
            ? "Prompt restored to the editor for retry."
            : "Prompt preserved below because the current editor draft is no longer empty.");
        builder.AppendLine();
        builder.AppendLine("Prompt:");

        foreach (var line in (string.IsNullOrWhiteSpace(normalizedPrompt) ? prompt.CreateFallbackTitle() : normalizedPrompt).Split('\n'))
        {
            builder.Append("> ");
            builder.AppendLine(line);
        }

        if (prompt.Images.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Images:");
            foreach (var image in prompt.Images)
            {
                builder.Append("- ").Append(image.Title).Append(" (").Append(image.MediaType).AppendLine(")");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private async Task RecordResolvedReferenceUsageAsync(IReadOnlyList<ProjectFileResolution> resolutions)
    {
        if (resolutions.Count == 0)
        {
            return;
        }

        foreach (var resolution in resolutions)
        {
            var item = resolution.Item;
            if (item is null)
            {
                continue;
            }

            try
            {
                await _projectFileSearchService.RecordUsageAsync(
                    new ProjectFileUsageEvent(
                        item.ProjectRoot,
                        item.RelativePath,
                        item.Kind,
                        DateTimeOffset.UtcNow,
                        ProjectFileUsageAccessKind.PromptInserted));
            }
            catch
            {
            }
        }
    }
}
