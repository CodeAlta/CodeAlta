namespace CodeAlta.Sandbox;

/// <summary>Where the sandbox of a session was decided.</summary>
public enum SandboxScope
{
    /// <summary>Nowhere: no scope says anything, the session is not sandboxed.</summary>
    None,

    /// <summary>The setting of every session.</summary>
    AllSessions,

    /// <summary>The space of the session.</summary>
    Space,

    /// <summary>The project of the session.</summary>
    Project,

    /// <summary>The provider of the session.</summary>
    Provider,

    /// <summary>The model of the session, in its provider.</summary>
    Model,

    /// <summary>The choice made for the session itself.</summary>
    Session,
}

/// <summary>What each scope says of the sandbox of one session: a profile name, <see cref="SandboxProfiles.Off"/>, or null for nothing.</summary>
public sealed record SandboxScopeValues
{
    /// <summary>Gets the choice made for the session itself.</summary>
    public string? Session { get; init; }

    /// <summary>Gets the value of its model.</summary>
    public string? Model { get; init; }

    /// <summary>Gets the value of its provider.</summary>
    public string? Provider { get; init; }

    /// <summary>Gets the value of its project.</summary>
    public string? Project { get; init; }

    /// <summary>Gets the value of its space.</summary>
    public string? Space { get; init; }

    /// <summary>Gets the value of every session.</summary>
    public string? AllSessions { get; init; }
}

/// <summary>The sandbox of a session: the profile it runs with, or none, and the scope that decided it.</summary>
/// <param name="Profile">The profile name, or null when the session is not sandboxed.</param>
/// <param name="Source">The scope that decided it.</param>
public sealed record SandboxChoice(string? Profile, SandboxScope Source)
{
    /// <summary>Gets whether the session is sandboxed.</summary>
    public bool IsSandboxed => Profile is not null;

    /// <summary>Gets the choice of a session no scope sandboxes.</summary>
    public static SandboxChoice None { get; } = new(null, SandboxScope.None);
}

/// <summary>The precedence of the scopes that sandbox a session.</summary>
public static class SandboxScopes
{
    /// <summary>
    /// Decides the sandbox of a session: the most specific scope that says something decides, in the order session,
    /// model, provider, project, space, every session. <see cref="SandboxProfiles.Off"/> in a scope turns the sandbox off.
    /// </summary>
    /// <param name="values">What each scope says.</param>
    /// <returns>The choice and its scope.</returns>
    /// <remarks>The provider comes before the project: a sandbox set for a model or a provider is distrust of that model,
    /// which follows it into every project.</remarks>
    public static SandboxChoice Resolve(SandboxScopeValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach (var (value, scope) in new[]
        {
            (values.Session, SandboxScope.Session), (values.Model, SandboxScope.Model), (values.Provider, SandboxScope.Provider),
            (values.Project, SandboxScope.Project), (values.Space, SandboxScope.Space), (values.AllSessions, SandboxScope.AllSessions),
        })
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var trimmed = value.Trim();
            return trimmed.Equals(SandboxProfiles.Off, StringComparison.OrdinalIgnoreCase) ? new(null, scope) : new(trimmed, scope);
        }

        return SandboxChoice.None;
    }
}
