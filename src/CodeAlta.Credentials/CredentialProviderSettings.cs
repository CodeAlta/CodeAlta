using System.Globalization;
using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using Tomlyn;
using Tomlyn.Model;

namespace CodeAlta.Credentials;

/// <summary>How long the values a credential provider read stay in memory.</summary>
public static class CredentialCacheDurations
{
    /// <summary>Until CodeAlta closes or the user locks the provider, the default.</summary>
    public const string UntilClosed = "session";

    /// <summary>15 minutes.</summary>
    public const string FifteenMinutes = "15m";

    /// <summary>One hour.</summary>
    public const string OneHour = "1h";

    /// <summary>Eight hours.</summary>
    public const string EightHours = "8h";

    /// <summary>Gets whether a text is one of the durations.</summary>
    /// <param name="value">The text.</param>
    /// <returns>True for a duration.</returns>
    public static bool IsDuration(string? value) => value is UntilClosed or FifteenMinutes or OneHour or EightHours;

    /// <summary>Gets the time a duration lasts; null until CodeAlta closes.</summary>
    /// <param name="value">A duration.</param>
    /// <returns>The time.</returns>
    public static TimeSpan? TimeOf(string? value) => value switch
    {
        FifteenMinutes => TimeSpan.FromMinutes(15),
        OneHour => TimeSpan.FromHours(1),
        EightHours => TimeSpan.FromHours(8),
        _ => null,
    };
}

/// <summary>One credential provider of the user's configuration: a <c>[credentials.&lt;id&gt;]</c> table.</summary>
/// <param name="Id">Its id, which the secrets bound to it name.</param>
/// <param name="Type">Its type, such as <c>1password</c>.</param>
/// <param name="Values">The settings of its type, by key, as written: texts, booleans, lists of texts.</param>
public sealed record CredentialProviderDefinition(string Id, string Type, IReadOnlyDictionary<string, object?> Values)
{
    /// <summary>Gets the name shown for it; null for its id.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets how long the values it read stay in memory (<see cref="CredentialCacheDurations"/>).</summary>
    public string Cache { get; init; } = CredentialCacheDurations.UntilClosed;

    /// <summary>Gets whether CodeAlta Desktop asks its vault to unlock when it starts, once.</summary>
    public bool UnlockAtStart { get; init; } = true;

    /// <summary>Gets a signature of what it is: a provider whose signature changed is created again.</summary>
    public string Signature => string.Join('\n', new[] { Type, DisplayName ?? string.Empty, Cache }
        .Concat(Values.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(static pair => pair.Key + "=" + Text(pair.Value))));

    private static string Text(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        bool flag => flag ? "true" : "false",
        IEnumerable<object?> list => "[" + string.Join('\u001f', list.Select(Text)) + "]",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}

/// <summary>
/// The credential providers of the user, kept in <c>~/.alta/config.toml</c> under <c>[credentials.&lt;id&gt;]</c>. Only the
/// user's own file is read: the configuration of a project never names a vault. A write edits the keys of one table and leaves
/// the rest of the file as it is.
/// </summary>
public sealed class CredentialProviderSettings
{
    /// <summary>The keys of a table the host keeps for every type.</summary>
    public static readonly IReadOnlyList<string> HostKeys = ["type", "display_name", "cache", "unlock_at_start"];

    private readonly string _configPath;

    /// <summary>Initializes a new instance of the <see cref="CredentialProviderSettings"/> class.</summary>
    /// <param name="configPath">The path of <c>~/.alta/config.toml</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="configPath"/> is blank.</exception>
    public CredentialProviderSettings(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        _configPath = configPath;
    }

    /// <summary>Gets the path of the file.</summary>
    public string ConfigPath => _configPath;

