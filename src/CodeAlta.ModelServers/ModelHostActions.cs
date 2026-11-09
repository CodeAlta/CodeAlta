using System.Text.Json.Serialization;
using CodeAlta.ModelServers.Servers;

namespace CodeAlta.ModelServers;

/// <summary>A repository of Hugging Face as a model host reads it.</summary>
/// <param name="Details">Its details.</param>
/// <param name="Quantizations">Its quantizations.</param>
/// <param name="Shape">The shape of its model, from the header of a GGUF file; null when it could not be read.</param>
public sealed record RepoInspection(CodeAlta.ModelServers.HuggingFace.HubModelDetails Details, IReadOnlyList<CodeAlta.ModelServers.Store.QuantFiles> Quantizations,
    CodeAlta.ModelServers.Store.GgufShape? Shape);

/// <summary>One thing asked of a model host: the same words for the page, the node API and <c>alta llama</c>.</summary>
public sealed record ModelHostAction
{
    /// <summary>Gets what is asked: <c>refreshHardware</c>, <c>checkLatest</c>, <c>installBuilds</c>, <c>addOwnBuild</c>, <c>removeBuild</c>,
    /// <c>useBuild</c>, <c>setSettings</c>, <c>planUpdate</c>, <c>cancelPlanned</c>, <c>updateNow</c>, <c>download</c>, <c>controlDownload</c>,
    /// <c>removeModel</c>, <c>rescan</c>, <c>checkModelUpdates</c>, <c>measureFit</c>, <c>setServer</c>, <c>removeServer</c>, <c>controlServer</c>, <c>testServer</c>,
    /// <c>serverLog</c> or <c>import</c>.</summary>
    public required string Action { get; init; }

    /// <summary>Gets the backends, for <c>installBuilds</c>.</summary>
    public IReadOnlyList<string> Backends { get; init; } = [];

    /// <summary>Gets a backend.</summary>
    public string? Backend { get; init; }

    /// <summary>Gets a build (<c>b12110</c>).</summary>
    public string? Build { get; init; }

    /// <summary>Gets a folder (of an own build).</summary>
    public string? Folder { get; init; }

    /// <summary>Gets a model, <c>org/repo:TAG</c>.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the id of a server, a download or a task.</summary>
    public string? Id { get; init; }

    /// <summary>Gets a verb: <c>pause</c>, <c>resume</c>, <c>cancel</c>, <c>clear</c> for a download; <c>start</c>, <c>stop</c>, <c>restart</c>, <c>load</c>, <c>unload</c> for a server.</summary>
    public string? Verb { get; init; }

    /// <summary>Gets whether running servers may be stopped, for <c>removeModel</c>.</summary>
    public bool Stop { get; init; }

    /// <summary>Gets whether models are updated too, for an update.</summary>
    public bool Models { get; init; }

    /// <summary>Gets when, for <c>planUpdate</c>.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>Gets the settings, for <c>setSettings</c>.</summary>
    public ModelHostSettings? Settings { get; init; }

    /// <summary>Gets the server, for <c>setServer</c>.</summary>
    public ServerDefinition? Server { get; init; }

    /// <summary>Gets a text: the command line of <c>import</c>.</summary>
    public string? Text { get; init; }

    /// <summary>Gets a name: of the server <c>import</c> gives.</summary>
    public string? Name { get; init; }

    /// <summary>Gets how many lines, for <c>serverLog</c>.</summary>
    public int Lines { get; init; } = 200;
}

/// <summary>What an action gave.</summary>
public sealed record ModelHostActionResult
{
    /// <summary>Gets a server.</summary>
    public ServerView? Server { get; init; }

    /// <summary>Gets a download.</summary>
    public DownloadInfo? Download { get; init; }

    /// <summary>Gets a task (an install or an update) that runs on.</summary>
    public HostTask? Task { get; init; }

    /// <summary>Gets the result of a test.</summary>
    public ServerTestResult? Test { get; init; }

    /// <summary>Gets lines of a log.</summary>
    public IReadOnlyList<string>? Log { get; init; }

    /// <summary>Gets the server an import gives, not saved.</summary>
    public ServerDefinition? Imported { get; init; }

    /// <summary>Gets what an import did not take, or other notes.</summary>
    public IReadOnlyList<string>? Notes { get; init; }

    /// <summary>Gets the bytes freed.</summary>
    public long? Freed { get; init; }

    /// <summary>Gets the update planned.</summary>
    public PlannedUpdate? Planned { get; init; }

    /// <summary>Gets models: those with an update.</summary>
    public IReadOnlyList<string>? ModelIds { get; init; }

    /// <summary>Gets a build.</summary>
    public InstalledBuild? Build { get; init; }

    /// <summary>Gets the hardware.</summary>
    public HardwareInventory? Hardware { get; init; }

    /// <summary>Gets the build of the channel.</summary>
    public LlamaRelease? Latest { get; init; }

    /// <summary>Gets how a model fits the GPUs of the machine, as measured there.</summary>
    public MeasuredFit? Measured { get; init; }
}

/// <summary>Runs the actions of a model host.</summary>
public static class ModelHostActions
{
    /// <summary>Whether an action only reads (a device that may view the machine may ask it).</summary>
    /// <param name="action">The action.</param>
    /// <returns>Whether it only reads.</returns>
    public static bool IsRead(string action) => action is "serverLog" or "checkModelUpdates";

