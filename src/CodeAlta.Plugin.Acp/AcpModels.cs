using System.Globalization;
using System.Text.Json;
using CodeAlta.Acp;
using CodeAlta.Agent;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Acp;

/// <summary>A value of a select option of a session: its value, its name, its description and the <c>_meta</c> the agent adds.</summary>
internal readonly record struct AcpOptionValue(string Value, string? Name, string? Description, JsonElement Meta);

/// <summary>
/// A model as an agent lists it, with its reasoning efforts once they are known: <see cref="Efforts"/> is
/// <see langword="null"/> while they are not, and empty for a model that runs without one.
/// </summary>
/// <param name="Id">The value of the <c>model</c> option.</param>
/// <param name="Name">The name the agent gives it, or <see langword="null"/>.</param>
/// <param name="Description">The description the agent gives it, or <see langword="null"/>.</param>
/// <param name="Meta">The <c>_meta</c> of the value, or <see langword="default"/>.</param>
internal sealed record AcpListedModel(string Id, string? Name, string? Description, JsonElement Meta)
{
    /// <summary>Gets the reasoning efforts of the model, or <see langword="null"/> when they are not known.</summary>
    public IReadOnlyList<AgentReasoningEffort>? Efforts { get; init; }

    /// <summary>Gets the effort the agent chooses for the model, or <see langword="null"/>.</summary>
    public AgentReasoningEffort? DefaultEffort { get; init; }

    public static AcpListedModel From(AcpOptionValue value) => new(value.Value, value.Name, value.Description, value.Meta);

    /// <summary>Keeps what was learned of each model before, when a new list does not tell its efforts.</summary>
    public static IReadOnlyList<AcpListedModel> Merge(IReadOnlyList<AcpListedModel> listed, IReadOnlyList<AcpListedModel>? known)
    {
        if (known is not { Count: > 0 })
        {
            return listed;
        }

        var learned = new Dictionary<string, AcpListedModel>(StringComparer.Ordinal);
        foreach (var model in known)
        {
            if (model.Efforts is not null)
            {
                learned.TryAdd(model.Id, model);
            }
        }

        return [.. listed.Select(model => model.Efforts is null && learned.TryGetValue(model.Id, out var earlier)
            ? model with { Efforts = earlier.Efforts, DefaultEffort = earlier.DefaultEffort }
            : model)];
    }
}

/// <summary>What the window shows of a model of an agent.</summary>
/// <param name="DisplayName">The name shown.</param>
/// <param name="Description">The description, or <see langword="null"/>.</param>
/// <param name="CatalogModelId">The identifier the catalog of models knows it by, or <see langword="null"/> for its own.</param>
/// <param name="Efforts">Its reasoning efforts, or <see langword="null"/> when they are not known.</param>
/// <param name="DefaultEffort">The effort the agent chooses for it, or <see langword="null"/>.</param>
/// <param name="ContextWindow">The size of its context in tokens, as the agent gives it, or <see langword="null"/>.</param>
internal sealed record AcpModelFacts(
    string DisplayName,
    string? Description,
    string? CatalogModelId,
    IReadOnlyList<AgentReasoningEffort>? Efforts,
    AgentReasoningEffort? DefaultEffort,
    long? ContextWindow);

/// <summary>
/// What CodeAlta knows of an agent beyond the protocol: how it names its models, and which provider of the catalog of
/// models serves them. An agent with ways of its own has a subclass, which <see cref="For"/> chooses; the others are
/// read as the protocol says.
/// </summary>
internal class AcpAgentQuirks
{
    /// <summary>The ways of an agent CodeAlta knows nothing particular of.</summary>
    public static AcpAgentQuirks Protocol { get; } = new();

    /// <summary>Returns the ways of the agent a provider runs: by its id in the ACP registry, else by its command.</summary>
    public static AcpAgentQuirks For(AcpAgentSettings agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        if (CursorAcpQuirks.Runs(agent))
        {
            return CursorAcpQuirks.Instance;
        }

        return CopilotAcpQuirks.Runs(agent) ? CopilotAcpQuirks.Instance : Protocol;
    }

