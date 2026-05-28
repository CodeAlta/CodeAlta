using CodeAlta.Plugin.Mcp;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.App;

internal static class McpManagementCoordinatorFactory
{
    public static McpManagementCoordinator Create(
        McpManagementService service,
        Func<string?> getProjectDirectory,
        Func<string, CancellationToken, Task> openFileAsync,
        Func<Rectangle?> getBounds,
        Func<Visual?> getFocusTarget)
        => new(
            service,
            new McpManagementCoordinatorOptions
            {
                GetProjectDirectory = getProjectDirectory,
                OpenFileAsync = openFileAsync,
                GetBounds = getBounds,
                GetFocusTarget = getFocusTarget,
            });
}
