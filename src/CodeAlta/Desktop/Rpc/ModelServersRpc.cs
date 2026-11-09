using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CodeAlta.Desktop.ModelServers;
using CodeAlta.ModelServers;
using CodeAlta.ModelServers.HuggingFace;
using CodeAlta.ModelServers.Store;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The model servers of this computer and of the paired machines, for Settings > Fleet > Model servers and the Discover
/// models tab: the machines, the status of one, the actions on its model host, its changes, the providers of a server, and
/// Hugging Face. Keys and connections stay in the host. The records of the model host go to the page as their JSON (the
/// one of the node API), which the page reads with its own types: their sizes are 64-bit integers.
/// </summary>
[NeoRpcService("modelServers", Version = 1)]
internal sealed class ModelServersService
{
    private const int MaximumText = 256 * 1024;
    private readonly DesktopModelServers? _servers;
    private readonly string? _epoch;

    internal ModelServersService() { }

    /// <summary>Creates the service over the model servers of the window.</summary>
    /// <param name="servers">The model servers.</param>
    /// <param name="epoch">The host epoch requests must name.</param>
    internal ModelServersService(DesktopModelServers servers, string epoch)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _servers = servers;
        _epoch = epoch;
    }

    /// <summary>Lists the machines whose model servers the window shows.</summary>
    [NeoRpcMethod("machines")]
    public async Task<ModelServersMachinesResponse> MachinesAsync(ModelServersRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, []);
        await _servers!.WatchMachinesAsync(cancellationToken).ConfigureAwait(false);
        return new("ok", [.. _servers.Machines()]);
    }

    /// <summary>Reads the model servers of a machine.</summary>
    [NeoRpcMethod("status")]
    public async Task<ModelServersJsonResponse> StatusAsync(ModelServersMachineRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (request!.Machine is not { Length: > 0 and <= 64 } machine) return new("invalid_request", null, null);
        try
        {
            var status = await _servers!.StatusAsync(machine, cancellationToken).ConfigureAwait(false);
            return new("ok", JsonSerializer.Serialize(status, ModelHostWireJson.Default.ModelHostStatus), null);
        }
        catch (ModelHostException exception)
        {
            return new(exception.Code, null, exception.Message);
        }
    }

    /// <summary>Asks a machine for an action on its model servers; the action and its result are the JSON of the node API.</summary>
    [NeoRpcMethod("act")]
    public async Task<ModelServersJsonResponse> ActAsync(ModelServersDoRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (request!.Machine is not { Length: > 0 and <= 64 } machine || request.Action is not { Length: > 0 and <= MaximumText } json) return new("invalid_request", null, null);
        ModelHostAction? action;
        try
        {
            action = JsonSerializer.Deserialize(json, ModelHostWireJson.Default.ModelHostAction);
        }
        catch (JsonException exception)
        {
            return new("invalid_request", null, exception.Message);
        }

        if (action is null) return new("invalid_request", null, null);
        try
        {
            var result = await _servers!.DoAsync(machine, action, "this computer", cancellationToken).ConfigureAwait(false);
            return new("ok", JsonSerializer.Serialize(result, ModelHostWireJson.Default.ModelHostActionResult), null);
        }
        catch (ModelHostException exception)
        {
            return new(exception.Code, null, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or HttpRequestException)
        {
            return new("failed", null, exception.Message);
        }
    }

    /// <summary>Adds the provider of a server on each machine named, so that the sessions there use it.</summary>
    [NeoRpcMethod("useInSessions")]
    public async Task<ModelServersUseResponse> UseInSessionsAsync(ModelServersUseRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, []);
        if (request!.Machine is not { Length: > 0 and <= 64 } machine || request.Server is not { Length: > 0 and <= 40 } server || request.Machines is not { Count: > 0 and <= 32 } machines)
            return new("invalid_request", []);
        var results = await _servers!.UseInSessionsAsync(machine, server, request.Name is { Length: > 0 and <= 120 } name ? name : server, machines, cancellationToken).ConfigureAwait(false);
        return new("ok", [.. results.Select(static pair => new ModelServersUseResult(pair.Key, pair.Value))]);
    }

    /// <summary>Searches the GGUF models of Hugging Face.</summary>
    [NeoRpcMethod("hubSearch")]
    public async Task<ModelServersJsonResponse> HubSearchAsync(ModelServersHubSearchRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (_servers!.Local is not { } local) return new("unavailable", null, null);
        try
        {
            var models = await local.Hub.SearchAsync(request!.Search?.Trim() is { Length: > 0 and <= 200 } search ? search : null, request.Author is { Length: > 0 and <= 96 } author ? author : null,
                request.Pipeline is { Length: > 0 and <= 64 } pipeline ? pipeline : null, request.Sort ?? "trendingScore", Math.Clamp(request.Limit, 1, 100), cancellationToken).ConfigureAwait(false);
            return new("ok", JsonSerializer.Serialize(models.ToList(), ModelServersPageJson.Default.ListHubModel), null);
        }
        catch (ModelHostException exception)
        {
            return new(exception.Code, null, exception.Message);
        }
    }

    /// <summary>Reads a model of Hugging Face: its details, its quantizations with whether each fits the machines, and its card.</summary>
    [NeoRpcMethod("hubModel")]
    public async Task<ModelServersJsonResponse> HubModelAsync(ModelServersHubModelRequest request, CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is { } refused) return new(refused, null, null);
        if (_servers!.Local is not { } local) return new("unavailable", null, null);
        try
        {
            var model = await _servers.ReadHubModelAsync(request!.Repo ?? "", [.. (request.Machines ?? []).Where(static machine => machine is { Length: > 0 and <= 64 }).Take(32)],
                cancellationToken).ConfigureAwait(false);
            var details = model.Details;
            var card = await local.Hub.GetCardAsync(details.Model.Id, details.Revision, cancellationToken).ConfigureAwait(false);
            var views = model.Quantizations.Select(static quant => new HubQuantView(quant.Tag, quant.Bits, quant.ModelBytes, quant.MmprojBytes, quant.Files,
                [.. quant.Fits.Select(static fit => new HubFitView(fit.Machine, fit.Fit, fit.OnDisk))])).ToList();
            var view = new HubModelView(details.Model, details.Revision, details.License, [.. details.BaseModels], details.Parameters, details.Architecture, details.ContextLength, views, card, model.Shape);
            return new("ok", JsonSerializer.Serialize(view, ModelServersPageJson.Default.HubModelView), null);
        }
        catch (ModelHostException exception)
        {
            return new(exception.Code, null, exception.Message);
        }
    }

    /// <summary>Says each time the model servers of a machine changed (the page reads them again), and when a download or an install ended.</summary>
    [NeoRpcMethod("watch")]
    public NeoRpcChannel<ModelServersEvent> Watch(ModelServersRequest request, CancellationToken cancellationToken)
        => new(WatchAsync(request, cancellationToken), DesktopJsonContext.Default.ModelServersEvent);

    internal async IAsyncEnumerable<ModelServersEvent> WatchAsync(ModelServersRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Refuse(request?.ExpectedEpoch) is not null) yield break;
        var changes = Channel.CreateBounded<ModelServersEvent>(new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        void OnChanged(string machine) => changes.Writer.TryWrite(new ModelServersEvent("changed", machine, null));
        void OnDownload(string machine, DownloadInfo download)
            => changes.Writer.TryWrite(new ModelServersEvent("download", machine, JsonSerializer.Serialize(download, ModelServersPageJson.Default.DownloadInfo)));
        void OnTask(string machine, HostTask task) => changes.Writer.TryWrite(new ModelServersEvent("task", machine, JsonSerializer.Serialize(task, ModelServersPageJson.Default.HostTask)));
        _servers!.Changed += OnChanged;
        _servers.DownloadEnded += OnDownload;
        _servers.TaskEnded += OnTask;
        try
        {
            await foreach (var value in changes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return value;
        }
        finally
        {
            _servers.Changed -= OnChanged;
            _servers.DownloadEnded -= OnDownload;
            _servers.TaskEnded -= OnTask;
        }
    }

    private string? Refuse(string? expectedEpoch)
        => _servers is null ? "unavailable" : !string.Equals(expectedEpoch, _epoch, StringComparison.Ordinal) ? "stale_epoch" : null;
}

