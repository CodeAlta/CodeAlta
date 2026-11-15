using System.Collections.Concurrent;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Sandbox;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    // The variables a profile sets for a session, with their values: by session, for the entries they were read from. A
    // session keeps them while it is attached, as it keeps the variables of its identity: each tool call builds its policy,
    // and a secret is neither read from its vault nor asked for at each of them.
    private readonly ConcurrentDictionary<string, SandboxVariableValues> _sandboxVariables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or initializes what reads the references (<c>${secret:NAME}</c>, <c>${var:NAME}</c>, <c>${env:NAME}</c>) of the
    /// variables a sandbox profile sets. It is given the work whose values are read, and the scope of the attachment of the
    /// session when it is read as the session attaches, whose confirmations of the secrets that ask before use it joins; null
    /// for a read that asks nobody (a tool call, the check of a profile). Null, the default, reads the environment of this
    /// process alone: a secret or a variable of the store is then not set.
    /// </summary>
    public Func<SecretScope, SecretScope?, SecretReferenceResolver>? SandboxVariables { get; init; }

    private SecretReferenceResolver SandboxVariableResolver(SecretScope scope, SecretScope? attachment)
        => SandboxVariables?.Invoke(scope, attachment) ?? new SecretReferenceResolver(Environment.GetEnvironmentVariable, []);

    // The work the variables of a profile are read for. The values are those of the machine, the same for every session of
    // its owner, never those of a space, a project or an environment: the sessions of a profile share its home folder and
    // its caches, where one could leave what reads the variables of another. The work of another person gets none of the
    // owner's values.
    private static SecretScope SandboxVariableScope(string sessionId, bool ownerWork) => new(null, null, null) { RunId = sessionId, OwnerWork = ownerWork };

    // The profile of a session with the value of each variable it sets, and why one has none: the values read as it
    // attached, else read now, without asking anybody.
    private async Task<(ResolvedSandboxProfile Profile, string? Problem)> SandboxVariablesOfAsync(string sessionId, ResolvedSandboxProfile profile,
        CancellationToken cancellationToken)
    {
        if (!profile.Env.Any(HasReference)) return (profile, null);
        var written = WrittenKey(profile);
        if (_sandboxVariables.TryGetValue(sessionId, out var kept) && kept.Written == written) return (profile with { Env = kept.Entries }, null);

        var ownerWork = true;
        if (Identities.IsEnabled)
        {
            var record = await GetSessionSpaceAsync(sessionId, cancellationToken).ConfigureAwait(false);
            // A run without a session (a script of an automation) is the work of no space and no project.
            if (record is not null)
            {
                if (await Identities.ScopeOfAsync(record.SpaceId, record.ProjectId, sessionId, record.ParentSessionId, cancellationToken).ConfigureAwait(false) is not { } read)
                    return (WithoutReferences(profile), $"The sandbox profile '{profile.Name}' sets variables from secrets, and the spaces cannot be read now.");
                ownerWork = read.OwnerWork;
            }
        }

        var (resolved, problem) = ResolveSandboxVariables(profile, SandboxVariableResolver(SandboxVariableScope(sessionId, ownerWork), null));
        // Only what has every value is kept: a secret set meanwhile is read at the next call.
        if (problem is null) _sandboxVariables[sessionId] = new(written, resolved.Env);
        return (resolved, problem);
    }

    // What reads the variables the sandbox profile of a session sets as the session attaches, called with the scope of the
    // attachment after each build of its environment: a secret that asks before use is then asked for with those of its
    // identity, and read again once the user confirmed it. Null for a session whose profile sets none from a reference.
    private async Task<Action<SecretScope>?> SandboxVariablesAtAttachmentAsync(string sessionId, CancellationToken cancellationToken)
    {
        // What an earlier attachment read is not what this one was confirmed.
        _sandboxVariables.TryRemove(sessionId, out _);
        if ((await GetSessionSandboxAsync(sessionId, cancellationToken).ConfigureAwait(false)).Profile is not { } profile || !profile.Env.Any(HasReference)) return null;
        var written = WrittenKey(profile);
        return attachment =>
        {
            var (resolved, problem) = ResolveSandboxVariables(profile, SandboxVariableResolver(SandboxVariableScope(sessionId, attachment.OwnerWork), attachment));
            // A variable without value is told at the first command, which reads it again.
            if (problem is null) _sandboxVariables[sessionId] = new(written, resolved.Env);
            else _sandboxVariables.TryRemove(sessionId, out _);
        };
    }

    // An attachment that goes away keeps no value: the next one reads them again.
    internal void ForgetSandboxVariables(string sessionId) => _sandboxVariables.TryRemove(sessionId, out _);

    // The profile with the value of each reference of the variables it sets; a variable whose reference has no value is
    // left out, and named in the problem.
    internal static (ResolvedSandboxProfile Profile, string? Problem) ResolveSandboxVariables(ResolvedSandboxProfile profile, SecretReferenceResolver resolver)
    {
        if (!profile.Env.Any(HasReference)) return (profile, null);
        var entries = new List<string>(profile.Env.Count);
        string? problem = null;
        for (var index = 0; index < profile.Env.Count; index++)
        {
            var entry = profile.Env[index];
            if (!HasReference(entry) || !SandboxVariable.TryGetValue(entry, out var value))
            {
                entries.Add(entry);
            }
            else if (IsSetAgainAfter(profile.Env, index))
            {
                // Replaced by a profile that extends this one: its secret is not read, and may be missing.
            }
            else if (resolver.TryResolve(value, out var resolved, out var missing))
            {
                entries.Add(SandboxVariable.NameOf(entry) + "=" + resolved);
            }
            else
            {
                problem ??= $"The sandbox profile '{profile.Name}' sets the variable {SandboxVariable.NameOf(entry)}, which has no value: {missing}";
            }
        }

        return (profile with { Env = entries }, problem);
    }

    // Whether a later entry sets the variable of an entry: the later one wins (SandboxPolicyBuilder).
    private static bool IsSetAgainAfter(IReadOnlyList<string> entries, int index)
    {
        var name = SandboxVariable.NameOf(entries[index]);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var later = index + 1; later < entries.Count; later++)
        {
            if (SandboxVariable.TryGetValue(entries[later], out _) && string.Equals(SandboxVariable.NameOf(entries[later]), name, comparison)) return true;
        }

        return false;
    }

    private static ResolvedSandboxProfile WithoutReferences(ResolvedSandboxProfile profile) => profile with { Env = [.. profile.Env.Where(static entry => !HasReference(entry))] };

    // Whether an entry sets a variable from a reference: `NAME=${secret:TOKEN}`.
    private static bool HasReference(string entry)
        => SandboxVariable.TryGetValue(entry, out var value) && SecretReferences.Find(value).Count > 0;

    private static string WrittenKey(ResolvedSandboxProfile profile) => string.Join('\u001F', profile.Env);

    private sealed record SandboxVariableValues(string Written, IReadOnlyList<string> Entries);
}
