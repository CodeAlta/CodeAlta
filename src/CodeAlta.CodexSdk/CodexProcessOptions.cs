namespace CodeAlta.CodexSdk;

/// <summary>
/// Options that control how <see cref="CodexProcess"/> locates and starts the
/// codex app-server executable.
/// </summary>
public sealed class CodexProcessOptions
{
    /// <summary>
    /// Gets or sets an explicit path to the <c>codex</c> executable.
    /// When <see langword="null"/>, the executable is resolved from the pinned
    /// local CodeAlta Codex install.
    /// </summary>
    public string? CodexPath { get; set; }

    /// <summary>
    /// Gets or sets the pinned Codex release tag to install and run when
    /// <see cref="CodexPath"/> is not provided.
    /// </summary>
    public string? ReleaseTag { get; set; }

    /// <summary>
    /// Gets or sets the CodeAlta local root used for pinned Codex installations.
    /// When <see langword="null"/>, this defaults to <c>~/.codealta/cache</c>.
    /// </summary>
    public string? LocalRootPath { get; set; }

    /// <summary>
    /// Gets or sets an optional progress sink used while downloading or extracting
    /// a pinned Codex release.
    /// </summary>
    public IProgress<CodexInstallProgress>? Progress { get; set; }
}
