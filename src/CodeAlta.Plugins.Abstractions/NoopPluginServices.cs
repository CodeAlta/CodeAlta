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
        Workspace = new NoopPluginWorkspaceService();
        Threads = new NoopPluginThreadService();
        Prompts = new NoopPluginPromptService();
        Agents = new NoopPluginAgentService();
    }

    /// <inheritdoc />
    public Logger Logger { get; }

    /// <inheritdoc />
    public IPluginUiService Ui { get; }

    /// <inheritdoc />
    public IPluginStateStore State { get; }

    /// <inheritdoc />
    public IPluginWorkspaceService Workspace { get; }

    /// <inheritdoc />
    public IPluginThreadService Threads { get; }

    /// <inheritdoc />
    public IPluginPromptService Prompts { get; }

    /// <inheritdoc />
    public IPluginAgentService Agents { get; }

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
/// No-op implementation of <see cref="IPluginThreadService"/>.
/// </summary>
public sealed class NoopPluginThreadService : IPluginThreadService
{
    /// <inheritdoc />
    public string? SelectedThreadId => null;

    /// <inheritdoc />
    public bool IsSelectedThreadBusy => false;

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
    public AgentBackendId? ActiveBackendId => null;

    /// <inheritdoc />
    public string? ActiveBackendDisplayName => null;

    /// <inheritdoc />
    public string? ActiveModel => null;

    /// <inheritdoc />
    public bool IsCodeAltaManagedBackend => false;

    /// <inheritdoc />
    public bool HasCapability(string capabilityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityName);
        return false;
    }
}
