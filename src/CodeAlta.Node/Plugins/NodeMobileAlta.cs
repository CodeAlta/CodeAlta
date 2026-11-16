using CodeAlta.Fleet.Client;
using CodeAlta.Fleet.NodeApi;
using CodeAlta.LiveTool;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Node.Plugins;

// Only the built-in personal phone page uses the node's work admission. Other plugins keep their normal alta bridge;
// an agent cannot select a plugin runtime key for itself.
internal sealed class NodeMobileAlta(PluginAltaServiceBridge inner) : IPluginAltaRuntimeService
{
    private FleetMachineAlta? _personal;

    internal void Attach(IFleetSessionAccess admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        Volatile.Write(ref _personal, new FleetMachineAlta(Environment.MachineName, _ => ValueTask.FromResult<IFleetSessionAccess?>(admission)));
    }

    public ValueTask<PluginAltaCommandResult> InvokeAsync(IReadOnlyList<string> args, string? stdin = null, PluginAltaInvocationOptions? options = null,
        CancellationToken cancellationToken = default) => inner.InvokeAsync(args, stdin, options, cancellationToken);

    public ValueTask<PluginAltaCommandResult> InvokeAsync(string pluginRuntimeKey, IReadOnlyList<string> args, string? stdin = null,
        PluginAltaInvocationOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (pluginRuntimeKey != "builtin:mobile") return inner.InvokeAsync(pluginRuntimeKey, args, stdin, options, cancellationToken);
        return Volatile.Read(ref _personal) is { } personal
            ? personal.InvokeAsync(args, stdin, options, cancellationToken)
            : ValueTask.FromResult(new PluginAltaCommandResult
            {
                ExitCode = 5, Error = "The node's session admission is not ready. Start its listener to use Mobile.",
                TranscriptJsonl = "{\"type\":\"alta.error\",\"code\":\"service.unavailable\",\"message\":\"The node's session admission is not ready. Start its listener to use Mobile.\"}\n",
            });
    }
}
