namespace CodeAlta.Plugin.Mcp;

// The references a session keeps of what it loaded from MCP (AgentRunTools.KeepLoaded): `mcp:<server>` for a server
// activated as a whole, `mcp:<server>/<tool>` for one tool. A tool name of MCP has no '/', a server key may.
internal static class McpToolReference
{
    public const string Prefix = "mcp:";

    public static string OfServer(string server) => Prefix + server;

    public static string OfTool(string server, string tool) => Prefix + server + "/" + tool;

    // Reads a reference: the server and, for one tool, its name. A reference of another plugin is not read.
    public static bool TryParse(string? reference, out string server, out string? tool)
    {
        server = string.Empty;
        tool = null;
        if (reference is null || !reference.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = reference[Prefix.Length..].Trim();
        var slash = rest.LastIndexOf('/');
        if (slash < 0)
        {
            server = rest;
            return server.Length > 0;
        }

        server = rest[..slash];
        tool = rest[(slash + 1)..];
        return server.Length > 0 && tool.Length > 0;
    }
}
