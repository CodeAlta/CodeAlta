namespace CodeAlta.Catalog;

/// <summary>
/// The mode of Code Mode a session gets (<c>off</c>, <c>on</c> or <c>only</c>), from its provider and its model: the mode
/// of its model in the user's file (<c>[providers.x.model_overrides."id"] code_mode</c>), else of its provider
/// (<c>[providers.x] code_mode</c>), else the setting of every session (<c>[code_mode] mode</c>), else <c>on</c>. The
/// tools of a session and its instructions both resolve it here, so that they never disagree. A project's own file never
/// chooses it.
/// </summary>
public static class CodeModeScopes
{
    /// <summary>The mode without Code Mode.</summary>
    public const string Off = "off";

    /// <summary>The mode with <c>exec</c> and <c>wait</c> beside the other tools: the default.</summary>
    public const string On = "on";

    /// <summary>The mode with <c>exec</c> and <c>wait</c> in place of the other tools.</summary>
    public const string Only = "only";

    /// <summary>The modes: <c>off</c>, <c>on</c> and <c>only</c>.</summary>
    public static IReadOnlyList<string> Modes { get; } = [Off, On, Only];

    /// <summary>Gets a mode as it is written, or null when it is not one (a blank value, a mode this version does not know).</summary>
    /// <param name="mode">The value.</param>
    /// <returns><c>off</c>, <c>on</c>, <c>only</c> or null.</returns>
    public static string? Known(string? mode)
        => mode?.Trim().ToLowerInvariant() is { } normalized && Modes.Contains(normalized) ? normalized : null;

    /// <summary>Resolves the mode of a session from what each scope chooses; a value that is not a mode chooses nothing.</summary>
    /// <param name="model">The mode of its model, or null.</param>
    /// <param name="provider">The mode of its provider, or null.</param>
    /// <param name="allSessions">The setting of every session, or null.</param>
    /// <returns><c>off</c>, <c>on</c> or <c>only</c>; <c>on</c> when no scope chooses.</returns>
    public static string Resolve(string? model, string? provider, string? allSessions)
        => Known(model) ?? Known(provider) ?? Known(allSessions) ?? On;
}
