using CodeAlta.Agent;
using CodeAlta.App.Context;
using CodeAlta.App.State;
using CodeAlta.Catalog;
using CodeAlta.Models;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Presentation.Prompting;
using CodeAlta.Presentation.Shell;
using CodeAlta.Views;
using XenoAtom.Logging;
using CodeAlta.Search;

namespace CodeAlta.App;

internal sealed class ThreadPromptDispatchCoordinator
{
    private const int MaxInitialThreadTitleLength = 80;
    private readonly WorkThreadRuntimeService _runtimeService;
    private readonly ThreadExecutionOptionsFactory _executionOptionsFactory;
    private readonly ThreadPromptQueueCoordinator _queueCoordinator;
    private readonly ThreadCommandContext _commandContext;
    private readonly IProjectFileSearchService _projectFileSearchService;
    private readonly PromptImageAttachmentStore _promptImageAttachmentStore;

    public ThreadPromptDispatchCoordinator(
        WorkThreadRuntimeService runtimeService,
        ThreadExecutionOptionsFactory executionOptionsFactory,
        ThreadPromptQueueCoordinator queueCoordinator,
        ThreadCommandContext commandContext,
        CatalogOptions catalogOptions,
        IProjectFileSearchService projectFileSearchService)
    {
        ArgumentNullException.ThrowIfNull(runtimeService);
        ArgumentNullException.ThrowIfNull(executionOptionsFactory);
        ArgumentNullException.ThrowIfNull(queueCoordinator);
        ArgumentNullException.ThrowIfNull(commandContext);
        ArgumentNullException.ThrowIfNull(catalogOptions);
        ArgumentNullException.ThrowIfNull(projectFileSearchService);

        _runtimeService = runtimeService;
        _executionOptionsFactory = executionOptionsFactory;
        _queueCoordinator = queueCoordinator;
        _commandContext = commandContext;
        _projectFileSearchService = projectFileSearchService;
        _promptImageAttachmentStore = new PromptImageAttachmentStore(catalogOptions);
    }

    public Task DispatchPromptAsync(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        string prompt,
        bool steer,
        CancellationToken cancellationToken = default)
        => DispatchPromptAsync(thread, tab, PromptSubmission.TextOnly(prompt), steer, cancellationToken);

    public Task DispatchPromptAsync(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        PromptSubmission prompt,
        bool steer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(prompt);
        if (!prompt.HasContent)
        {
            throw new ArgumentException("Prompt text or image attachments are required.", nameof(prompt));
        }

        return DispatchPromptCoreAsync(thread, tab, prompt, steer, cancellationToken);
    }

    public WorkThreadExecutionOptions BuildExecutionOptions(WorkThreadDescriptor thread, OpenThreadState tab)
        => _executionOptionsFactory.BuildExecutionOptions(thread, tab);

    public WorkThreadExecutionOptions BuildPreferredExecutionOptions(
        AgentBackendId backendId,
        string workingDirectory,
        IReadOnlyList<string> projectRoots)
        => _executionOptionsFactory.BuildPreferredExecutionOptions(backendId, workingDirectory, projectRoots);

    public static string CreateInitialThreadTitle(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var normalized = prompt
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Trim();

        normalized = string.Join(" ", normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
        {
            return prompt.Trim();
        }

        var sentenceLength = FindFirstSentenceLength(normalized);
        var candidate = sentenceLength > 0
            ? normalized[..sentenceLength]
            : normalized;

        if (candidate.Length <= MaxInitialThreadTitleLength)
        {
            return candidate;
        }

        return candidate[..(MaxInitialThreadTitleLength - 3)].TrimEnd() + "...";
    }

    public static string CreateInitialThreadTitle(PromptSubmission prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        return CreateInitialThreadTitle(prompt.CreateFallbackTitle());
    }

    private static int FindFirstSentenceLength(string content)
    {
        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (ch is not ('.' or '!' or '?'))
            {
                continue;
            }

            if (i == content.Length - 1 || char.IsWhiteSpace(content[i + 1]))
            {
                return i + 1;
            }
        }

        return 0;
    }