internal sealed record ModelServersRequest(string? ExpectedEpoch);
internal sealed record ModelServersMachineRequest(string? ExpectedEpoch, string? Machine);
internal sealed record ModelServersDoRequest(string? ExpectedEpoch, string? Machine, string? Action);
internal sealed record ModelServersUseRequest(string? ExpectedEpoch, string? Machine, string? Server, string? Name, IReadOnlyList<string>? Machines);
internal sealed record ModelServersHubSearchRequest(string? ExpectedEpoch, string? Search, string? Author, string? Pipeline, string? Sort, int Limit);
internal sealed record ModelServersHubModelRequest(string? ExpectedEpoch, string? Repo, IReadOnlyList<string>? Machines);
internal sealed record ModelServersMachinesResponse(string Status, IReadOnlyList<ModelServersMachine> Machines);
internal sealed record ModelServersJsonResponse(string Status, string? Json, string? Message);
internal sealed record ModelServersUseResult(string Machine, string? Problem);
internal sealed record ModelServersUseResponse(string Status, IReadOnlyList<ModelServersUseResult> Results);
internal sealed record ModelServersEvent(string Kind, string Machine, string? Json);

/// <summary>Whether a quantization fits a machine, as the page shows it.</summary>
public sealed record HubFitView(string Machine, FitResult Fit, bool OnMachine);

/// <summary>A quantization of a model of Hugging Face, as the page shows it.</summary>
public sealed record HubQuantView(string Tag, int Bits, long ModelBytes, long MmprojBytes, int Files, IReadOnlyList<HubFitView> Fits);

/// <summary>A model of Hugging Face, as the page shows it.</summary>
public sealed record HubModelView(HubModel Model, string Revision, string? License, IReadOnlyList<string> BaseModels, long? Parameters, string? Architecture, int? ContextLength,
    IReadOnlyList<HubQuantView> Quantizations, string Card, GgufShape? Shape = null);

/// <summary>The JSON of the model servers for the page.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<HubModel>))]
[JsonSerializable(typeof(HubModelView))]
[JsonSerializable(typeof(DownloadInfo))]
[JsonSerializable(typeof(HostTask))]
internal sealed partial class ModelServersPageJson : JsonSerializerContext;
