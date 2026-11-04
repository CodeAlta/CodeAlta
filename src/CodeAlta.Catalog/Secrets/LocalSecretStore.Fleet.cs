using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Serialization;

namespace CodeAlta.Catalog.Secrets;

/// <summary>The kinds of scopes of the values the fleet gives a machine (<c>doc/fleet/secrets.md</c>, scopes), as the machine knows them.</summary>
public static class FleetSecretScopes
{
    /// <summary>An environment, by its name.</summary>
    public const string Environment = "environment";

    /// <summary>A project of this machine, by its id.</summary>
    public const string Project = "project";

    /// <summary>A space of this machine, by its id.</summary>
    public const string Space = "space";

    /// <summary>Every work of the person.</summary>
    public const string Person = "person";
}

/// <summary>A value the fleet gave this machine, opened and mapped to the scopes of this machine.</summary>
/// <param name="Source">The server link it came through.</param>
/// <param name="SecretId">The id of the value at the server.</param>
/// <param name="Kind"><c>secret</c> or <c>variable</c>.</param>
/// <param name="Name">Its name.</param>
/// <param name="ScopeKind">The kind of its scope (<see cref="FleetSecretScopes"/>).</param>
/// <param name="ScopeId">The id of its scope on this machine: an environment's name, a project's or a space's id; null for the person.</param>
/// <param name="Version">Its version.</param>
/// <param name="HostOnly">Whether it goes only to host-side tools, never to the agent's shell.</param>
/// <param name="Value">Its value.</param>
public sealed record FleetSecretValue(string Source, string SecretId, string Kind, string Name, string ScopeKind, string? ScopeId, int Version, bool HostOnly, string Value)
{
    /// <summary>The kind of a secret.</summary>
    public const string SecretKind = "secret";

    /// <summary>The kind of a variable.</summary>
    public const string VariableKind = "variable";
}

/// <summary>What is known of a value the fleet gave this machine, never its value.</summary>
/// <param name="Source">The server link it came through.</param>
/// <param name="SecretId">The id of the value at the server.</param>
/// <param name="Kind"><c>secret</c> or <c>variable</c>.</param>
/// <param name="Name">Its name.</param>
/// <param name="ScopeKind">The kind of its scope.</param>
/// <param name="ScopeId">The id of its scope on this machine; null for the person.</param>
/// <param name="Version">Its version.</param>
/// <param name="HostOnly">Whether it goes only to host-side tools.</param>
/// <param name="ReceivedAt">When this machine took this version.</param>
public sealed record FleetSecretCopyInfo(string Source, string SecretId, string Kind, string Name, string ScopeKind, string? ScopeId, int Version, bool HostOnly,
    DateTimeOffset ReceivedAt);

