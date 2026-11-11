using CodeAlta.Fleet.Protocol.Api;

namespace CodeAlta.Server.Hosting;

internal sealed class ServerTunnelAccessSource(Func<CancellationToken, Task<FleetTunnelAccessDto?>>? read)
{
    internal async Task<FleetTunnelAccessDto> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return read is not null && await read(cancellationToken).ConfigureAwait(false) is { } access ? access
                : throw CoordinatorException.NotFound("tunnel.not_configured", "This server has no managed Dev Tunnel.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // CLI output can contain credentials: report only a controlled diagnostic.
            throw new CoordinatorException(503, "tunnel.login_required", "The server could not renew Dev Tunnel access. Its owner must sign in again on the server's machine with devtunnel user login --github -d.");
        }
    }
}
