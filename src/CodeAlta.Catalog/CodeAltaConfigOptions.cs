namespace CodeAlta.Catalog;

/// <summary>
/// How a process reads and writes the configuration of the user: given to <see cref="CodeAltaConfigStore"/> through
/// <see cref="CatalogOptions.Config"/>, so every store built from the same catalog options reads the same settings.
/// </summary>
/// <remarks>
/// The configuration the store reads is the user's <c>config.toml</c> (none when <see cref="IgnoreUserConfig"/> is set),
/// then each of <see cref="Overlays"/> in order: tables merge key by key, and the later layer wins on a value, an array or
/// a value of another kind. A project's <c>.alta/config.toml</c> still overlays the result as it overlays the file.
/// Saves write the file alone, never the overlays.
/// </remarks>
public sealed record CodeAltaConfigOptions
{
    private readonly IReadOnlyList<CodeAltaConfigOverlay> _overlays = [];

    /// <summary>Gets the options of a process that reads and writes the user's configuration as it is.</summary>
    public static CodeAltaConfigOptions Default { get; } = new();

    /// <summary>
    /// Gets whether the user's <c>config.toml</c> is left out: it is neither read nor created, and the configuration is
    /// what <see cref="Overlays"/> say over an empty file.
    /// </summary>
    public bool IgnoreUserConfig { get; init; }

    /// <summary>Gets the configurations merged over the user's file, in order: the last one wins.</summary>
    /// <exception cref="ArgumentNullException">The value set is <see langword="null"/>.</exception>
    public IReadOnlyList<CodeAltaConfigOverlay> Overlays
    {
        get => _overlays;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _overlays = [.. value];
        }
    }

    /// <summary>
    /// Gets whether a setting no part of CodeAlta reads is an error instead of being left as it is, in the user's
    /// configuration, its overlays and the project configurations. Plugin tables beyond <c>enabled</c>, the
    /// <c>spaces</c> lists, the automations of the desktop and the providers of a type a plugin runs are not checked:
    /// their owners read them.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>
    /// Gets whether the store never writes a configuration file: a save fails with
    /// <see cref="InvalidOperationException"/>, and the creation of the first-run file does nothing.
    /// </summary>
    public bool ReadOnly { get; init; }
}
