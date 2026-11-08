using CodeAlta.ModelServers;
using CodeAlta.ModelServers.HuggingFace;

namespace CodeAlta.LiveTool;

/// <summary>
/// The llama.cpp servers of this computer and of the machines it manages (<c>doc/model-servers.md</c>), for the
/// <c>alta llama</c> commands: their builds, models, downloads, servers and updates, and the models of Hugging Face.
/// </summary>
public interface IAltaModelServers
{
    /// <summary>Lists the machines: this computer (<c>local</c>) when it runs a model host, then the paired machines.</summary>
    /// <param name="cancellationToken">Ends the listing.</param>
    /// <returns>The machines.</returns>
    Task<IReadOnlyList<AltaModelServersMachine>> ListMachinesAsync(CancellationToken cancellationToken);

    /// <summary>Reads the model host of a machine.</summary>
    /// <param name="machine">The machine: <c>local</c>, or the id or the name of a paired machine.</param>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>Its status.</returns>
    /// <exception cref="ModelHostException">The machine is not known, cannot be reached or runs no model host.</exception>
    Task<ModelHostStatus> StatusAsync(string machine, CancellationToken cancellationToken);

    /// <summary>
    /// Asks a machine for an action; a download, an install or an update it starts tells <paramref name="sessionId"/> its end
    /// with a prompt.
    /// </summary>
    /// <param name="machine">The machine.</param>
    /// <param name="action">The action.</param>
    /// <param name="sessionId">The session that asks, told the end of what it starts; null for none.</param>
    /// <param name="cancellationToken">Ends the action.</param>
    /// <returns>What it gave.</returns>
    /// <exception cref="ModelHostException">It is refused or fails.</exception>
    Task<ModelHostActionResult> RunAsync(string machine, ModelHostAction action, string? sessionId, CancellationToken cancellationToken);

    /// <summary>Searches the GGUF models of Hugging Face.</summary>
    /// <param name="search">Words of their names, or null.</param>
    /// <param name="author">Their publisher, or null.</param>
    /// <param name="pipeline">Their kind (<c>text-generation</c>...), or null.</param>
    /// <param name="sort"><c>trendingScore</c>, <c>downloads</c>, <c>likes</c> or <c>lastModified</c>.</param>
    /// <param name="limit">How many, at most.</param>
    /// <param name="cancellationToken">Ends the search.</param>
    /// <returns>The models.</returns>
    /// <exception cref="ModelHostException">Hugging Face cannot be reached.</exception>
    Task<IReadOnlyList<HubModel>> SearchHubAsync(string? search, string? author, string? pipeline, string sort, int limit, CancellationToken cancellationToken);

    /// <summary>Reads a model of Hugging Face, with its quantizations and whether each fits the machines named.</summary>
    /// <param name="repo">The repository: <c>publisher/name</c>.</param>
    /// <param name="machines">The machines to tell the fit for.</param>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>The model.</returns>
    /// <exception cref="ModelHostException">The repository is not found or has no GGUF file.</exception>
    Task<AltaHubModel> ReadHubModelAsync(string repo, IReadOnlyList<string> machines, CancellationToken cancellationToken);

    /// <summary>Adds the provider of a server, so that the sessions of the machines named use it.</summary>
    /// <param name="machine">The machine of the server.</param>
    /// <param name="server">The server.</param>
    /// <param name="name">The name of the provider.</param>
    /// <param name="machines">The machines whose sessions use it: <c>local</c> for this computer, or the machine of the server.</param>
    /// <param name="cancellationToken">Ends the changes.</param>
    /// <returns>The key of the provider, and for each machine null when done or why not.</returns>
    Task<(string Provider, IReadOnlyDictionary<string, string?> Results)> UseInSessionsAsync(string machine, string server, string name, IReadOnlyList<string> machines,
        CancellationToken cancellationToken);
}

/// <summary>A machine of the model servers.</summary>
/// <param name="Id">Its id: <c>local</c> for this computer, the device id of a paired machine.</param>
/// <param name="Name">Its name.</param>
/// <param name="Local">Whether it is this computer.</param>
/// <param name="State"><c>online</c>, <c>offline</c>...</param>
/// <param name="Manages">Whether this computer may change its model servers.</param>
/// <param name="Uses">Whether this computer may use its model servers.</param>
/// <param name="Problem">Why it cannot be reached, or null.</param>
public sealed record AltaModelServersMachine(string Id, string Name, bool Local, string State, bool Manages, bool Uses, string? Problem);

/// <summary>A model of Hugging Face, with its quantizations.</summary>
/// <param name="Details">Its details.</param>
/// <param name="Quantizations">Its quantizations, with their fit on each machine named.</param>
public sealed record AltaHubModel(HubModelDetails Details, IReadOnlyList<AltaHubQuantization> Quantizations);

/// <summary>A quantization of a model of Hugging Face.</summary>
/// <param name="Tag">Its tag (<c>Q4_K_M</c>).</param>
/// <param name="Bits">Its bits per weight, about.</param>
/// <param name="ModelBytes">The size of its model files.</param>
/// <param name="MmprojBytes">The size of its vision projector, 0 for none.</param>
/// <param name="Files">How many files it has.</param>
/// <param name="Fits">Whether it fits each machine named, and whether it is on its disk.</param>
public sealed record AltaHubQuantization(string Tag, int Bits, long ModelBytes, long MmprojBytes, int Files, IReadOnlyList<AltaHubFit> Fits);

/// <summary>Whether a quantization fits a machine.</summary>
/// <param name="Machine">The machine.</param>
/// <param name="Fit">How it fits its GPUs.</param>
/// <param name="OnDisk">Whether it is on its disk already.</param>
public sealed record AltaHubFit(string Machine, CodeAlta.ModelServers.Store.FitResult Fit, bool OnDisk);