    /// <summary>Whether an action downloads code or starts a program: refused to a session without the shell, or whose commands are reviewed.</summary>
    /// <param name="action">The action.</param>
    /// <returns>Whether it does.</returns>
    public static bool StartsPrograms(ModelHostAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Action is "installBuilds" or "addOwnBuild" or "useBuild" or "planUpdate" or "updateNow" or "testServer" or "measureFit"
            || action.Action == "setServer" && action.Server?.Enabled != false
            || action.Action == "controlServer" && action.Verb is "start" or "restart" or "load";
    }

    /// <summary>Runs an action.</summary>
    /// <param name="host">The model host.</param>
    /// <param name="action">The action.</param>
    /// <param name="requestedBy">Who asks, for the records.</param>
    /// <param name="cancellationToken">Ends the action.</param>
    /// <returns>What it gave.</returns>
    /// <exception cref="ModelHostException">It is refused or fails.</exception>
    public static async Task<ModelHostActionResult> RunAsync(ModelHost host, ModelHostAction action, string? requestedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(action);
        string Required(string? value, string what) => value is { Length: > 0 } ? value : throw new ModelHostException("invalid", $"'{action.Action}' needs {what}.");
        switch (action.Action)
        {
            case "refreshHardware":
                return new() { Hardware = await host.RefreshHardwareAsync(cancellationToken).ConfigureAwait(false) };
            case "checkLatest":
                return new() { Latest = await host.CheckLatestAsync(force: true, cancellationToken).ConfigureAwait(false) };
            case "installBuilds":
                return new() { Task = host.InstallBuilds(action.Backends, action.Build, requestedBy) };
            case "addOwnBuild":
                var own = await host.Builds.AddOwnAsync(Required(action.Folder, "a folder"), Required(action.Backend, "a backend"), cancellationToken).ConfigureAwait(false);
                host.NotifyChanged();
                return new() { Build = own };
            case "removeBuild":
                return new() { Freed = host.RemoveBuild(Required(action.Backend, "a backend"), Required(action.Build, "a build")) };
            case "useBuild":
                await host.UseBuildAsync(Required(action.Backend, "a backend"), Required(action.Build, "a build"), cancellationToken).ConfigureAwait(false);
                return new() { Build = host.Builds.Find(action.Backend!, action.Build) };
            case "setSettings":
                host.SetSettings(action.Settings ?? throw new ModelHostException("invalid", "'setSettings' needs the settings."));
                return new();
            case "planUpdate":
                return new() { Planned = host.PlanUpdate(action.At ?? throw new ModelHostException("invalid", "'planUpdate' needs a time."), action.Build, action.Models, requestedBy ?? "") };
            case "cancelPlanned":
                host.CancelPlannedUpdate();
                return new();
            case "updateNow":
                return new() { Task = host.UpdateNow(action.Build, action.Models, requestedBy) };
            case "download":
                return new() { Download = host.Download(Required(action.Model, "a model"), requestedBy) };
            case "controlDownload":
                return new() { Download = host.ControlDownload(Required(action.Id, "a download"), Required(action.Verb, "a verb")) };
            case "removeModel":
                return new() { Freed = await host.RemoveModelAsync(Required(action.Model, "a model"), action.Stop, cancellationToken).ConfigureAwait(false) };
            case "rescan":
                host.RescanModels();
                return new();
            case "measureFit":
                return new() { Measured = await host.MeasureFitAsync(Required(action.Model, "a model"), cancellationToken).ConfigureAwait(false)
                    ?? throw new ModelHostException("not_found", $"{action.Model} is not on this machine, or no build of llama.cpp is installed to measure it.") };
            case "checkModelUpdates":
                return new() { ModelIds = await host.CheckModelUpdatesAsync(cancellationToken).ConfigureAwait(false) };
            case "setServer":
                return new() { Server = await host.SetServerAsync(action.Server ?? throw new ModelHostException("invalid", "'setServer' needs the server."), cancellationToken).ConfigureAwait(false) };
            case "removeServer":
                await host.RemoveServerAsync(Required(action.Id, "a server"), cancellationToken).ConfigureAwait(false);
                return new();
            case "controlServer":
                return new() { Server = await host.ControlServerAsync(Required(action.Id, "a server"), Required(action.Verb, "a verb"), action.Model, cancellationToken).ConfigureAwait(false) };
            case "testServer":
                return new() { Test = await host.TestServerAsync(Required(action.Id, "a server"), action.Model, cancellationToken).ConfigureAwait(false) };
            case "serverLog":
                return new() { Log = host.ReadServerLog(Required(action.Id, "a server"), action.Lines) };
            case "import":
                var (definition, ignored) = host.Import(Required(action.Text, "a command line"), action.Name);
                return new() { Imported = definition, Notes = ignored };
            default:
                throw new ModelHostException("invalid", $"'{action.Action}' is not an action of a model host.");
        }
    }
}

/// <summary>The JSON of the actions on the wire, without indentation.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ModelHostAction))]
[JsonSerializable(typeof(ModelHostActionResult))]
[JsonSerializable(typeof(ModelHostStatus))]
public sealed partial class ModelHostWireJson : JsonSerializerContext;