// The copies of the values the fleet gives this machine (doc/fleet/secrets.md, how a run gets them): the node opens the
// copies sealed for it and keeps them here, protected by the protected store of the machine as its own values are, one
// set per server link; a rotation replaces the set at the next refresh, and a revoked link takes its set away.
public sealed partial class LocalSecretStore
{
    /// <summary>
    /// Replaces the values one server link gave this machine with those given now; those of other links stay. A version a
    /// machine held already keeps the time it took it.
    /// </summary>
    /// <param name="source">The link.</param>
    /// <param name="values">The values, opened.</param>
    /// <returns>Whether the set changed.</returns>
    /// <exception cref="ArgumentException">The link is empty, or a value does not hold.</exception>
    /// <exception cref="SecretStoreUnavailableException">The values cannot be protected.</exception>
    public bool ReplaceFleetCopies(string source, IReadOnlyCollection<FleetSecretValue> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(values);
        foreach (var value in values)
        {
            if (value is null || value.Source != source || !IsName(value.Name) || value.Kind is not (FleetSecretValue.SecretKind or FleetSecretValue.VariableKind)
                || value.ScopeKind is not (FleetSecretScopes.Person or FleetSecretScopes.Space or FleetSecretScopes.Project or FleetSecretScopes.Environment)
                || value.Value.Length is 0 or > MaximumValueLength || value.Version < 1)
                throw new ArgumentException("A value of the fleet has the name of an environment variable, a kind, a scope, a version and a value.", nameof(values));
        }

        var current = values.Select(static value => Key(value.SecretId, value.ScopeKind, value.ScopeId, value.Version)).ToHashSet(StringComparer.Ordinal);
        return Change(document =>
        {
            var held = document.Fleet.Where(copy => copy.Source == source).ToDictionary(static copy => Key(copy.Id!, copy.Scope!, copy.ScopeId, copy.Version), StringComparer.Ordinal);
            // Unchanged: the same versions in the same scopes; the values of one version never change.
            if (held.Count == current.Count && held.Keys.All(current.Contains)) return false;
            var protector = ProtectorFor(document);
            var now = DateTimeOffset.UtcNow;
            var copies = document.Fleet.Where(copy => copy.Source != source).ToList();
            foreach (var value in values)
            {
                var bytes = Encoding.UTF8.GetBytes(value.Value);
                byte[] protectedValue;
                try
                {
                    protectedValue = protector.Protect(bytes);
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
                }

                copies.Add(new FleetCopyDocument
                {
                    Source = source, Id = value.SecretId, Kind = value.Kind, Name = value.Name, Scope = value.ScopeKind, ScopeId = value.ScopeId, Version = value.Version,
                    HostOnly = value.HostOnly, Value = Convert.ToBase64String(protectedValue),
                    ReceivedAt = held.TryGetValue(Key(value.SecretId, value.ScopeKind, value.ScopeId, value.Version), out var kept) ? kept.ReceivedAt : now,
                });
            }

            document.Fleet = copies;
            return true;
        });
    }

    /// <summary>Takes away the values a server link gave: the link is gone, or the machine was revoked.</summary>
    /// <param name="source">The link.</param>
    /// <returns>Whether it gave any.</returns>
    public bool RemoveFleetCopies(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return Change(document => document.Fleet.RemoveAll(copy => copy.Source == source) > 0);
    }

    /// <summary>Lists what is known of the values the fleet gave this machine, never their values.</summary>
    /// <returns>The values, by name.</returns>
    /// <exception cref="InvalidDataException">The file of the store is damaged.</exception>
    public IReadOnlyList<FleetSecretCopyInfo> ListFleetCopies()
        => [.. Read().Document.Fleet.Select(static copy => Info(copy)).OrderBy(static copy => copy.Name, StringComparer.OrdinalIgnoreCase).ThenBy(static copy => copy.ScopeKind, StringComparer.Ordinal)];

