using CodeAlta.Agent;
using XenoAtom.Logging;

namespace CodeAlta.Plugins.Abstractions;

/// <summary>
/// Provides no-op plugin services for tests, headless bootstrap, and unsupported host modes.
/// </summary>
public sealed class NoopPluginServices : IPluginServices
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NoopPluginServices"/> class.
    /// </summary>
    /// <param name="logger">The plugin logger.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="logger"/> is <see langword="null"/>.</exception>
    public NoopPluginServices(Logger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Logger = logger;
        Ui = new NoopPluginUiService();
        State = new NoopPluginStateStore();
        Database = new NoopPluginDatabase();
        Workspace = new NoopPluginWorkspaceService();
        Sessions = new NoopPluginSessionService();
        Prompts = new NoopPluginPromptService();
        Agents = new NoopPluginAgentService();
        Tasks = new NoopPluginTaskService();
        Alta = new NoopPluginAltaService();
        Canvases = NoopPluginCanvasService.Instance;
    }

    /// <inheritdoc />
    public Logger Logger { get; }

    /// <inheritdoc />
    public IPluginUiService Ui { get; }

    /// <inheritdoc />
    public IPluginStateStore State { get; }

    /// <inheritdoc />
    public IPluginDatabase Database { get; }

    /// <inheritdoc />
    public IPluginWorkspaceService Workspace { get; }

    /// <inheritdoc />
    public IPluginSessionService Sessions { get; }

    /// <inheritdoc />
    public IPluginPromptService Prompts { get; }

    /// <inheritdoc />
    public IPluginAgentService Agents { get; }

    /// <inheritdoc />
    public IPluginTaskService Tasks { get; }

    /// <inheritdoc />
    public IPluginAltaService Alta { get; }

    /// <inheritdoc />
    public IPluginCanvasService Canvases { get; }

    /// <summary>
    /// Creates no-op services with a default logger.
    /// </summary>
    /// <returns>No-op services.</returns>
    public static NoopPluginServices Create()
    {
        return new NoopPluginServices(LogManager.GetLogger("CodeAlta.Plugin.Noop"));
    }
}

/// <summary>
/// No-op implementation of <see cref="IPluginAltaService"/>.
/// </summary>
public sealed class NoopPluginAltaService : IPluginAltaService
{
    /// <inheritdoc />
    public ValueTask<PluginAltaCommandResult> InvokeAsync(
        IReadOnlyList<string> args,
        string? stdin = null,
        PluginAltaInvocationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        cancellationToken.ThrowIfCancellationRequested();
        var correlationId = Guid.CreateVersion7().ToString("N");
        return new ValueTask<PluginAltaCommandResult>(new PluginAltaCommandResult
        {
            ExitCode = 5,
            TranscriptJsonl = $"{{\"type\":\"alta.result\",\"version\":1,\"exitCode\":5,\"correlationId\":\"{correlationId}\",\"truncated\":false,\"recordCount\":0,\"diagnosticCount\":1,\"durationMs\":0}}\n{{\"type\":\"alta.error\",\"version\":1,\"correlationId\":\"{correlationId}\",\"code\":\"service.unavailable\",\"exitCode\":5,\"message\":\"The alta plugin service is not available in this host context.\"}}\n",
            Error = "The alta plugin service is not available in this host context.",
        });
    }
}

/// <summary>
/// No-op implementation of <see cref="IPluginUiService"/>.
/// </summary>
public sealed class NoopPluginUiService : IPluginUiService
{
    /// <inheritdoc />
    public bool HasInteractiveUi => false;

    /// <inheritdoc />
    public ValueTask NotifyAsync(string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<bool>(false);
    }

    /// <inheritdoc />
    public ValueTask<string?> InputAsync(string title, string? initialText = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<string?>((string?)null);
    }

    /// <inheritdoc />
    public ValueTask<string?> EditTextAsync(string title, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<string?>((string?)null);
    }

    /// <inheritdoc />
    public ValueTask<T?> SelectAsync<T>(string title, IReadOnlyList<PluginSelectItem<T>> items, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(items);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<T?>((T?)default);
    }

    /// <inheritdoc />
    public ValueTask ShowDialogAsync(PluginDialogRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<PluginDialogResponse?> ShowDialogForResultAsync(PluginDialogRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<PluginDialogResponse?>((PluginDialogResponse?)null);
    }
}

/// <summary>
/// No-op implementation of <see cref="IPluginStateStore"/>.
/// </summary>
public sealed class NoopPluginStateStore : IPluginStateStore
{
    /// <inheritdoc />
    public string GetDirectory(PluginStateScope scope)
    {
        return Path.Combine(Path.GetTempPath(), "CodeAlta", "Plugins", "Noop", scope.ToString());
    }

    /// <inheritdoc />
    public ValueTask<T?> ReadJsonAsync<T>(PluginStateScope scope, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<T?>((T?)default);
    }

