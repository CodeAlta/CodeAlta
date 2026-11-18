using System.Collections.Concurrent;
using CodeAlta.Agent.Runtime;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Pseudonymization;

namespace CodeAlta.Plugin.Pseudonymizer;

/// <summary>
/// Pseudonymizes what the confidential sessions send to their providers: the personal data a request holds (French
/// identifiers, emails, IBAN, names...) is replaced by pseudonyms such as <c>[PERSON_001]</c> before it leaves the
/// machine, and the answers get the real values back. The pseudonyms of each session are kept on this machine only,
/// encrypted. The sessions of a secure project are confidential (<c>[privacy] pseudonymize</c> of the project); the
/// language of its documents chooses the model that finds names.
/// </summary>
[Plugin(PluginId, DisplayName = "Pseudonymizer", Description = "Pseudonymizes what confidential sessions send to their providers, and puts the real values back in the answers.", Frontends = PluginFrontends.Desktop)]
public sealed partial class PseudonymizerPlugin : PluginBase
{
    /// <summary>The id of the plugin: <c>[plugins.pseudonymizer]</c> in the configuration.</summary>
    public const string PluginId = "pseudonymizer";

    /// <summary>The key of the setting of each project that names the language of its documents.</summary>
    public const string LanguageKey = "language";

    /// <summary>The key of the setting of each project that lists the names it always hides.</summary>
    public const string AlwaysHideKey = "always_hide";

    /// <summary>The key of the setting of each project that lists the words it never hides.</summary>
    public const string NeverHideKey = "never_hide";

    /// <summary>The key of the setting of the user that names the folder of a model of theirs.</summary>
    public const string CustomModelKey = "custom_model";

    /// <summary>The key of the setting of the user that lists the tools that get pseudonyms.</summary>
    public const string NetworkToolsKey = "network_tools";

    /// <summary>
    /// The tools that get pseudonyms at first: those that send their arguments away from this machine, the web tools
    /// (<see cref="CodeAlta.Agent.AgentToolPatterns.WebAlias"/>) and the pages a browser opens.
    /// </summary>
    internal static IReadOnlyList<string> DefaultNetworkTools { get; } = [CodeAlta.Agent.AgentToolPatterns.WebAlias, "navigate_page", "new_page"];

    /// <summary>The key of the setting of the user that lists the words no project hides.</summary>
    public const string SafeWordsKey = "safe_words";

    /// <summary>
    /// The words no project hides at first: those CodeAlta's own instructions put in every request (its name, the
    /// address of the trailer of its commits), which name no one. The user changes the list in Settings.
    /// </summary>
    internal static IReadOnlyList<string> DefaultSafeWords { get; } = ["CodeAlta", "noreply@codealta.io"];

    private readonly ConcurrentDictionary<string, SessionPseudonyms> _sessions = new(StringComparer.OrdinalIgnoreCase);
    // The recognizers of the identifiers of each language, made once: they hold no state, and some forty patterns are
    // compiled for each set.
    private readonly ConcurrentDictionary<string, IReadOnlyList<PiiRecognizer>> _identifiers = new(StringComparer.Ordinal);
    private readonly Func<string, ISecretProtector> _protector;
    private readonly Func<string, string?, PiiRecognizer?>? _names;
    private readonly PseudonymizerModelStore? _models;

    /// <summary>Creates the plugin without models: only identifiers are found, and a project that asks for names sends nothing.</summary>
    public PseudonymizerPlugin()
        : this(LocalSecretStore.ForThisMachine, names: null)
    {
    }

    /// <summary>Creates the plugin with the models the application keeps.</summary>
    /// <param name="models">The models, which the window shares.</param>
    /// <exception cref="ArgumentNullException"><paramref name="models"/> is <see langword="null"/>.</exception>
    public PseudonymizerPlugin(PseudonymizerModelStore models)
        : this(LocalSecretStore.ForThisMachine, names: null, models ?? throw new ArgumentNullException(nameof(models)))
    {
    }

    /// <summary>Creates the plugin over what protects its files and what finds names.</summary>
    /// <param name="protector">Makes the protection of the files of a folder.</param>
    /// <param name="names">Gives the recognizer of a language and the folder of a custom model; null for the models of the store.</param>
    /// <param name="models">The models, or null.</param>
    internal PseudonymizerPlugin(Func<string, ISecretProtector> protector, Func<string, string?, PiiRecognizer?>? names, PseudonymizerModelStore? models = null)
    {
        ArgumentNullException.ThrowIfNull(protector);
        _protector = protector;
        _names = names;
        _models = models;
    }

    private string SessionsDirectory => Path.Combine(Services.State.GetDirectory(PluginStateScope.User), "sessions");

    private string SessionPath(string sessionId) => Path.Combine(SessionsDirectory, Sanitize(sessionId) + ".bin");

