using CodeAlta.Plugin.Mcp;
using CodeAlta.Views;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.App;

internal sealed record McpManagementCoordinatorOptions
{
    public required Func<string?> GetProjectDirectory { get; init; }

    public required Func<string, CancellationToken, Task> OpenFileAsync { get; init; }

    public required Func<Rectangle?> GetBounds { get; init; }

    public required Func<Visual?> GetFocusTarget { get; init; }
}

internal sealed class McpManagementCoordinator
{
    private readonly McpManagementService _service;
    private readonly McpManagementCoordinatorOptions _options;

    public McpManagementCoordinator(McpManagementService service, McpManagementCoordinatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.GetProjectDirectory);
        ArgumentNullException.ThrowIfNull(options.OpenFileAsync);
        ArgumentNullException.ThrowIfNull(options.GetBounds);
        ArgumentNullException.ThrowIfNull(options.GetFocusTarget);
        _service = service;
        _options = options;
    }

    public void Open()
        => new McpServersDialog(_service, CreateRequest, _options.OpenFileAsync, _options.GetBounds, _options.GetFocusTarget).Show();

    private McpManagementRequest CreateRequest()
        => new() { ProjectDirectory = _options.GetProjectDirectory() };
}
