using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace CodeAlta.Catalog.Secrets;

/// <summary>What a reference of the configuration names.</summary>
public enum SecretReferenceKind
{
    /// <summary><c>${env:NAME}</c>: a variable of the environment of the process.</summary>
    Environment,

    /// <summary><c>${secret:NAME}</c>: a secret of the store.</summary>
    Secret,

    /// <summary><c>${var:NAME}</c>: a variable of the store.</summary>
    Variable,
}

/// <summary>A reference of the configuration to a value by name: <c>${env:NAME}</c>, <c>${secret:NAME}</c> or <c>${var:NAME}</c>.</summary>
/// <param name="Kind">What it names.</param>
/// <param name="Name">The name.</param>
public readonly record struct SecretReference(SecretReferenceKind Kind, string Name)
{
    /// <summary>Gets the reference as it is written, such as <c>${secret:GRAFANA_TOKEN}</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => "${" + SecretReferences.PrefixOf(Kind) + ":" + Name + "}";
}

/// <summary>Thrown when a reference of the configuration names a value that is not set or cannot be read.</summary>
public sealed class SecretReferenceException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SecretReferenceException"/> class.</summary>
    /// <param name="reference">The reference.</param>
    /// <param name="message">What is wrong, naming the reference.</param>
    /// <param name="innerException">The cause, or <see langword="null"/>.</param>
    public SecretReferenceException(SecretReference reference, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Reference = reference;
    }

    /// <summary>Gets the reference whose value is missing.</summary>
    public SecretReference Reference { get; }
}

/// <summary>The work of a run, which tells the scopes its references resolve in (<c>doc/fleet/secrets.md</c>, scopes).</summary>
/// <param name="SpaceId">The space of the run on this machine; null for none.</param>
/// <param name="ProjectId">The project of the run on this machine; null for a chat.</param>
/// <param name="Environment">
/// The scope of the environment the run was admitted to, as its values are kept: the organization and the id its signed definition
/// names, never its name alone, which another environment may have too; null for none.
/// </param>
public sealed record SecretScope(string? SpaceId, string? ProjectId, string? Environment)
{
    /// <summary>Gets the work of no space, project or environment.</summary>
    public static SecretScope None { get; } = new(null, null, null);

    /// <summary>Gets the run (a session, a run of an automation) its uses are recorded for; null records none.</summary>
    public string? RunId { get; init; }

    /// <summary>
    /// Gets whether the run is the work of the owner of the machine, whose own values then come first. The work of another
    /// person (a session a member of a team started on a team machine) gets none of the owner's values: neither the store
    /// of the machine nor the values the fleet gave the owner's person, spaces and projects; only those of the environment
    /// it was admitted to.
    /// </summary>
    public bool OwnerWork { get; init; } = true;
}

/// <summary>Reads the references of the configuration: <c>${env:NAME}</c>, <c>${secret:NAME}</c> and <c>${var:NAME}</c>.</summary>
public static class SecretReferences
{
    /// <summary>Gets the word of a kind of reference: <c>env</c>, <c>secret</c> or <c>var</c>.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The word.</returns>
    public static string PrefixOf(SecretReferenceKind kind) => kind switch
    {
        SecretReferenceKind.Secret => "secret",
        SecretReferenceKind.Variable => "var",
        _ => "env",
    };

    /// <summary>Reads a reference that starts at an index of a text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="index">Where <c>${</c> is.</param>
    /// <param name="reference">The reference.</param>
    /// <param name="length">How many characters it takes, <c>}</c> included.</param>
    /// <returns><see langword="true"/> for a reference of a valid name.</returns>
    public static bool TryReadAt(string text, int index, out SecretReference reference, out int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        reference = default;
        length = 0;
        if (!text.AsSpan(index).StartsWith("${", StringComparison.Ordinal)) return false;
        var end = text.IndexOf('}', index + 2);
        var colon = text.IndexOf(':', index + 2);
        if (end < 0 || colon < 0 || colon > end) return false;
        SecretReferenceKind? kind = text.AsSpan(index + 2, colon - index - 2) switch
        {
            "env" => SecretReferenceKind.Environment,
            "secret" => SecretReferenceKind.Secret,
            "var" => SecretReferenceKind.Variable,
            _ => null,
        };
        var name = text[(colon + 1)..end];
        if (kind is null || !LocalSecretStore.IsName(name)) return false;
        reference = new(kind.Value, name);
        length = end - index + 1;
        return true;
    }

    /// <summary>Lists the references of a text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The references, in their order.</returns>
    public static IReadOnlyList<SecretReference> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<SecretReference>();
        for (var index = text.IndexOf("${", StringComparison.Ordinal); index >= 0; index = text.IndexOf("${", index + 2, StringComparison.Ordinal))
        {
            if (TryReadAt(text, index, out var reference, out _)) found.Add(reference);
        }