    /// <inheritdoc />
    public override IEnumerable<PluginSettingContribution> GetSettings()
    {
        // What each secure project chooses: the window sets them when it makes a project secure, and in Project security.
        yield return Setting.Choice(LanguageKey, "Language of the documents",
            "Chooses the model that finds names, on this computer: only the models of the languages of pseudonymized projects are downloaded.",
            PseudonymizerLanguages.All, PseudonymizerLanguages.Default) with
        {
            Order = 0,
            Scope = PluginSettingScope.Project,
            Group = "Project",
            Choices =
            [
                new(PseudonymizerLanguages.French, "French"),
                new(PseudonymizerLanguages.English, "English"),
                new(PseudonymizerLanguages.Multilingual, "Several languages"),
                new(PseudonymizerLanguages.Identifiers, "Identifiers only"),
                new(PseudonymizerLanguages.Custom, "Custom model"),
            ],
            Keywords = ["ner", "model", "names", "camembert", "bert", "english", "french", "language"],
        };
        yield return Setting.List(AlwaysHideKey, "Names always hidden",
            "Names hidden wherever they appear, even where nothing finds them: the parties of a file. One per line.") with
        { Order = 1, Scope = PluginSettingScope.Project, Group = "Project", Keywords = ["names", "clients", "deny list"] };
        yield return Setting.List(NeverHideKey, "Words never hidden in this project",
            "Words this project sends as they are, which were taken for personal data by mistake. One per line.") with
        { Order = 2, Scope = PluginSettingScope.Project, Group = "Project", Keywords = ["allow list", "false positive"] };
        yield return Setting.List(SafeWordsKey, "Words never hidden",
            "Words every project sends as they are: names of products or of your firm that name no one. Each project adds its own in Pseudonymization.",
            DefaultSafeWords) with
        { Order = 15, Group = "What is hidden", Keywords = ["allow list", "false positive", "safe words"] };

        // What is hidden, in every secure project. People, contact details and identifiers always are.
        yield return Setting.Boolean(HidePlacesKey, "Places", "Streets, towns, regions and countries the model finds. Postal codes are always hidden.", true) with
        { Order = 10, Group = "What is hidden", Keywords = ["location", "town", "city"] };
        yield return Setting.Boolean(HideOrganizationsKey, "Organizations", "Companies, firms and administrations the model finds.", true) with
        { Order = 11, Group = "What is hidden", Keywords = ["company", "firm"] };
        yield return Setting.Boolean(HideCompanyNumbersKey, "Company numbers", "SIREN and SIRET in France, company numbers of Companies House in the UK, EIN in the US.", true) with
        { Order = 12, Group = "What is hidden", Keywords = ["siren", "siret"] };
        yield return Setting.Boolean(HideDatesKey, "Every date", "Off, only dates of birth are hidden: the dates of acts and meetings are sent as they are.", false) with
        { Order = 13, Group = "What is hidden", Keywords = ["dates", "birth"] };
        yield return Setting.Boolean(HideUrlsKey, "Web addresses", "Off, web addresses are sent as they are: links to documentation, to a repository, to a site.", false) with
        { Order = 14, Group = "What is hidden", Keywords = ["url", "link", "website"] };

        yield return Setting.Path(CustomModelKey, "Custom model",
            "The folder of a token classification model of yours (BERT, RoBERTa, XLM-RoBERTa): its ONNX graph, its tokenizer (SentencePiece or WordPiece), its config.json and a ner-model.json that describes it. Used by the projects whose language is Custom model.") with
        { Order = 20, Group = "Advanced", Keywords = ["onnx", "ner", "model"] };
        yield return Setting.List(NetworkToolsKey, "Tools that get pseudonyms",
            "The tools that send their arguments away from this machine get the pseudonyms rather than the real values. * stands for any characters, and web names every web tool.",
            DefaultNetworkTools) with
        { Order = 21, Group = "Advanced", Keywords = ["web", "browser", "mcp"] };
    }

    /// <inheritdoc />
    public override IAgentProviderBoundary? GetProviderBoundary(PluginProviderBoundaryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var language = LanguageOf(context.ProjectId);
        var recognizers = RecognizersOf(language, out var failure);
        if (failure is not null)
        {
            // The status line of the session offers the download from then on: it tells why nothing was sent.
            _waitingForModels.TryAdd(context.SessionId, 0);
            Services.Ui.InvalidateContent();
            throw new AgentProviderBoundaryException(NotReadyMessage(language, failure), failure);
        }

        var options = Options(context.ProjectId);
        var session = _sessions.GetOrAdd(context.SessionId, id => SessionPseudonyms.Load(SessionPath(id), _protector(SessionsDirectory)));
        return session.Boundary(new PiiAnalyzer(recognizers), options);
    }