    /// <summary>Reads the providers. A table that is not valid is left out, and said in <paramref name="problems"/>.</summary>
    /// <param name="problems">What is wrong with the tables left out.</param>
    /// <returns>The providers, in the order of the file.</returns>
    public IReadOnlyList<CredentialProviderDefinition> Load(out IReadOnlyList<string> problems)
    {
        var found = new List<CredentialProviderDefinition>();
        var wrong = new List<string>();
        problems = wrong;
        string? content;
        try
        {
            content = CodeAlta.Core.IO.SafeFile.ReadAllText(_configPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            wrong.Add($"{_configPath} cannot be read: {exception.Message}");
            return found;
        }

        if (string.IsNullOrWhiteSpace(content)) return found;
        TomlTable model;
        try
        {
            model = TomlSerializer.Deserialize<TomlTable>(content) ?? [];
        }
        catch (TomlException exception)
        {
            wrong.Add($"{_configPath} is not valid TOML: {exception.Message}");
            return found;
        }

        if (!model.TryGetValue("credentials", out var node) || node is not TomlTable tables) return found;
        foreach (var (id, value) in tables)
        {
            if (value is not TomlTable table)
            {
                wrong.Add($"credentials.{id} is not a table.");
                continue;
            }

            if (!SecretBindings.IsProviderId(id))
            {
                wrong.Add($"credentials.{id}: an id is lowercase letters, digits, - and _, starting with a letter.");
                continue;
            }

            if (!table.TryGetValue("type", out var typeNode) || typeNode is not string type || string.IsNullOrWhiteSpace(type))
            {
                wrong.Add($"credentials.{id} has no type.");
                continue;
            }

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, item) in table)
            {
                if (HostKeys.Contains(key)) continue;
                values[key] = item switch
                {
                    TomlArray array => array.Select(static entry => (object?)Convert.ToString(entry, CultureInfo.InvariantCulture)).ToArray(),
                    string or bool => item,
                    long or double => Convert.ToString(item, CultureInfo.InvariantCulture),
                    _ => null,
                };
            }

            var cache = table.TryGetValue("cache", out var cacheNode) && cacheNode is string cacheText && CredentialCacheDurations.IsDuration(cacheText) ? cacheText : CredentialCacheDurations.UntilClosed;
            found.Add(new CredentialProviderDefinition(id, type.Trim(), values)
            {
                DisplayName = table.TryGetValue("display_name", out var name) && name is string text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null,
                Cache = cache,
                UnlockAtStart = !table.TryGetValue("unlock_at_start", out var unlock) || unlock is not false,
            });
        }

        return found;
    }

