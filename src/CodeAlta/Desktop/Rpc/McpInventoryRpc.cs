using CodeAlta.Orchestration.Runtime;
using CodeAlta.Plugin.Mcp;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// No client paths, raw definitions or runtime activation cross this boundary.
[NeoRpcService("mcpInventory", Version = 1)]
internal sealed class McpInventoryService
{
    private readonly Func<string, CancellationToken, Task<OwnedMcpScope?>>? _resolve;
    private readonly McpInventoryReader _reader = new();
    private readonly string? _epoch;
    private readonly string? _home;

    internal McpInventoryService() { }
    internal McpInventoryService(OwnedSessionCommandService commands, string epoch, string? home)
        : this(commands.GetMcpScopeAsync, epoch, home) { }
    internal McpInventoryService(Func<string, CancellationToken, Task<OwnedMcpScope?>> resolve, string epoch, string? home = null)
    { _resolve = resolve; _epoch = epoch; _home = home; }

    [NeoRpcMethod("list")]
    public async Task<McpInventoryResponse> List(McpInventoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        McpInventoryResponse Fail(string status) => new(status, _epoch, request.SessionId, null, [], [], false, 0);
        if (_resolve is null || _epoch is null) return Fail("unconfigured");
        if (!ValidId(request.ExpectedEpoch) || !ValidId(request.SessionId)) return Fail("invalid_request");
        if (request.ExpectedEpoch != _epoch) return Fail("stale_epoch");
        try
        {
            var scope = await _resolve(request.SessionId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (scope is null) return Fail("unavailable");
            var inventory = _reader.Read(scope.ProjectDirectory, _home);
            return new("ok", _epoch, request.SessionId, scope.ProjectId, inventory.Servers, inventory.Sources,
                inventory.PolicyReadError, inventory.Omitted);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Fail("read_failed"); } // Never serialize exceptions, file paths or parser diagnostics.
    }

    private static bool ValidId(string? value) => value is { Length: > 0 and <= 256 } && value.Trim() == value && !value.Any(char.IsControl);
}

internal sealed record McpInventoryRequest(string ExpectedEpoch, string SessionId);
internal sealed record McpInventoryResponse(string Status, string? Epoch, string SessionId, string? ProjectId,
    IReadOnlyList<McpInventoryServer> Servers, IReadOnlyList<string> Sources, bool PolicyReadError, int Omitted);