        return found;
    }

    /// <summary>Tells whether a text is one reference and nothing else, such as <c>${secret:OPENAI_API_KEY}</c>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="reference">The reference.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public static bool TryParseLone([NotNullWhen(true)] string? text, out SecretReference reference)
    {
        reference = default;
        return text is not null && TryReadAt(text, 0, out reference, out var length) && length == text.Length;
    }

    /// <summary>Gives the message of a reference whose value is not set.</summary>
    /// <param name="reference">The reference.</param>
    /// <returns>The message, naming the reference.</returns>
    public static string MissingMessage(SecretReference reference) => reference.Kind switch
    {
        SecretReferenceKind.Secret => $"The secret {reference.Name} is not set on this machine.",
        SecretReferenceKind.Variable => $"The variable {reference.Name} is not set on this machine.",
        _ => $"The environment variable {reference.Name} is not set.",
    };
}

/// <summary>
/// Gives the values of the references of the configuration: a variable of the environment for <c>${env:NAME}</c>, and for
/// <c>${secret:NAME}</c> and <c>${var:NAME}</c> the value of the first source that has it: the machine's own store for
/// the work of its owner, then the values the fleet gave it, the narrowest scope first (the environment of the run, the
/// project, the space, the person; teams and the organization later). A value that is not set is an error that names it,
/// never an empty value.
/// </summary>
public sealed class SecretReferenceResolver
{
    private readonly Func<string, string?> _environment;
    private readonly IReadOnlyList<ISecretLookup> _sources;

