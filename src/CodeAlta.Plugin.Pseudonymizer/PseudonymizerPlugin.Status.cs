using System.Collections.Concurrent;
using System.Globalization;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Pseudonymization;

namespace CodeAlta.Plugin.Pseudonymizer;

public sealed partial class PseudonymizerPlugin
{
    private const string DownloadCommand = "pseudonymizer-download";
    private const string HidePlacesKey = "hide_places";
    private const string HideOrganizationsKey = "hide_organizations";
    private const string HideCompanyNumbersKey = "hide_company_numbers";
    private const string HideDatesKey = "hide_all_dates";
    private const string HideUrlsKey = "hide_urls";

    // The sessions that sent nothing for want of their model: their status line offers the download. Only the sessions of
    // a secure project ask for a boundary, so the others never show it.
    private readonly ConcurrentDictionary<string, byte> _waitingForModels = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastState;

    /// <inheritdoc />
    public override ValueTask OnActivatedAsync(CancellationToken cancellationToken = default)
    {
        // Nothing is downloaded here: the window downloads the model of a language when a project in it is made secure.
        if (_models is null) return ValueTask.CompletedTask;
        _lastState = $"{_models.Download.State}:{_models.Download.Language}";
        _models.Changed += OnModelsChanged;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        if (_models is not null) _models.Changed -= OnModelsChanged;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override IEnumerable<PluginUiContribution> GetUiContributions()
    {
        yield return new PluginStatusContribution { Region = PluginUiRegion.SessionStatus, Name = "pseudonymizer", GetStatus = Status };
    }

    // The status line of a session: a download while it runs; for a session that waits for its model, the download to
    // start; then what the session hid. A session of a normal project shows nothing but a download.
    private PluginStatusItem? Status(PluginStatusContext context)
    {
        var download = _models?.Download ?? PseudonymizerDownload.Idle;
        if (download.State == PseudonymizerDownloadState.Downloading)
            return new PluginStatusItem { Label = "Pseudonymizer", Text = string.Create(CultureInfo.InvariantCulture, $"downloading the model {download.Percent} %"), Tone = PluginStatusTone.Info };
        if (context.SessionId is not { Length: > 0 } session) return null;
        if (_waitingForModels.ContainsKey(session) && _models is not null && !_models.IsReady(LanguageOf(context.ProjectId)))
            return new PluginStatusItem
            {
                Label = "Pseudonymizer",
                Text = download.State == PseudonymizerDownloadState.Failed ? "download failed: retry"
                    : string.Create(CultureInfo.InvariantCulture, $"download the model ({PseudonymizerLanguages.MegabytesOf(LanguageOf(context.ProjectId))} MB)"),
                Tone = PluginStatusTone.Warning,
                Command = DownloadCommand,
            };
        var hidden = _sessions.TryGetValue(session, out var pseudonyms) ? pseudonyms.Table.Count : 0;
        return hidden > 0
            ? new PluginStatusItem
            {
                Label = "Pseudonymizer",
                Text = string.Create(CultureInfo.InvariantCulture, $"{hidden} value{(hidden == 1 ? "" : "s")} hidden"),
                Tone = PluginStatusTone.Success,
                Command = "pseudonymizer-masked",
            }
            : null;
    }

    // A download that ends is said once, wherever the user is; the status lines read the store again at each change.
    private void OnModelsChanged(object? sender, EventArgs e)
    {
        var download = _models!.Download;
        var state = $"{download.State}:{download.Language}";
        var ended = download.State is PseudonymizerDownloadState.Done or PseudonymizerDownloadState.Failed && Interlocked.Exchange(ref _lastState, state) != state;
        if (download.State == PseudonymizerDownloadState.Downloading) Interlocked.Exchange(ref _lastState, state);
        if (download.State == PseudonymizerDownloadState.Done) _waitingForModels.Clear();
        Services.Ui.InvalidateContent();
        if (!ended) return;
        var model = PseudonymizerLanguages.PresetOf(download.Language)?.Name ?? "the model";
        var message = download.State == PseudonymizerDownloadState.Done
            ? $"Pseudonymizer: {model} is downloaded. The pseudonymized projects that use it now find names."
            : $"Pseudonymizer: {model} could not be downloaded ({download.Error}). The pseudonymized projects that use it send nothing until it is.";
        _ = Services.Ui.NotifyAsync(message, CancellationToken.None).AsTask();
    }

    // What is hidden, from the switches of the settings: people, contact details and identifiers always are, ordinary
    // dates are kept unless every date is hidden.
    private PiiPolicies Policies()
    {
        var policies = PiiPolicies.Default;
        foreach (var (key, hiddenByDefault, entities) in Switches)
        {
            // A host without settings answers null: the default of the switch applies, as Settings shows it.
            if ((Services.Settings.GetBoolean(key) ?? hiddenByDefault) == hiddenByDefault) continue;
            foreach (var entity in entities) policies = policies.With(entity, hiddenByDefault ? PiiPolicy.Keep : PiiPolicy.Pseudonymize);
        }

        return policies;
    }

    // The switches of "What is hidden": their key, whether they hide by default, and the kinds they decide.
    private static readonly (string Key, bool HiddenByDefault, string[] Entities)[] Switches =
    [
        (HidePlacesKey, true, [PiiEntities.Location]),
        (HideOrganizationsKey, true, [PiiEntities.Organization]),
        (HideCompanyNumbersKey, true, [PiiEntities.Siren, PiiEntities.SirenSuspect, PiiEntities.Siret, PiiEntities.SiretSuspect, PiiEntities.CompanyNumber, PiiEntities.Ein]),
        (HideDatesKey, false, [PiiEntities.Date]),
        (HideUrlsKey, false, [PiiEntities.Url]),
    ];
}
