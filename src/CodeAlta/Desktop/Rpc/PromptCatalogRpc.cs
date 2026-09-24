using System.Text.Json;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Orchestration.Runtime.SystemPrompts;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// Project scope is resolved by the owned session command service; never accept paths from the view.
[NeoRpcService("promptCatalog", Version = 1)]
internal sealed class PromptCatalogService
{
    internal const int MaximumResponseBytes = 96 * 1024;
    private readonly string? _epoch;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<AgentPromptDescriptor>?>>? _read;

    internal PromptCatalogService() { }
    internal PromptCatalogService(OwnedSessionCommandService commands, string epoch)
        : this(commands.GetPromptCatalogAsync, epoch) { }
    internal PromptCatalogService(Func<string, CancellationToken, Task<IReadOnlyList<AgentPromptDescriptor>?>> read, string epoch)
    { _read = read; _epoch = epoch; }

    [NeoRpcMethod("list")]
    public async Task<PromptCatalogResponse> List(PromptCatalogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_read is null || _epoch is null) return new("unconfigured", _epoch, request.SessionId, [], false);
        if (!ValidId(request.ExpectedEpoch) || !ValidId(request.SessionId)) return new("invalid_request", _epoch, request.SessionId, [], false);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", _epoch, request.SessionId, [], false);
        try
        {
            var descriptors = await _read(request.SessionId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (descriptors is null) return new("unavailable", _epoch, request.SessionId, [], false);
            var rows = new List<PromptCatalogEntry>();
            foreach (var prompt in descriptors)
            {
                if (rows.Count >= 64) break;
                if (!ValidId(prompt.PromptName)) continue;
                var body = Bound(prompt.Body, 2048);
                var entry = new PromptCatalogEntry(prompt.PromptName, Bound(prompt.DisplayName, 256),
                    prompt.Description is null ? null : Bound(prompt.Description, 1024),
                    prompt.SourceKind.ToString(), prompt.IsBuiltIn, prompt.Mode == PromptCompositionMode.Append,
                    body, body.Length != prompt.Body.Length);
                var candidate = new PromptCatalogResponse("ok", _epoch, request.SessionId, [.. rows, entry], false);
                if (JsonSerializer.SerializeToUtf8Bytes(candidate, DesktopJsonContext.Default.PromptCatalogResponse).Length > MaximumResponseBytes) break;
                rows.Add(entry);
            }
            return new("ok", _epoch, request.SessionId, rows, descriptors.Count > rows.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new("read_failed", _epoch, request.SessionId, [], false); }
    }

    private static bool ValidId(string? value) => value is { Length: > 0 and <= 256 } && value.Trim() == value && !value.Any(char.IsControl);
    private static string Bound(string value, int length) => value[..Math.Min(value.Length, length)];
}

internal sealed record PromptCatalogRequest(string ExpectedEpoch, string SessionId);
internal sealed record PromptCatalogResponse(string Status, string? Epoch, string SessionId, IReadOnlyList<PromptCatalogEntry> Prompts, bool Truncated);
internal sealed record PromptCatalogEntry(string Id, string Name, string? Description, string Scope, bool BuiltIn,
    bool Appended, string Body, bool BodyTruncated);