    /// <inheritdoc />
    public ValueTask WriteJsonAsync<T>(PluginStateScope scope, string name, T value, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DeleteAsync(PluginStateScope scope, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// No-op implementation of <see cref="IPluginWorkspaceService"/>.
/// </summary>
public sealed class NoopPluginWorkspaceService : IPluginWorkspaceService
{
    /// <inheritdoc />
    public string? SelectedProjectId => null;

    /// <inheritdoc />
    public string? SelectedProjectPath => null;

    /// <inheritdoc />
    public IReadOnlyList<string> ProjectPaths => [];

    /// <inheritdoc />
    public string? GetSelectedProjectPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        return null;
    }

    /// <inheritdoc />
    public bool IsInsideSelectedProject(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return false;
    }
}

/// <summary>
/// No-op implementation of <see cref="IPluginSessionService"/>.
/// </summary>
public sealed class NoopPluginSessionService : IPluginSessionService
{
    /// <inheritdoc />
    public string? SelectedSessionId => null;

    /// <inheritdoc />
    public bool IsSelectedSessionBusy => false;

    /// <inheritdoc />
    public ValueTask SendPromptAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask EnqueuePromptAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> TrySteerAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<bool>(false);
    }

    /// <inheritdoc />
    public ValueTask<bool> RequestCompactionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<bool>(false);
    }
}

/// <summary>
/// No-op implementation of <see cref="IPluginPromptService"/>.
/// </summary>
public sealed class NoopPluginPromptService : IPluginPromptService
{
    /// <inheritdoc />
    public string? DraftText => null;

    /// <inheritdoc />
    public ValueTask SetDraftTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask AddAttachmentAsync(PluginPromptAttachment attachment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<PluginPromptAttachment>> GetAttachmentsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IReadOnlyList<PluginPromptAttachment>>([]);
    }
}

/// <summary>
/// No-op implementation of <see cref="IPluginAgentService"/>.
/// </summary>
public sealed class NoopPluginAgentService : IPluginAgentService
{
    /// <inheritdoc />
    public ModelProviderId? ActiveProviderId => null;

    /// <inheritdoc />
    public string? ActiveProviderDisplayName => null;

    /// <inheritdoc />
    public string? ActiveModel => null;

    /// <inheritdoc />
    public bool IsCodeAltaManagedProvider => false;

    /// <inheritdoc />
    public bool HasCapability(string capabilityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityName);
        return false;
    }
}

/// <summary>
/// No-op implementation of <see cref="IPluginTaskService"/> that still tracks scheduled work.
/// </summary>
public sealed class NoopPluginTaskService : IPluginTaskService
{
    private readonly object _gate = new();
    private readonly List<PluginTaskHandle> _runningTasks = [];

    /// <inheritdoc />
    public bool HasRunningTasks
    {
        get
        {
            lock (_gate)
            {
                RemoveCompletedTasks();
                return _runningTasks.Count != 0;
            }
        }
    }

    /// <inheritdoc />
    public int RunningTaskCount
    {
        get
        {
            lock (_gate)
            {
                RemoveCompletedTasks();
                return _runningTasks.Count;
            }
        }
    }

    /// <inheritdoc />
    public PluginTaskHandle Run(string name, Func<CancellationToken, ValueTask> work, PluginTaskOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(work);

        options ??= new PluginTaskOptions();
        var cancellationTokenSource = new CancellationTokenSource();
        var task = StartTask(work, cancellationTokenSource.Token, options.LongRunning);
        var handle = new PluginTaskHandle(
            name,
            options.Description,
            options.LongRunning,
            DateTimeOffset.UtcNow,
            task,
            () => RequestCancellation(cancellationTokenSource));

        lock (_gate)
        {
            _runningTasks.Add(handle);
        }

        _ = task.ContinueWith(
            _ =>
            {
                lock (_gate)
                {
                    _runningTasks.Remove(handle);
                }

                cancellationTokenSource.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return handle;
    }

    /// <inheritdoc />
    public async ValueTask WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task[] runningTasks;
            lock (_gate)
            {
                RemoveCompletedTasks();
                if (_runningTasks.Count == 0)
                {
                    return;
                }

                runningTasks = [.. _runningTasks.Select(static task => task.Completion)];
            }

            try
            {
                await Task.WhenAll(runningTasks).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Completion failures are exposed through PluginTaskHandle.Completion; waiting for idle only tracks lifetime.
            }
        }
    }

    private static Task StartTask(Func<CancellationToken, ValueTask> work, CancellationToken cancellationToken, bool longRunning)
    {
        return longRunning
            ? Task.Factory.StartNew(
                static state => RunWorkAsync((WorkState)state!),
                new WorkState(work, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap()
            : Task.Run(() => RunWorkAsync(new WorkState(work, cancellationToken)), cancellationToken);
    }

    private static async Task RunWorkAsync(WorkState state)
    {
        await state.Work(state.CancellationToken).ConfigureAwait(false);
    }

    private static void RequestCancellation(CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The task has already completed and the no-op tracker has released its cancellation source.
        }
    }

    private void RemoveCompletedTasks()
    {
        _runningTasks.RemoveAll(static task => task.IsCompleted);
    }

    private readonly record struct WorkState(Func<CancellationToken, ValueTask> Work, CancellationToken CancellationToken);
}
