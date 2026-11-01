using System.Text.RegularExpressions;

namespace CodeAlta.Plugin.Mcp;

/// <summary>
/// The MCP servers CodeAlta knows without a file: Safari 27 and later ship one, <c>safaridriver --mcp</c>, that drives a
/// window Safari marks as controlled by automation. The browser plugin activates it for a session that asks for Safari.
/// </summary>
internal static partial class McpBuiltInServers
{
    /// <summary>The key of Safari's server.</summary>
    public const string Safari = "safari";

    /// <summary>The path of the source of the built-in servers: no file is read or written there.</summary>
    public const string SourcePath = "builtin:mcpServers";

    private const string SafariDriver = "/usr/bin/safaridriver";

    /// <summary>The built-in servers of this computer: Safari's on a Mac whose Safari is version 27 or later.</summary>
    public static IReadOnlyList<McpServerDefinition> ForThisComputer()
        => OperatingSystem.IsMacOS() && File.Exists(SafariDriver) && SafariMajorVersion("/Applications/Safari.app/Contents/Info.plist") >= 27 ? [SafariServer(SafariDriver)] : [];

    /// <summary>The definition of Safari's server.</summary>
    /// <param name="driver">The path of <c>safaridriver</c>.</param>
    public static McpServerDefinition SafariServer(string driver) => new()
    {
        Key = Safari,
        Transport = McpTransportKind.Stdio,
        SourceScope = McpConfigScope.Global,
        SourcePath = SourcePath,
        SourceFlavor = McpConfigFlavor.CodeAlta,
        SourceOrigin = McpConfigOrigin.Common,
        Command = driver,
        Args = ["--mcp"],
    };

    /// <summary>The major version of Safari, read from its <c>Info.plist</c>; 0 when it cannot be read.</summary>
    internal static int SafariMajorVersion(string infoPlist)
    {
        try
        {
            return File.Exists(infoPlist) && ShortVersion().Match(File.ReadAllText(infoPlist)) is { Success: true } match && int.TryParse(match.Groups[1].ValueSpan, out var major) ? major : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    [GeneratedRegex(@"<key>CFBundleShortVersionString</key>\s*<string>(\d+)")]
    private static partial Regex ShortVersion();
}
