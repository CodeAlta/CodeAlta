namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// Where the plugin finds its files: the CodeAlta root (<c>mcp.json</c>, <c>config.toml</c>, the OAuth tokens under
/// <c>auth/mcp</c>, the lists under <c>cache/mcp</c>, the approvals under <c>plugin-data/mcp</c>) and the home whose
/// configurations of other tools it reads (<c>~/.copilot/mcp-config.json</c>).
/// </summary>
/// <remarks>
/// A home alone has its CodeAlta root in <c>&lt;home&gt;/.alta</c>; no home is the one of the current user. A CodeAlta root
/// given apart (a separate profile, <c>altacli --profile</c>) reads the configurations of other tools only from a home
/// given with it: without one, it reads none, and nothing of the current user's home.
/// </remarks>
/// <param name="UserHomeDirectory">The home folder, or null for the one of the current user.</param>
/// <param name="CodeAltaRoot">The CodeAlta root in place of <c>&lt;home&gt;/.alta</c>, or null.</param>
internal readonly record struct McpHome(string? UserHomeDirectory, string? CodeAltaRoot)
{
    /// <summary>A home alone, as the plugin always took it: its CodeAlta root is <c>&lt;home&gt;/.alta</c>.</summary>
    public static implicit operator McpHome(string? userHomeDirectory) => new(userHomeDirectory, null);

    /// <summary>Gets whether nothing was given: the home of the current user.</summary>
    public bool IsDefault => string.IsNullOrWhiteSpace(UserHomeDirectory) && string.IsNullOrWhiteSpace(CodeAltaRoot);

    /// <summary>Gets the CodeAlta root.</summary>
    public string Root => string.IsNullOrWhiteSpace(CodeAltaRoot)
        ? Path.Combine(McpConfigDiscovery.GetUserHome(UserHomeDirectory), ".alta")
        : Path.GetFullPath(CodeAltaRoot);

    /// <summary>Gets the home whose configurations of other tools are read, or null to read none.</summary>
    public string? OtherToolsHome => string.IsNullOrWhiteSpace(CodeAltaRoot)
        ? McpConfigDiscovery.GetUserHome(UserHomeDirectory)
        : string.IsNullOrWhiteSpace(UserHomeDirectory) ? null : UserHomeDirectory;

    /// <summary>Gets a key that tells two homes apart, for a cache.</summary>
    public string Key => string.Concat(Root, "\n", OtherToolsHome);
}