    /// <summary>Gets the provider of the catalog of models that serves the models of the agent, or <see langword="null"/> to find each by its vendor.</summary>
    public virtual string? CatalogProviderId => null;

    /// <summary>
    /// Gets whether the probe chooses each model in turn to read its reasoning efforts. An agent that names them
    /// otherwise, or for which it takes too long, does not.
    /// </summary>
    public virtual bool LearnsEffortsByModel => true;

    /// <summary>Describes a model: its name and description as the agent gives them, the description left out when it only repeats the name.</summary>
    public virtual AcpModelFacts Describe(AcpListedModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var name = string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name.Trim();
        var description = string.IsNullOrWhiteSpace(model.Description) || string.Equals(model.Description.Trim(), name, StringComparison.OrdinalIgnoreCase)
            ? null
            : model.Description.Trim();
        return new AcpModelFacts(name, description, null, model.Efforts, model.DefaultEffort, null);
    }

    /// <summary>The name of the executable of a command, whichever separator its folders have (a provider may be written on another system).</summary>
    protected static string CommandFileName(string command)
        => command[(command.LastIndexOfAny(['/', '\\']) + 1)..];

    /// <summary>Joins the parts of a description that are there.</summary>
    protected static string? Join(params string?[] parts)
    {
        var present = parts.Where(static part => !string.IsNullOrWhiteSpace(part)).ToArray();
        return present.Length == 0 ? null : string.Join(" · ", present);
    }
}

/// <summary>
/// Cursor names a variant of a model by its parameters (<c>grok-4.7[context=256k,reasoning_effort=high,fast=true]</c>)
/// and takes only the variants it lists: their reasoning effort is the one of their name, and the session has no
/// option for it. Choosing each model would take half a minute, and Cursor keeps the last one chosen.
/// </summary>
internal sealed class CursorAcpQuirks : AcpAgentQuirks
{
    public static CursorAcpQuirks Instance { get; } = new();

    public static bool Runs(AcpAgentSettings agent)
        => string.Equals(agent.AgentId, "cursor", StringComparison.OrdinalIgnoreCase) ||
           CommandFileName(agent.Command).StartsWith("cursor-agent", StringComparison.OrdinalIgnoreCase);

    public override bool LearnsEffortsByModel => false;

    public override AcpModelFacts Describe(AcpListedModel model)
    {
        var facts = base.Describe(model);
        if (!TryParse(model.Id, out var name, out var parameters))
        {
            return facts;
        }

        AgentReasoningEffort? effort = null;
        long? context = null;
        var words = new List<string>();
        foreach (var (key, value) in parameters)
        {
            switch (key)
            {
                case "context":
                    context = ParseTokens(value);
                    words.Add($"{value.ToUpperInvariant()} context");
                    break;
                case "reasoning_effort" or "effort" or "reasoning":
                    effort ??= AcpConfigOptions.ToEffort(value);
                    words.Add($"{value} reasoning");
                    break;
                case "thinking":
                    words.Add(value == "true" ? "thinking" : "no thinking");
                    break;
                case "fast":
                    if (value == "true")
                    {
                        words.Add("fast");
                    }

                    break;
                default:
                    words.Add($"{key} {value}");
                    break;
            }
        }

        // The agent has no option for the effort: a variant runs at the one of its name, or without one.
        IReadOnlyList<AgentReasoningEffort> efforts = facts.Efforts is { Count: > 0 } offered ? offered : effort is { } fixedEffort ? [fixedEffort] : [];
        return facts with
        {
            // A name that is the whole id is the name of the model, without the parameters the description gives.
            DisplayName = string.Equals(facts.DisplayName, model.Id, StringComparison.OrdinalIgnoreCase) ? name : facts.DisplayName,
            Description = Join(facts.Description, words.Count == 0 ? null : string.Join(" · ", words)),
            CatalogModelId = string.Equals(name, "default", StringComparison.Ordinal) ? null : name,
            Efforts = efforts,
            DefaultEffort = facts.Efforts is { Count: > 0 } ? facts.DefaultEffort : effort,
            ContextWindow = context,
        };
    }

