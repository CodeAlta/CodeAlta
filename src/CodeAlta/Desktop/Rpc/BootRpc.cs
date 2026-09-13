using System.Text.Json.Serialization;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(CodeAlta.Desktop.Rpc.DesktopJsonContext))]

namespace CodeAlta.Desktop.Rpc;

[NeoRpcService("boot", Version = 1)]
internal sealed class BootService
{
    private readonly string? _epoch;
    private readonly bool _commandReview;
    private readonly bool _userInput;
    internal BootService() { }
    internal BootService(string epoch) { _epoch = epoch; }
    internal BootService(string epoch, bool commandReview) { _epoch = epoch; _commandReview = commandReview; }
    internal BootService(string epoch, bool commandReview, bool userInput) : this(epoch, commandReview) { _userInput = userInput; }
    [NeoRpcMethod("status")]
    public BootStatus Status(BootRequest request) => _epoch is null
        ? new("in-development", "CodeAlta", DesktopCommandLine.Version, false)
        : new("owned-text-only", "CodeAlta", DesktopCommandLine.Version, true) { HostEpoch = _epoch, CommandReviewEnabled = _commandReview, OwnedAsksEnabled = true, OwnedUserInputEnabled = _userInput };
}

internal sealed record BootRequest;
internal sealed record BootStatus(string State, string ProductName, string Version, bool HostAvailable)
{
    public string? HostEpoch { get; init; }
    public bool CommandReviewEnabled { get; init; }
    public bool OwnedAsksEnabled { get; init; }
    public bool OwnedUserInputEnabled { get; init; }
}

[JsonSerializable(typeof(BootRequest))]
[JsonSerializable(typeof(BootStatus))]
[JsonSerializable(typeof(WorkspaceRequest))]
[JsonSerializable(typeof(WorkspaceSnapshot))]
[JsonSerializable(typeof(WorkspaceProject))]
[JsonSerializable(typeof(WorkspaceSession))]
[JsonSerializable(typeof(HistoryRequest))]
[JsonSerializable(typeof(HistoryResponse))]
[JsonSerializable(typeof(SessionSendRequest))]
[JsonSerializable(typeof(SessionAbortRequest))]
[JsonSerializable(typeof(SessionSteerRequest))]
[JsonSerializable(typeof(SessionCompactRequest))]
[JsonSerializable(typeof(SessionAbortRunRequest))]
[JsonSerializable(typeof(SessionQueueRequest))]
[JsonSerializable(typeof(SessionCancelQueueRequest))]
[JsonSerializable(typeof(SessionQueueInsertionView))]
[JsonSerializable(typeof(SessionReceiptRequest))]
[JsonSerializable(typeof(SessionAdmission))]
[JsonSerializable(typeof(SessionReceiptPage))]
[JsonSerializable(typeof(SessionDisplayRequest))]
[JsonSerializable(typeof(SessionDisplayItem))]
[JsonSerializable(typeof(SessionRuntimeStateRequest))]
[JsonSerializable(typeof(SessionRuntimeStateResponse))]
[JsonSerializable(typeof(SessionPermissionsRequest))]
[JsonSerializable(typeof(SessionPermissionsPage))]
[JsonSerializable(typeof(SessionPermissionResolveRequest))]
[JsonSerializable(typeof(SessionPermissionResolution))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionAsksRequest))]
[JsonSerializable(typeof(SessionAsksPage))]
[JsonSerializable(typeof(SessionAskActionRequest))]
[JsonSerializable(typeof(SessionAskObservationRequest))]
[JsonSerializable(typeof(SessionAskResult))]
[JsonSerializable(typeof(SessionNotesRequest))]
[JsonSerializable(typeof(SessionNotesResponse))]
[JsonSerializable(typeof(UserInputListRequest))]
[JsonSerializable(typeof(UserInputPage))]
[JsonSerializable(typeof(UserInputResolveRequest))]
[JsonSerializable(typeof(UserInputCancelRequest))]
[JsonSerializable(typeof(UserInputResult))]
internal sealed partial class DesktopJsonContext : JsonSerializerContext;