    /// <summary>Initializes a new instance of the <see cref="SecretReferenceResolver"/> class.</summary>
    /// <param name="environment">Reads a variable of the environment.</param>
    /// <param name="sources">The sources of secrets and variables, the narrowest scope first.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public SecretReferenceResolver(Func<string, string?> environment, IReadOnlyList<ISecretLookup> sources)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(sources);
        _environment = environment;
        _sources = sources;
    }

    /// <summary>
    /// Gets a resolver of the environment of this process and the store of a profile, for what runs on the host side
    /// (providers, MCP servers, plugin tools): the machine's own values first, then those the fleet gave the person's work.
    /// </summary>
    /// <param name="root">The root of the profile, <c>~/.alta</c>.</param>
    /// <returns>The resolver.</returns>
    public static SecretReferenceResolver ForMachine(string root)
    {
        var store = new LocalSecretStore(root);
        return new(Environment.GetEnvironmentVariable, [store, store.FleetScope(FleetSecretScopes.Person, null, shell: false)]);
    }

    /// <summary>Gets a resolver for the work of a run: its environment, its project and its space (<see cref="ForScope(string, SecretScope, bool, Action{FleetSecretCopyInfo}?)"/>).</summary>
    /// <param name="root">The root of the profile, <c>~/.alta</c>.</param>
    /// <param name="scope">The work of the run.</param>
    /// <param name="shell">Whether the values go to the agent's shell, which never gets a host-only secret.</param>
    /// <returns>The resolver.</returns>
    public static SecretReferenceResolver ForScope(string root, SecretScope scope, bool shell) => ForScope(root, scope, shell, null);

    /// <summary>
    /// Gets a resolver for the work of a run (<c>doc/fleet/secrets.md</c>, scopes): the machine's own values first for the
    /// work of its owner, then the values the fleet gave this machine, the narrowest scope first: the run's environment,
    /// its project, its space, the person.
    /// </summary>
    /// <param name="root">The root of the profile, <c>~/.alta</c>.</param>
    /// <param name="scope">The work of the run.</param>
    /// <param name="shell">Whether the values go to the agent's shell, which never gets a host-only secret.</param>
    /// <param name="used">Told each value of the fleet given, for the record of its uses; null for none.</param>
    /// <returns>The resolver.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is null.</exception>
    public static SecretReferenceResolver ForScope(string root, SecretScope scope, bool shell, Action<FleetSecretCopyInfo>? used)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var store = new LocalSecretStore(root);
        var sources = new List<ISecretLookup>(5);
        if (scope.OwnerWork) sources.Add(store);
        if (!string.IsNullOrWhiteSpace(scope.Environment)) sources.Add(store.FleetScope(FleetSecretScopes.Environment, scope.Environment, shell, used));
        // The values of the owner's person, spaces and projects are the owner's: the work of another person never gets them.
        if (!scope.OwnerWork) return new(Environment.GetEnvironmentVariable, sources);
        if (!string.IsNullOrWhiteSpace(scope.ProjectId)) sources.Add(store.FleetScope(FleetSecretScopes.Project, scope.ProjectId, shell, used));
        if (!string.IsNullOrWhiteSpace(scope.SpaceId)) sources.Add(store.FleetScope(FleetSecretScopes.Space, scope.SpaceId, shell, used));
        sources.Add(store.FleetScope(FleetSecretScopes.Person, null, shell, used));
        return new(Environment.GetEnvironmentVariable, sources);
    }

    /// <summary>Gets the sources of secrets and variables, the narrowest scope first.</summary>
    public IReadOnlyList<ISecretLookup> Sources => _sources;

    /// <summary>
    /// Gets what is told each value of a secret before it is given (a masking of the host: it masks the value from that moment, for
    /// as long as it lives, whatever the store keeps of it later); null tells nothing.
    /// </summary>
    public Action<string>? Issued { get; init; }

    /// <summary>
    /// Gets a resolver for the work of a run, as <see cref="ForScope(string, SecretScope, bool, Action{FleetSecretCopyInfo}?)"/> does, that
    /// tells each value of a secret before it gives it (<see cref="Issued"/>).
    /// </summary>
    /// <param name="root">The root of the profile, <c>~/.alta</c>.</param>
    /// <param name="scope">The work of the run.</param>
    /// <param name="shell">Whether the values go to the agent's shell, which never gets a host-only secret.</param>
    /// <param name="used">Told each value of the fleet given, for the record of its uses; null for none.</param>
    /// <param name="issued">Told each value of a secret before it is given; null for none.</param>
    /// <returns>The resolver.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="scope"/> is null.</exception>
    public static SecretReferenceResolver ForScope(string root, SecretScope scope, bool shell, Action<FleetSecretCopyInfo>? used, Action<string>? issued)
    {
        var resolver = ForScope(root, scope, shell, used);
        return new SecretReferenceResolver(resolver._environment, resolver._sources) { Issued = issued };
    }

    /// <summary>Gets the value of a reference.</summary>
    /// <param name="reference">The reference.</param>
    /// <param name="value">Its value when it is set.</param>
    /// <returns><see langword="true"/> when it is set.</returns>
    /// <exception cref="SecretReferenceException">The store holds the secret but cannot read it back.</exception>
    public bool TryGet(SecretReference reference, [NotNullWhen(true)] out string? value)
    {
        if (reference.Kind == SecretReferenceKind.Environment)
        {
            value = _environment(reference.Name);
            return value is not null;
        }

        foreach (var source in _sources)
        {
            try
            {
                if (reference.Kind == SecretReferenceKind.Secret ? source.TryGetSecret(reference.Name, out value) : source.TryGetVariable(reference.Name, out value))
                {
                    // Masked before anything gets it.
                    if (reference.Kind == SecretReferenceKind.Secret) Issued?.Invoke(value);
                    return true;
                }
            }
            catch (Exception exception) when (exception is SecretStoreUnavailableException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new SecretReferenceException(reference, $"{reference} cannot be read: {exception.Message}", exception);
            }
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Reads a value by the key a reader of variables is given (<see cref="SpaceCliProfile.Resolve"/>): the name of a
    /// variable of the environment (or <c>env:NAME</c>), <c>secret:NAME</c> and <c>var:NAME</c> for a secret and a variable
    /// of the store.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The value, or <see langword="null"/> when it is not set or cannot be read.</returns>
    public string? Read(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var colon = key.IndexOf(':');
        var reference = colon < 0 ? new SecretReference(SecretReferenceKind.Environment, key)
            : key.AsSpan(0, colon) is "secret" ? new SecretReference(SecretReferenceKind.Secret, key[(colon + 1)..])
            : key.AsSpan(0, colon) is "var" ? new SecretReference(SecretReferenceKind.Variable, key[(colon + 1)..])
            : key.AsSpan(0, colon) is "env" ? new SecretReference(SecretReferenceKind.Environment, key[(colon + 1)..])
            : (SecretReference?)null;
        if (reference is not { } known) return null;
        try
        {
            return TryGet(known, out var value) ? value : null;
        }
        catch (SecretReferenceException)
        {
            return null;
        }
    }

    /// <summary>Gets the value of a reference.</summary>
    /// <param name="reference">The reference.</param>
    /// <returns>Its value.</returns>
    /// <exception cref="SecretReferenceException">It is not set, or cannot be read.</exception>
    public string Get(SecretReference reference)
        => TryGet(reference, out var value) ? value : throw new SecretReferenceException(reference, SecretReferences.MissingMessage(reference));

    /// <summary>Gives a text with the value of each of its references.</summary>
    /// <param name="text">The text, as written.</param>
    /// <returns>The text with the values; a text without reference as it is.</returns>
    /// <exception cref="SecretReferenceException">A reference names a value that is not set or cannot be read.</exception>
    public string Resolve(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = text.IndexOf("${", StringComparison.Ordinal);
        if (start < 0) return text;
        var builder = new StringBuilder(text.Length);
        var offset = 0;
        for (; start >= 0; start = text.IndexOf("${", start + 2, StringComparison.Ordinal))
        {
            if (start < offset || !SecretReferences.TryReadAt(text, start, out var reference, out var length)) continue;
            builder.Append(text, offset, start - offset).Append(Get(reference));
            offset = start + length;
        }

        return builder.Append(text, offset, text.Length - offset).ToString();
    }

    /// <summary>Gives a text with the value of each of its references, or what is missing.</summary>
    /// <param name="text">The text, as written.</param>
    /// <param name="resolved">The text with the values.</param>
    /// <param name="problem">What is missing, naming it.</param>
    /// <returns><see langword="true"/> when every reference has a value.</returns>
    public bool TryResolve(string text, [NotNullWhen(true)] out string? resolved, [NotNullWhen(false)] out string? problem)
    {
        try
        {
            resolved = Resolve(text);
            problem = null;
            return true;
        }
        catch (SecretReferenceException exception)
        {
            resolved = null;
            problem = exception.Message;
            return false;
        }
    }
}
