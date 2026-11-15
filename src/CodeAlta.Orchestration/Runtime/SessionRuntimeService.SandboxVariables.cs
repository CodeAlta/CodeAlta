using System.Collections.Concurrent;
using CodeAlta.Catalog.Secrets;
using CodeAlta.Sandbox;

namespace CodeAlta.Orchestration.Runtime;

public sealed partial class SessionRuntimeService
{
    // How long the values of the variables a profile sets are kept for a session: each tool call builds its policy, and a
    // secret is not read from its store or its vault at each of them. A value that changed starts another worker then.
    private static readonly TimeSpan SandboxVariablesTime = TimeSpan.FromMinutes(5);

    // The variables a profile sets for a session, with their values: by session, for the entries they were read from.
    private readonly ConcurrentDictionary<string, SandboxVariableValues> _sandboxVariables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or initializes what reads the references (<c>${secret:NAME}</c>, <c>${var:NAME}</c>, <c>${env:NAME}</c>) of the
    /// variables a sandbox profile sets, for the work of a session. Null, the default, reads the environment of this
    /// process alone: a secret or a variable of the store is then not set.
    /// </summary>
    public Func<SecretScope, SecretReferenceResolver>? SandboxVariables { get; init; }

    private SecretReferenceResolver SandboxVariableResolver(SecretScope scope)
        => SandboxVariables?.Invoke(scope) ?? new SecretReferenceResolver(Environment.GetEnvironmentVariable, []);

    // The profile of a session with the value of each variable it sets, and why one has none. The values are those of the
    // machine, the same for every session of its owner, never those of a space, a project or an environment: the sessions
    // of a profile share its home folder and its caches, where one could leave what reads the variables of another. The
    // work of another person gets none of the owner's values.
    private async Task<(ResolvedSandboxProfile Profile, string? Problem)> SandboxVariablesOfAsync(string sessionId, ResolvedSandboxProfile profile,
        CancellationToken cancellationToken)
    {
        if (!profile.Env.Any(HasReference)) return (profile, null);
        var written = string.Join('\u001F', profile.Env);
        if (_sandboxVariables.TryGetValue(sessionId, out var kept) && kept.Written == written && Environment.TickCount64 - kept.At < SandboxVariablesTime.TotalMilliseconds)
            return (profile with { Env = kept.Entries }, null);

        var scope = new SecretScope(null, null, null) { RunId = sessionId };
        if (Identities.IsEnabled)
        {
            var record = await GetSessionSpaceAsync(sessionId, cancellationToken).ConfigureAwait(false);
            // A run without a session (a script of an automation) is the work of no space and no project.
            if (record is not null)
            {
                if (await Identities.ScopeOfAsync(record.SpaceId, record.ProjectId, sessionId, record.ParentSessionId, cancellationToken).ConfigureAwait(false) is not { } read)
                    return (WithoutReferences(profile), $"The sandbox profile '{profile.Name}' sets variables from secrets, and the spaces cannot be read now.");
                scope = scope with { OwnerWork = read.OwnerWork };
            }
        }

        var (resolved, problem) = ResolveSandboxVariables(profile, SandboxVariableResolver(scope));
        // Only what has every value is kept: a secret set meanwhile is read at the next call.
        if (problem is null) _sandboxVariables[sessionId] = new(written, resolved.Env, Environment.TickCount64);
        return (resolved, problem);
    }

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

    private sealed record SandboxVariableValues(string Written, IReadOnlyList<string> Entries, long At);
}