    public WorkThreadExecutionOptions BuildDelegationExecutionOptions(
        string threadId,
        OpenThreadState tab,
        string workingDirectory,
        IReadOnlyList<string> projectRoots)
        => _executionOptionsFactory.BuildDelegationExecutionOptions(threadId, tab, workingDirectory, projectRoots);

    private async Task DispatchPromptCoreAsync(
        WorkThreadDescriptor thread,
        OpenThreadState tab,
        PromptSubmission prompt,
        bool steer,
        CancellationToken cancellationToken)
    {
        string? pendingSteerId = null;
        try
        {
            tab.ActiveRunStartedAt ??= DateTimeOffset.UtcNow;
            _commandContext.SetThreadStatus(tab, StatusVisualFormatter.BuildThinkingStatusText(), true, StatusTone.Info);
            var executionOptions = _executionOptionsFactory.BuildExecutionOptions(thread, tab);
            var promptInput = await ProjectFilePromptInputBuilder.BuildAsync(
                    prompt.Text,
                    ResolveReferenceProjectRoot(executionOptions),
                    _projectFileSearchService,
                    cancellationToken);
            var imageReferences = await _promptImageAttachmentStore.SaveAsync(thread, prompt.Images, cancellationToken);
            var agentInput = prompt.AppendImageItems(promptInput.Input, imageReferences);
            var dispatchAsSteer = steer && tab.ActiveRunId is not null;
            _ = RecordResolvedReferenceUsageAsync(promptInput.ResolvedReferences);
            AgentRunId runId;
            if (dispatchAsSteer)
            {
                pendingSteerId = _queueCoordinator.AddPendingSteer(tab, prompt);
                runId = await _runtimeService.SteerAsync(
                        thread,
                        executionOptions,
                        new AgentSteerOptions
                        {
                            Input = agentInput,
                            ExpectedRunId = tab.ActiveRunId,
                        },
                        cancellationToken)
                    ;
            }
            else
            {
                tab.Timeline.RenderOptimisticUserPrompt(promptInput.NormalizedPromptText, imageReferences, DateTimeOffset.UtcNow);

                runId = await _runtimeService.SendAsync(
                        thread,
                        executionOptions,
                        new AgentSendOptions { Input = agentInput },
                        cancellationToken)
                    ;
            }

            tab.ActiveRunId = runId;
            thread.MarkStarted(DateTimeOffset.UtcNow);
            tab.HistoryLoaded = true;
            _commandContext.RefreshHeaderAndThreadWorkspace();
        }
        catch (NotSupportedException ex) when (steer)
        {
            if (!string.IsNullOrWhiteSpace(pendingSteerId))
            {
                _queueCoordinator.RemovePendingSteer(tab, pendingSteerId);
            }

            _queueCoordinator.EnqueuePrompt(tab, prompt);
            _commandContext.SetThreadStatus(
                tab,
                $"Live steering is not supported by '{thread.BackendId}'; queued the prompt for the next turn.",
                false,
                StatusTone.Warning);

            if (LogManager.IsInitialized && CodeAltaApp.UiLogger.IsEnabled(LogLevel.Debug))
            {
                CodeAltaApp.UiLogger.Debug(ex, $"Queued prompt after unsupported steering attempt for thread {thread.ThreadId}");
            }
        }
        catch (Exception ex)
        {
            if (steer && !string.IsNullOrWhiteSpace(pendingSteerId))
            {
                _queueCoordinator.RemovePendingSteer(tab, pendingSteerId);
            }

            if (LogManager.IsInitialized && CodeAltaApp.UiLogger.IsEnabled(LogLevel.Error))
            {
                CodeAltaApp.UiLogger.Error(ex, $"Failed to send prompt for thread {thread.ThreadId}");
            }

            var restoredToDraft = false;
            if (_commandContext.IsThreadInputEmpty())
            {
                _commandContext.RestoreThreadInput(prompt);
                restoredToDraft = true;
            }

            tab.Timeline.RenderFailure(BuildPromptDispatchFailureMarkdown(ex.Message, prompt, restoredToDraft));
            tab.ActiveRunStartedAt = null;
            _commandContext.SetThreadStatus(tab, $"Failed to send prompt: {ex.Message}", false, StatusTone.Error);
        }
    }

    private static string? ResolveReferenceProjectRoot(WorkThreadExecutionOptions executionOptions)
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
