using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// What a CLI says of itself when it is asked, without running a turn.
/// </summary>
/// <param name="ExecutablePath">The executable that answered.</param>
/// <param name="Models">The models the account of the user can select.</param>
/// <param name="IsSignedIn">Whether the CLI has a way to authenticate.</param>
/// <param name="AccountSummary">How the CLI authenticates (a plan or a provider, never an identity).</param>
internal sealed record ClaudeCodeInspection(
    string ExecutablePath,
    IReadOnlyList<AgentModelInfo> Models,
    bool IsSignedIn,
    string? AccountSummary);

/// <summary>
/// Lists the models of the Claude Code CLI by asking the CLI: it knows what the plan, the settings and the
/// policies of the user allow. A short-lived process answers and exits; no model is called.
/// </summary>
internal sealed class ClaudeCodeModelCatalog : IModelProviderModelCatalog
{
    /// <summary>
    /// The entry of the CLI that lets it choose the model. It is not offered as a model; a session or a setting that
    /// names it still runs with the choice of the CLI.
    /// </summary>
    public const string DefaultModelId = "default";

    // The model an entry of the CLI runs, which several entries can share (an alias and the default).
    private const string ResolvedModelCapability = "resolvedModel";

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    // The CLI does not say which models take images: the ones it runs do, and it accepts an image in a prompt
    // whatever the model is. CodeAlta only lets an image be attached for a model that says it takes them.
    private const string ImageInputCapability = "supportsImageInput";

    // The aliases of the CLI, offered when it cannot be asked. The first one is the model every account has.
    private static readonly AgentModelInfo[] FallbackModels =
    [
        CreateUnlistedModel("sonnet", "Sonnet", "The Sonnet model of the installed Claude Code."),
        CreateUnlistedModel("opus", "Opus", "The Opus model of the installed Claude Code."),
        CreateUnlistedModel("haiku", "Haiku", "The Haiku model of the installed Claude Code."),
    ];

    private readonly ClaudeCodeModelProviderRuntimeOptions _options;
    private readonly IClaudeCodeTransportFactory _transportFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaudeCodeInspection? _cached;
    private DateTimeOffset _cachedAt;

