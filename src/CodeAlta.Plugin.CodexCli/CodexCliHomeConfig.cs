using System.Text;

namespace CodeAlta.Plugin.CodexCli;

/// <summary>
/// The <c>config.toml</c> CodeAlta writes in the <c>CODEX_HOME</c> of a provider before it starts an app-server: Codex
/// keeps its model loop and its coding tools, and CodeAlta takes over the instructions, the tools, delegation, skills,
/// plugins, hooks, approvals and the sandbox. The user's <c>~/.codex</c> is never read.
/// </summary>
/// <remarks>
/// What depends on the session (the MCP server of its tools, web search, the trust of its folder) is not here: it goes
/// with each <c>thread/start</c>, <c>thread/resume</c> and <c>thread/fork</c>, as Codex does not keep it.
/// </remarks>
internal static class CodexCliHomeConfig
{
    /// <summary>The name of the file.</summary>
    public const string FileName = "config.toml";

    /// <summary>
    /// The files and folders of the home that configure what Codex runs later, also outside a sandbox: they stay
    /// read-only, and are not created, in the sandbox of a session.
    /// </summary>
    public static IReadOnlyList<string> ProtectedEntries { get; } =
        [FileName, "AGENTS.md", "AGENTS.override.md", ".env", "rules", "hooks.json", "skills", "plugins", "prompts"];

    /// <summary>Gets the content of the file.</summary>
    public static string Content { get; } = CreateContent();

    /// <summary>Writes the file in a home, when its content differs, and creates the home.</summary>
    /// <param name="home">The <c>CODEX_HOME</c> of the provider.</param>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder cannot be written.</exception>
    public static void Write(string home)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, FileName);
        try
        {
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), Content, StringComparison.Ordinal))
            {
                return;
            }
        }
        catch (IOException)
        {
            // Written again below.
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, Content, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static string CreateContent()
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Written by CodeAlta before each start of the Codex app-server: changes are replaced.");
        builder.AppendLine("# CodeAlta runs its own sandbox, instructions, tools, approvals and sub-agents around Codex.");
        builder.AppendLine();
        // Codex's own sandbox is off: CodeAlta's container holds the process of a sandboxed session.
        builder.AppendLine("sandbox_mode = \"danger-full-access\"");
        // Approvals come to CodeAlta, which asks the user or answers by the permission mode of the session.
        builder.AppendLine("approvals_reviewer = \"user\"");
        // auth.json in the home: the keychain of the system is out of reach in a sandbox.
        builder.AppendLine("cli_auth_credentials_store = \"file\"");
        builder.AppendLine("mcp_oauth_credentials_store = \"file\"");
        // CodeAlta gives the AGENTS.md of the project in its own instructions.
        builder.AppendLine("project_doc_max_bytes = 0");
        // Codex's text about its own sandbox, apps and collaboration modes would be wrong under CodeAlta.
        builder.AppendLine("include_permissions_instructions = false");
        builder.AppendLine("include_apps_instructions = false");
        builder.AppendLine("include_collaboration_mode_instructions = false");
        builder.AppendLine("check_for_update_on_startup = false");
        builder.AppendLine("analytics = { enabled = false }");
        builder.AppendLine("feedback = { enabled = false }");
        builder.AppendLine("otel = { exporter = \"none\", trace_exporter = \"none\", metrics_exporter = \"none\" }");
        builder.AppendLine("history = { persistence = \"none\" }");
        // Delegation goes through CodeAlta sessions the user sees.
        builder.AppendLine("agents = { enabled = false }");
        builder.AppendLine("skills = { include_instructions = false, bundled = { enabled = false } }");
        builder.AppendLine();
        builder.AppendLine("[features]");
        foreach (var feature in (string[])
                 [
                     "multi_agent", "multi_agent_v2", "apps", "plugins", "remote_plugin", "hooks", "tool_suggest",
                     "guardian_approval", "goals", "image_generation", "browser_use", "computer_use", "in_app_browser", "skill_search",
                     "skill_mcp_dependency_install", "memories",
                 ])
        {
            builder.Append(feature).AppendLine(" = false");
        }

        return builder.ToString();
    }
}
