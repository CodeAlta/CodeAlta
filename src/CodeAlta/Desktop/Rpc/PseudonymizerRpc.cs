using CodeAlta.Plugin.Pseudonymizer;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// The models that find names in the documents of secure projects, for the window: the languages a project chooses
/// with the size of their model and whether it is downloaded, the download that runs, and the deletion of a model. The
/// Pseudonymizer plugin runs the same models: a download started here shows in the status line of its sessions.
/// Nothing is downloaded before <c>install</c> is called.
/// </summary>
[NeoRpcService("pseudonymizer", Version = 1)]
internal sealed class PseudonymizerService
{
    private readonly PseudonymizerModelStore? _models;
    private readonly string? _epoch;

    /// <summary>Creates the service of a window that runs no model.</summary>
    internal PseudonymizerService() { }

    /// <summary>Creates the service over the models of the application.</summary>
    /// <param name="models">The models, which the plugin shares.</param>
    /// <param name="epoch">The epoch of the host.</param>
    /// <exception cref="ArgumentNullException"><paramref name="models"/> is null.</exception>
    internal PseudonymizerService(PseudonymizerModelStore models, string epoch)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentException.ThrowIfNullOrWhiteSpace(epoch);
        _models = models;
        _epoch = epoch;
    }

    /// <summary>Reads the languages, their models and the download that runs or last ended.</summary>
    [NeoRpcMethod("models")]
    public PseudonymizerModelsResponse Models(PseudonymizerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Refuse(request.ExpectedEpoch) ?? Read("ok");
    }

    /// <summary>Downloads the model of a language, after the one that runs; nothing when it is there.</summary>
    [NeoRpcMethod("install")]
    public PseudonymizerModelsResponse Install(PseudonymizerLanguageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch, request.Language ?? string.Empty) is { } refused) return refused;
        _models!.Install(request.Language);
        return Read("ok");
    }

    /// <summary>Deletes the model of a language from this computer: the secure projects in that language send nothing until it is downloaded again.</summary>
    [NeoRpcMethod("delete")]
    public PseudonymizerModelsResponse Delete(PseudonymizerLanguageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request.ExpectedEpoch, request.Language ?? string.Empty) is { } refused) return refused;
        try
        {
            return Read(_models!.Delete(request.Language) ? "ok" : "busy");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Read("delete_failed");
        }
    }

    // Why a request is refused: no models in this window, a stale epoch, or a language that is not offered; null to serve it.
    private PseudonymizerModelsResponse? Refuse(string? epoch, string? language = null)
    {
        if (_models is null) return new("unavailable", [], null);
        if (!string.Equals(epoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", [], null);
        return language is null || PseudonymizerLanguages.Offered.Contains(language) ? null : Read("invalid");
    }

    private PseudonymizerModelsResponse Read(string status)
    {
        var download = _models!.Download;
        return new(status, [.. _models.Languages.Select(static language => new PseudonymizerLanguageEntry(language.Id, language.Model, language.SizeMegabytes, language.Installed))],
            new PseudonymizerDownloadEntry(download.State.ToString().ToLowerInvariant(), download.Language, download.Percent, download.Error));
    }
}

/// <param name="ExpectedEpoch">The epoch of the host the window reads.</param>
internal sealed record PseudonymizerRequest(string? ExpectedEpoch);

/// <param name="ExpectedEpoch">The epoch of the host the window reads.</param>
/// <param name="Language">One of the languages offered: <c>french</c>, <c>english</c>, <c>multilingual</c>, <c>identifiers</c>.</param>
internal sealed record PseudonymizerLanguageRequest(string? ExpectedEpoch, string? Language);

/// <param name="Id">The language.</param>
/// <param name="Model">The name of its model; null for identifiers only.</param>
/// <param name="SizeMegabytes">The megabytes of its download.</param>
/// <param name="Installed">Whether it can run now.</param>
internal sealed record PseudonymizerLanguageEntry(string Id, string? Model, int SizeMegabytes, bool Installed);

/// <param name="State"><c>idle</c>, <c>downloading</c>, <c>done</c> or <c>failed</c>.</param>
/// <param name="Language">The language whose model is or was downloaded.</param>
/// <param name="Percent">The percent received.</param>
/// <param name="Error">Why it failed.</param>
internal sealed record PseudonymizerDownloadEntry(string State, string? Language, int Percent, string? Error);

/// <param name="Status"><c>ok</c>, or why nothing was done: <c>unavailable</c>, <c>stale_epoch</c>, <c>invalid</c>, <c>busy</c>, <c>delete_failed</c>.</param>
/// <param name="Languages">The languages, in their order.</param>
/// <param name="Download">The download that runs or last ended.</param>
internal sealed record PseudonymizerModelsResponse(string Status, PseudonymizerLanguageEntry[] Languages, PseudonymizerDownloadEntry? Download);