    public ClaudeCodeModelCatalog(ClaudeCodeModelProviderRuntimeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transportFactory = options.TransportFactory ?? ClaudeCodeProcessTransportFactory.Instance;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(ModelProviderRuntimeDescriptor provider, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await InspectAsync(cancellationToken).ConfigureAwait(false)).Models;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            return Filter(FallbackModels);
        }
    }

    /// <summary>
    /// Starts the CLI, asks it for its models and its account, and stops it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The CLI was not found or did not answer.</exception>
    public async Task<ClaudeCodeInspection> InspectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is { } cached && DateTimeOffset.UtcNow - _cachedAt < CacheLifetime)
            {
                return cached;
            }

            var inspection = await InspectCoreAsync(cancellationToken).ConfigureAwait(false);

            // A signed-out answer is not kept: the user signs in in a terminal and tests the provider again.
            _cached = inspection.IsSignedIn ? inspection : null;
            _cachedAt = DateTimeOffset.UtcNow;
            return inspection;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ClaudeCodeInspection> InspectCoreAsync(CancellationToken cancellationToken)
    {
        var resolution = _options.ResolveCli?.Invoke() ?? ClaudeCodeCliLocator.Resolve(_options.Command);
        if (resolution.Path is null)
        {
            throw new ClaudeCodeNotFoundException(resolution.Error ?? "Claude Code was not found.");
        }

        // Nothing of the conversation is used: the process works in the profile of the user and saves no session.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var launch = ClaudeCodeLauncher.Create(
            resolution.Path,
            _options,
            new ClaudeCodeLaunchKey(string.IsNullOrWhiteSpace(home) ? null : home, null, null),
            newSessionId: null,
            resumeSessionId: null,
            withTools: false);
        launch = launch with { Arguments = [.. launch.Arguments, "--no-session-persistence"] };

        var connection = new ClaudeCodeConnection(_transportFactory.Start(launch), SilentHandler.Instance);
        try
        {
            connection.Start();
            JsonElement response;
            try
            {
                response = await connection.RequestAsync("initialize", null, _options.StartupTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or ClaudeCodeControlException)
            {
                throw new InvalidOperationException($"Claude Code at '{resolution.Path}' did not answer. {connection.DescribeExit()}", ex);
            }

            var models = ReadModels(response);
            var (isSignedIn, summary) = ReadAccount(response);
            return new ClaudeCodeInspection(resolution.Path, Filter(models.Count == 0 ? FallbackModels : models), isSignedIn, summary);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static List<AgentModelInfo> ReadModels(JsonElement response)
    {
        var models = new List<AgentModelInfo>();
        if (!ClaudeCodeJson.TryGetArray(response, "models", out var entries))
        {
            return models;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AgentModelInfo? byDefault = null;
        foreach (var entry in entries.EnumerateArray())
        {
            // An entry the CLI shows as unavailable (an update is required) cannot run a turn.
            if (ClaudeCodeJson.GetString(entry, "value") is not { Length: > 0 } value ||
                ClaudeCodeJson.GetBoolean(entry, "disabled") ||
                !seen.Add(value))
            {
                continue;
            }

            List<AgentReasoningEffort>? efforts = null;
            if (ClaudeCodeJson.TryGetArray(entry, "supportedEffortLevels", out var levels))
            {
                efforts = [];
                foreach (var level in levels.EnumerateArray())
                {
                    AgentReasoningEffort? effort = level.ValueKind == JsonValueKind.String
                        ? level.GetString() switch
                        {
                            "low" => AgentReasoningEffort.Low,
                            "medium" => AgentReasoningEffort.Medium,
                            "high" => AgentReasoningEffort.High,
                            "xhigh" => AgentReasoningEffort.XHigh,
                            "max" => AgentReasoningEffort.Max,
                            _ => null,
                        }
                        : null;
                    if (effort is { } known)
                    {
                        efforts.Add(known);
                    }
                }
            }

            var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal) { [ImageInputCapability] = true };
            if (ClaudeCodeJson.GetString(entry, "resolvedModel") is { Length: > 0 } resolvedModel)
            {
                capabilities[ResolvedModelCapability] = resolvedModel;
            }

            if (efforts is { Count: > 0 })
            {
                capabilities["supportsReasoning"] = true;
            }

            var model = new AgentModelInfo(
                value,
                ClaudeCodeJson.GetString(entry, "displayName") ?? value,
                ClaudeCodeJson.GetString(entry, "description"),
                Provider: "Claude Code",
                SupportedReasoningEfforts: efforts is { Count: > 0 } ? efforts : null,
                Capabilities: capabilities);
            if (string.Equals(value, DefaultModelId, StringComparison.OrdinalIgnoreCase))
            {
                byDefault = model;
            }
            else
            {
                models.Add(model);
            }
        }

        // The default of the CLI is one of its models under another name. It is not offered: the model it stands
        // for is, and comes first, where a session that names no model takes it.
        if (byDefault is not null && ResolvedModel(byDefault) is { } resolved)
        {
            var named = models.FindIndex(model =>
                string.Equals(ResolvedModel(model), resolved, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(model.Id, resolved, StringComparison.OrdinalIgnoreCase));
            if (named < 0)
            {
                models.Insert(0, byDefault with { Id = resolved, DisplayName = resolved });
            }
            else if (named > 0)
            {
                var model = models[named];
                models.RemoveAt(named);
                models.Insert(0, model);
            }
        }

        return models;
    }

    private static string? ResolvedModel(AgentModelInfo model)
        => model.Capabilities is { } capabilities && capabilities.TryGetValue(ResolvedModelCapability, out var resolved) ? resolved as string : null;

    private static AgentModelInfo CreateUnlistedModel(string id, string displayName, string? description)
        => new(id, displayName, description, Provider: "Claude Code", Capabilities: new Dictionary<string, object?>(StringComparer.Ordinal) { [ImageInputCapability] = true });

    internal static (bool IsSignedIn, string? Summary) ReadAccount(JsonElement response)
    {
        if (!ClaudeCodeJson.TryGetObject(response, "account", out var account))
        {
            // An older CLI does not describe its account: a turn will tell.
            return (true, null);
        }

        var apiProvider = ClaudeCodeJson.GetString(account, "apiProvider");
        if (apiProvider is not null && !string.Equals(apiProvider, "firstParty", StringComparison.Ordinal))
        {
            // A cloud provider authenticates with its own credentials.
            return (true, apiProvider);
        }

        // A signed-out CLI says so (`tokenSource` is "none"); a signed-in one names its plan and no token source.
        // Anything else is tried: a turn tells when the CLI cannot authenticate.
        var apiKeySource = ClaudeCodeJson.GetString(account, "apiKeySource");
        var hasApiKey = apiKeySource is not null && !string.Equals(apiKeySource, "none", StringComparison.OrdinalIgnoreCase);
        var saysNoToken = string.Equals(ClaudeCodeJson.GetString(account, "tokenSource"), "none", StringComparison.OrdinalIgnoreCase);
        if (saysNoToken && !hasApiKey)
        {
            return (false, null);
        }

        return (true, ClaudeCodeJson.GetString(account, "subscriptionType") ?? (hasApiKey ? "API key" : null));
    }

    private IReadOnlyList<AgentModelInfo> Filter(IReadOnlyList<AgentModelInfo> models)
    {
        if (!string.IsNullOrWhiteSpace(_options.SingleModelId))
        {
            var single = _options.SingleModelId.Trim();
            return [models.FirstOrDefault(model => string.Equals(model.Id, single, StringComparison.OrdinalIgnoreCase)) ?? CreateUnlistedModel(single, single, description: null)];
        }

        if (string.IsNullOrWhiteSpace(_options.ModelsIncludeRegex))
        {
            return models;
        }

        try
        {
            var regex = new Regex(_options.ModelsIncludeRegex, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            return [.. models.Where(model => regex.IsMatch(model.Id))];
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return models;
        }
    }

    private sealed class SilentHandler : IClaudeCodeConnectionHandler
    {
        public static SilentHandler Instance { get; } = new();

        public void OnMessage(string type, JsonElement message)
        {
        }

        public void OnControlRequest(string requestId, string subtype, JsonElement request)
        {
        }

        public void OnControlCancel(string requestId)
        {
        }

        public void OnClosed(Exception? failure)
        {
        }
    }
}