    /// <summary>
    /// Gets the source of the values the fleet gave this machine for one scope, to resolve the references of a run
    /// (<see cref="SecretReferenceResolver.ForScope(string, SecretScope, bool)"/>).
    /// </summary>
    /// <param name="scopeKind">The kind of the scope (<see cref="FleetSecretScopes"/>).</param>
    /// <param name="scopeId">The id of the scope on this machine; null for the person.</param>
    /// <param name="shell">
    /// Whether the values go to the agent's shell: a host-only secret is then refused, with an error that says so, instead
    /// of a value of a wider scope.
    /// </param>
    /// <param name="used">Told each value given, for the record of its uses; null for none.</param>
    /// <returns>The source.</returns>
    /// <exception cref="ArgumentException"><paramref name="scopeKind"/> is empty.</exception>
    public ISecretLookup FleetScope(string scopeKind, string? scopeId, bool shell, Action<FleetSecretCopyInfo>? used = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeKind);
        return new FleetLookup(this, scopeKind, scopeId, shell, used);
    }

    private bool TryGetFleet(string kind, string name, string scopeKind, string? scopeId, bool shell, [NotNullWhen(true)] out string? value,
        [NotNullWhen(true)] out FleetSecretCopyInfo? info)
    {
        ArgumentNullException.ThrowIfNull(name);
        value = null;
        info = null;
        var snapshot = Read();
        var copies = snapshot.Document.Fleet;
        for (var index = 0; index < copies.Count; index++)
        {
            var copy = copies[index];
            if (copy.Kind != kind || copy.Scope != scopeKind || !string.Equals(copy.ScopeId, scopeId, StringComparison.Ordinal)
                || !string.Equals(copy.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (shell && copy.HostOnly)
                throw new SecretStoreUnavailableException($"{name} is host-only: it goes to host-side tools (MCP servers, plugin tools, providers), never to the agent's shell.");
            value = FleetValues(snapshot)[index] ?? throw new SecretStoreUnavailableException($"The value the fleet gave for {name} cannot be read back on this machine.");
            info = Info(copy);
            return true;
        }

        return false;
    }

    // The values of the copies of a reading of the file, read back once for that reading; one that does not read back is null.
    private string?[] FleetValues(Snapshot snapshot)
    {
        lock (_gate)
        {
            if (snapshot.FleetValues is { } known) return known;
            var copies = snapshot.Document.Fleet;
            var values = new string?[copies.Count];
            if (copies.Count > 0)
            {
                var protector = ProtectorFor(snapshot.Document);
                for (var index = 0; index < copies.Count; index++)
                {
                    try
                    {
                        var bytes = protector.Unprotect(Convert.FromBase64String(copies[index].Value ?? string.Empty));
                        values[index] = Encoding.UTF8.GetString(bytes);
                        System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
                    }
                    catch (Exception exception) when (exception is FormatException or SecretStoreUnavailableException or System.Security.Cryptography.CryptographicException)
                    {
                        // Taken again at the next refresh of the fleet.
                    }
                }
            }

            return snapshot.FleetValues = values;
        }
    }

    // The values of the secrets the fleet gave, which are masked; a variable is not secret.
    private IEnumerable<string> FleetSecretValues(Snapshot snapshot)
    {
        var copies = snapshot.Document.Fleet;
        if (copies.Count == 0) return [];
        try
        {
            var values = FleetValues(snapshot);
            return [.. copies.Select((copy, index) => copy.Kind == FleetSecretValue.SecretKind ? values[index] : null).OfType<string>()];
        }
        catch (SecretStoreUnavailableException)
        {
            return [];
        }
    }

    private static FleetSecretCopyInfo Info(FleetCopyDocument copy)
        => new(copy.Source!, copy.Id ?? string.Empty, copy.Kind ?? FleetSecretValue.SecretKind, copy.Name!, copy.Scope ?? FleetSecretScopes.Person, copy.ScopeId, copy.Version,
            copy.HostOnly, copy.ReceivedAt);

    private static string Key(string id, string scope, string? scopeId, int version) => $"{id}\n{scope}\n{scopeId}\n{version}";

    private sealed class FleetLookup(LocalSecretStore store, string scopeKind, string? scopeId, bool shell, Action<FleetSecretCopyInfo>? used) : ISecretLookup
    {
        public string Scope => scopeKind;

        public bool TryGetSecret(string name, [NotNullWhen(true)] out string? value) => Get(FleetSecretValue.SecretKind, name, out value);

        public bool TryGetVariable(string name, [NotNullWhen(true)] out string? value) => Get(FleetSecretValue.VariableKind, name, out value);

        // The store masks every value it holds, these included.
        public IReadOnlyCollection<string> GetSecretValues() => [];

        private bool Get(string kind, string name, [NotNullWhen(true)] out string? value)
        {
            if (!store.TryGetFleet(kind, name, scopeKind, scopeId, shell, out value, out var info)) return false;
            used?.Invoke(info);
            return true;
        }
    }
}

internal sealed class FleetCopyDocument
{
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    [JsonPropertyName("scope_id")]
    public string? ScopeId { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("host_only")]
    public bool HostOnly { get; set; }

    [JsonPropertyName("received_at")]
    public DateTimeOffset ReceivedAt { get; set; }

    // The protected value, in base 64.
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}