    /// <summary>Reads a model id with the parameters of its variant: <c>name[key=value,...]</c>.</summary>
    internal static bool TryParse(string id, out string name, out IReadOnlyList<(string Key, string Value)> parameters)
    {
        name = id;
        parameters = [];
        var open = id.IndexOf('[', StringComparison.Ordinal);
        if (open <= 0 || !id.EndsWith(']'))
        {
            return false;
        }

        name = id[..open];
        var list = new List<(string, string)>();
        foreach (var part in id[(open + 1)..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                list.Add((part[..equals].Trim().ToLowerInvariant(), part[(equals + 1)..].Trim().ToLowerInvariant()));
            }
        }

        parameters = list;
        return true;
    }

    // 256k, 1m, 200000.
    private static long? ParseTokens(string value)
    {
        var multiplier = value.EndsWith('k') ? 1_000L : value.EndsWith('m') ? 1_000_000L : 1L;
        var digits = multiplier == 1 ? value : value[..^1];
        return decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) && number > 0
            ? (long)(number * multiplier)
            : null;
    }
}

/// <summary>
/// The Copilot CLI serves the models of GitHub Copilot, as the catalog of models lists them, and says in <c>_meta</c>
/// how many premium requests a prompt to a model counts (<c>copilotUsage</c>).
/// </summary>
internal sealed class CopilotAcpQuirks : AcpAgentQuirks
{
    public static CopilotAcpQuirks Instance { get; } = new();

    public static bool Runs(AcpAgentSettings agent)
        => string.Equals(agent.AgentId, "github-copilot-cli", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(Path.GetFileNameWithoutExtension(CommandFileName(agent.Command)), "copilot", StringComparison.OrdinalIgnoreCase) ||
           agent.Arguments.Any(static argument => argument.StartsWith("@github/copilot", StringComparison.OrdinalIgnoreCase));

    public override string? CatalogProviderId => "github-copilot";

    public override AcpModelFacts Describe(AcpListedModel model)
    {
        var facts = base.Describe(model);
        var usage = AcpJson.GetString(model.Meta, "copilotUsage");
        return string.IsNullOrWhiteSpace(usage) ? facts : facts with { Description = Join(facts.Description, $"{usage.Trim()} premium requests") };
    }
}

/// <summary>Gives the models of an agent as the window shows them.</summary>
internal static class AcpModels
{
    public static IReadOnlyList<AgentModelInfo> Present(IReadOnlyList<AcpListedModel> models, AcpAgentQuirks quirks, IPluginModelCatalog? catalog)
        => [.. models.Select(model => Present(model, quirks, catalog))];

    /// <summary>
    /// Describes a model by what the agent says of it, then by the catalog of models. Its efforts, once known, are all it
    /// has: CodeAlta sets no other one.
    /// </summary>
    public static AgentModelInfo Present(AcpListedModel model, AcpAgentQuirks quirks, IPluginModelCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(quirks);
        var facts = quirks.Describe(model);
        var capabilities = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (facts.Efforts is not null)
        {
            capabilities[AgentReasoningEffortCapability.CompleteListKey] = true;
        }

        if (facts.ContextWindow is { } context)
        {
            // The size of the variant the agent runs, which the catalog does not replace.
            capabilities["contextWindow"] = context;
            capabilities["contextWindowTokens"] = context;
        }

        var info = new AgentModelInfo(
            model.Id,
            facts.DisplayName,
            facts.Description,
            DefaultReasoningEffort: facts.Efforts is { Count: > 0 } ? facts.DefaultEffort : null,
            SupportedReasoningEfforts: facts.Efforts,
            Capabilities: capabilities.Count == 0 ? null : capabilities);
        return catalog?.Describe(info, facts.CatalogModelId, quirks.CatalogProviderId) ?? info;
    }
}