    /// <summary>
    /// Gets whether the user lets the shells of agents run the programs of the configured vaults (<c>agent_vault_commands = "allow"</c>
    /// under <c>[secrets]</c>); by default they are refused.
    /// </summary>
    /// <returns>True when the user allows them.</returns>
    public bool AgentVaultCommandsAllowed()
    {
        try
        {
            var content = CodeAlta.Core.IO.SafeFile.ReadAllText(_configPath);
            if (string.IsNullOrWhiteSpace(content)) return false;
            var model = TomlSerializer.Deserialize<TomlTable>(content) ?? [];
            return model.TryGetValue("secrets", out var node) && node is TomlTable table && table.TryGetValue("agent_vault_commands", out var value)
                && value is string text && string.Equals(text, "allow", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TomlException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets whether the user lets the developer instance of CodeAlta Desktop ask a vault to unlock, on a click in its window
    /// (`developer_instance = "unlock"` under `[secrets]`); by default it never does.
    /// </summary>
    /// <returns>True when the user allows it.</returns>
    public bool DeveloperInstanceUnlocks()
    {
        try
        {
            var content = CodeAlta.Core.IO.SafeFile.ReadAllText(_configPath);
            if (string.IsNullOrWhiteSpace(content)) return false;
            var model = TomlSerializer.Deserialize<TomlTable>(content) ?? [];
            return model.TryGetValue("secrets", out var node) && node is TomlTable table && table.TryGetValue("developer_instance", out var value)
                && value is string text && string.Equals(text, "unlock", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TomlException)
        {
            return false;
        }
    }

    /// <summary>Lets the developer instance ask a vault to unlock, or no longer.</summary>
    /// <param name="allowed">Whether it may.</param>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="InvalidDataException">The file is not a configuration this version edits.</exception>
    public void SetDeveloperInstanceUnlocks(bool allowed)
        => CodeAltaConfigStore.EditConfigFile(_configPath, text => ConfigTableSyntax.SetText(text, ["secrets"], "developer_instance", allowed ? "unlock" : null));

    /// <summary>Writes a provider: its table gets these values, and loses the keys of its type it no longer has.</summary>
    /// <param name="definition">The provider.</param>
    /// <exception cref="ArgumentException">The id or a key is not valid.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="InvalidDataException">The file is not a configuration this version edits.</exception>
    public void Save(CredentialProviderDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!SecretBindings.IsProviderId(definition.Id)) throw new ArgumentException($"'{definition.Id}' is not the id of a credential provider.", nameof(definition));
        if (!CredentialCacheDurations.IsDuration(definition.Cache)) throw new ArgumentException($"'{definition.Cache}' is not a duration of the memory of a provider.", nameof(definition));
        foreach (var key in definition.Values.Keys)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Any(static ch => !(char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '_')) || HostKeys.Contains(key))
                throw new ArgumentException($"'{key}' is not a key of the settings of a provider.", nameof(definition));
        }

        string[] table = ["credentials", definition.Id];
        CodeAltaConfigStore.EditConfigFile(_configPath, text =>
        {
            var existing = ExistingKeys(text, definition.Id);
            text = ConfigTableSyntax.SetText(text, table, "type", definition.Type);
            text = ConfigTableSyntax.SetText(text, table, "display_name", definition.DisplayName);
            text = ConfigTableSyntax.SetText(text, table, "cache", definition.Cache == CredentialCacheDurations.UntilClosed ? null : definition.Cache);
            text = definition.UnlockAtStart ? ConfigTableSyntax.SetText(text, table, "unlock_at_start", null) : ConfigTableSyntax.SetValue(text, table, "unlock_at_start", false);
            foreach (var key in existing.Where(key => !HostKeys.Contains(key) && !definition.Values.ContainsKey(key)))
            {
                text = ConfigTableSyntax.SetText(text, table, key, null);
            }

            foreach (var (key, value) in definition.Values)
            {
                text = value switch
                {
                    null => ConfigTableSyntax.SetText(text, table, key, null),
                    bool flag => ConfigTableSyntax.SetValue(text, table, key, flag),
                    string item => ConfigTableSyntax.SetText(text, table, key, string.IsNullOrEmpty(item) ? null : item),
                    IEnumerable<object?> list => ConfigTableSyntax.SetList(text, table, key, [.. list.Select(static entry => Convert.ToString(entry, CultureInfo.InvariantCulture) ?? string.Empty)]),
                    _ => ConfigTableSyntax.SetText(text, table, key, Convert.ToString(value, CultureInfo.InvariantCulture)),
                };
            }

            return text;
        });
    }

    /// <summary>Removes a provider from the file.</summary>
    /// <param name="id">The id of the provider.</param>
    /// <returns>True when the file named it.</returns>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public bool Remove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var removed = false;
        CodeAltaConfigStore.EditConfigFile(_configPath, text =>
        {
            var next = ConfigTableSyntax.RemoveTable(text, ["credentials", id]);
            removed = !string.Equals(next, text, StringComparison.Ordinal);
            return next;
        });
        return removed;
    }

    private static IReadOnlyList<string> ExistingKeys(string text, string id)
    {
        try
        {
            var model = TomlSerializer.Deserialize<TomlTable>(text) ?? [];
            return model.TryGetValue("credentials", out var node) && node is TomlTable tables && tables.TryGetValue(id, out var own) && own is TomlTable table ? [.. table.Keys] : [];
        }
        catch (TomlException)
        {
            return [];
        }
    }
}