    /// <inheritdoc />
    public override IEnumerable<PluginCommandContribution> GetCommands()
    {
        foreach (var command in ShowingCommands()) yield return command;
        if (_models is null) yield break;
        yield return new PluginCommandContribution
        {
            Name = DownloadCommand,
            Label = "Pseudonymizer: download the model of this project",
            Description = "Downloads the model that finds names in the language of the documents of this pseudonymized project.",
            SearchText = "pseudonymizer download install model ner camembert bert onnx",
            Availability = PluginCommandAvailability.SessionSelected,
            Handler = (context, _) =>
            {
                var language = LanguageOf(context.ProjectId);
                var message = _models.Install(language)
                    ? "Pseudonymizer: the download of the model started."
                    : "Pseudonymizer: the model of this project is downloaded already.";
                return ValueTask.FromResult(PluginCommandResult.Handled with { UserMessage = message });
            },
        };
    }

    /// <summary>Gets the pseudonyms of a session, as this process holds them; null when it holds none.</summary>
    /// <param name="sessionId">The session.</param>
    /// <returns>The pseudonyms, or null.</returns>
    internal IReadOnlyList<PseudonymEntry>? EntriesOf(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session.Table.Entries : null;

    // The language of the documents of a project; the default for a project that names none.
    private string LanguageOf(string? projectId)
        => (projectId is null ? null : Services.Settings.GetString(LanguageKey, projectId)) is { } language && PseudonymizerLanguages.All.Contains(language)
            ? language : PseudonymizerLanguages.Default;

    // The recognizers of the identifiers of a language, shared by the requests.
    private IReadOnlyList<PiiRecognizer> IdentifiersOf(string language)
        => _identifiers.GetOrAdd(language, static key => PseudonymizerLanguages.IdentifierRecognizers(key));

    // The recognizers of a language: its identifiers, and its model of names when it has one. A model that cannot run is
    // the failure, and the identifiers alone are given: the boundary refuses, a try says so.
    private List<PiiRecognizer> RecognizersOf(string language, out InvalidOperationException? failure)
    {
        var recognizers = new List<PiiRecognizer>(IdentifiersOf(language));
        failure = null;
        try
        {
            if (Names(language) is { } names) recognizers.Add(names);
        }
        catch (InvalidOperationException exception)
        {
            failure = exception;
        }

        return recognizers;
    }

    // The recognizer of names of a language; null for identifiers only.
    private PiiRecognizer? Names(string language)
    {
        var custom = Services.Settings.GetString(CustomModelKey);
        if (_names is not null) return _names(language, custom);
        if (language == PseudonymizerLanguages.Identifiers) return null;
        return _models?.Recognizer(language, custom) ?? throw new InvalidOperationException("This application runs no model.");
    }

    private static string NotReadyMessage(string language, InvalidOperationException exception)
        => PseudonymizerLanguages.PresetOf(language) is { } preset
            ? $"This project is pseudonymized and finds names with {preset.Name}, which is not downloaded yet ({PseudonymizerLanguages.MegabytesOf(language)} MB): download it from the status line of this session. Nothing was sent."
            : $"This project is pseudonymized and its model that finds names cannot run ({exception.Message}). Nothing was sent.";

    private PseudonymizerOptions Options(string? projectId)
    {
        var networkTools = Services.Settings.GetList(NetworkToolsKey) ?? DefaultNetworkTools;
        return new PseudonymizerOptions
        {
            Policies = Policies(),
            AlwaysHide = [.. (projectId is null ? [] : Services.Settings.GetList(AlwaysHideKey, projectId) ?? []).Select(ParseValue).OfType<(string, string)>()],
            NeverHide = new HashSet<string>((Services.Settings.GetList(SafeWordsKey) ?? DefaultSafeWords)
                .Concat(projectId is null ? [] : Services.Settings.GetList(NeverHideKey, projectId) ?? []).Select(static value => value.Trim()), StringComparer.OrdinalIgnoreCase),
            ToolReceivesRealValues = tool => !CodeAlta.Agent.AgentToolPatterns.IsDisabled(tool, networkTools),
        };
    }

    private static (string Entity, string Value)? ParseValue(string line)
    {
        var text = line.Trim();
        if (text.Length == 0) return null;
        var separator = text.IndexOf(':', StringComparison.Ordinal);
        if (separator > 0 && PiiEntities.All.Contains(text[..separator].Trim().ToUpperInvariant()))
        {
            var value = text[(separator + 1)..].Trim();
            return value.Length == 0 ? null : (text[..separator].Trim().ToUpperInvariant(), value);
        }

        return (PiiEntities.Person, text);
    }

    // A session id is a file name; anything else in it is replaced.
    private static string Sanitize(string sessionId)
        => string.Concat(sessionId.Select(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
}
