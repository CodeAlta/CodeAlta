namespace CodeAlta.Mcp;

/// <summary>
/// Represents configuration options for the in-process CodeAlta MCP server.
/// </summary>
public sealed class CodeAltaMcpOptions
{
    /// <summary>
    /// Gets or sets the MCP server name.
    /// </summary>
    public string ServerName { get; set; } = "CodeAlta";

    /// <summary>
    /// Gets or sets the MCP server version.
    /// </summary>
    public string ServerVersion { get; set; } = "0.1.0";

    /// <summary>
    /// Gets or sets the artifact root used by MCP artifact/task tools.
    /// </summary>
    public string ArtifactRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".alta",
        "artifacts");
}
