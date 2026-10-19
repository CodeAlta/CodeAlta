using System.Text.Json;

namespace CodeAlta.Plugin.Mcp;

// A file a discovery reads: one of CodeAlta (origin CodeAlta), which it writes, or one of another tool.
internal readonly record struct McpConfigSourceFile(McpConfigScope Scope, McpConfigOrigin Origin, string Path);

internal sealed class McpConfigDiscovery
{
    public McpConfigSnapshot Discover(McpConfigPathOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var home = GetUserHome(options.UserHomeDirectory);
        var projectDirectory = string.IsNullOrWhiteSpace(options.ProjectDirectory) ? null : Path.GetFullPath(options.ProjectDirectory);
        var boundedRead = options.BoundedRead ?? !options.ProbeWritability;
        var context = new McpExternalContext(projectDirectory, home);
        var sources = new List<McpConfigSource>(2);
        var external = new List<McpConfigSource>(4);
        foreach (var file in GetSourceFiles(projectDirectory, home))
        {
            if (file.Origin == McpConfigOrigin.CodeAlta)
            {
                sources.Add(ReadSource(file.Scope, file.Path, options.ProbeWritability, boundedRead));
            }
            else
            {
                AddExternalSource(external, file.Scope, file.Origin, file.Path, context);
            }
        }

        var (effective, shadowed) = BuildOverlay(sources, external);
        return new McpConfigSnapshot
        {
            Sources = sources,
            ExternalSources = external,
            EffectiveServers = effective,
            ShadowedServers = shadowed,
            DefaultWriteScope = projectDirectory is null ? McpConfigScope.Global : McpConfigScope.Project,
        };
    }

    // Every file a discovery reads, in the order it reads them: the two files of CodeAlta (global, then the project's), then
    // those of other tools. A cache of discoveries watches the same files.
    internal static IReadOnlyList<McpConfigSourceFile> GetSourceFiles(string? projectDirectory, string home)
    {
        var files = new List<McpConfigSourceFile>(6)
        {
            new(McpConfigScope.Global, McpConfigOrigin.CodeAlta, Path.Combine(home, ".alta", "mcp.json")),
        };
        if (projectDirectory is not null)
        {
            files.Add(new(McpConfigScope.Project, McpConfigOrigin.CodeAlta, Path.Combine(projectDirectory, ".alta", "mcp.json")));
            files.Add(new(McpConfigScope.Project, McpConfigOrigin.Common, Path.Combine(projectDirectory, ".mcp.json")));
            files.Add(new(McpConfigScope.Project, McpConfigOrigin.Copilot, Path.Combine(projectDirectory, ".github", "mcp.json")));
            files.Add(new(McpConfigScope.Project, McpConfigOrigin.Vscode, Path.Combine(projectDirectory, ".vscode", "mcp.json")));
        }

        files.Add(new(McpConfigScope.Global, McpConfigOrigin.Copilot, Path.Combine(home, ".copilot", "mcp-config.json")));
        return files;
    }

    public static string GetGlobalConfigPath(string? userHomeDirectory = null)
        => Path.Combine(GetUserHome(userHomeDirectory), ".alta", "mcp.json");

    internal static string GetUserHome(string? userHomeDirectory)
    {
        var home = string.IsNullOrWhiteSpace(userHomeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userHomeDirectory;
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME") ?? Environment.CurrentDirectory;
        }

        return home;
    }

    public static string GetProjectConfigPath(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        return Path.Combine(Path.GetFullPath(projectDirectory), ".alta", "mcp.json");
    }

    private static McpConfigSource ReadSource(McpConfigScope scope, string path, bool probeWritability, bool boundedRead)
    {
        var directory = Path.GetDirectoryName(path);
        var directoryExists = !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory);
        if (!File.Exists(path))
        {
            return new McpConfigSource
            {
                Scope = scope,
                Path = path,
                Exists = false,
                DirectoryExists = directoryExists,
                IsWritable = probeWritability && (directoryExists ? IsDirectoryWritable(directory!) : CanCreateDirectory(directory)),
            };
        }

        try
        {
            var document = McpConfigFormatAdapter.ParseDocument(boundedRead ? McpBoundedTextReader.Read(path) : File.ReadAllText(path));
            var servers = McpConfigFormatAdapter.ReadServers(document, scope, path);
            return new McpConfigSource
            {
                Scope = scope,
                Path = path,
                Exists = true,
                DirectoryExists = directoryExists,
                IsWritable = probeWritability && CanWriteFile(path),
                Flavor = document.Flavor,
                RootKey = document.RootKey,
                Servers = servers,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            return new McpConfigSource
            {
                Scope = scope,
                Path = path,
                Exists = true,
                DirectoryExists = directoryExists,
                IsWritable = probeWritability && CanWriteFile(path),
                IsValid = false,
                Diagnostic = ex.Message,
            };
        }
    }

    // A file of another tool is read where it is and never written. One that does not exist is not a source at all:
    // CodeAlta does not create it.
    private static void AddExternalSource(List<McpConfigSource> sources, McpConfigScope scope, McpConfigOrigin origin, string path, McpExternalContext context)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var document = McpConfigFormatAdapter.ParseExternalDocument(McpBoundedTextReader.Read(path));
            var servers = McpConfigFormatAdapter.ReadExternalServers(document, scope, origin, path, context, out var skipped);
            sources.Add(new McpConfigSource
            {
                Scope = scope,
                Origin = origin,
                Path = path,
                Exists = true,
                DirectoryExists = true,
                Flavor = document.Flavor,
                RootKey = document.RootKey,
                Servers = servers,
                SkippedServers = skipped,
            });
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // ArgumentException: a JSON object that names a property twice.
            sources.Add(new McpConfigSource
            {
                Scope = scope,
                Origin = origin,
                Path = path,
                Exists = true,
                DirectoryExists = true,
                IsValid = false,
                Diagnostic = ex.Message,
            });
        }
    }

    // The first definition of a key is the one in effect. A project comes before the user, and in each of them the
    // file of CodeAlta comes before the files of other tools, in the order they were added.
    private static (IReadOnlyList<McpEffectiveServer> Effective, IReadOnlyList<McpShadowedServer> Shadowed) BuildOverlay(
        IReadOnlyList<McpConfigSource> sources,
        IReadOnlyList<McpConfigSource> external)
    {
        var byKey = new Dictionary<string, McpEffectiveServer>(StringComparer.Ordinal);
        var shadowed = new List<McpShadowedServer>();
        McpConfigScope[] scopes = [McpConfigScope.Project, McpConfigScope.Global];
        foreach (var scope in scopes)
        {
            foreach (var source in sources.Concat(external).Where(source => source.Scope == scope && source.Exists && source.IsValid))
            {
                foreach (var server in source.Servers)
                {
                    if (!byKey.TryGetValue(server.Key, out var winner))
                    {
                        byKey[server.Key] = new McpEffectiveServer { Definition = server };
                        continue;
                    }

                    shadowed.Add(new McpShadowedServer { Definition = server, OverriddenBy = winner.Definition });
                    if (scope == McpConfigScope.Global && winner.Definition.SourceScope == McpConfigScope.Project && winner.ShadowedGlobalDefinition is null)
                    {
                        byKey[server.Key] = winner with { OverridesGlobal = true, ShadowedGlobalDefinition = server };
                    }
                }
            }
        }

        return (
            byKey.Values.OrderBy(static item => item.Definition.Key, StringComparer.Ordinal).ToArray(),
            shadowed.OrderBy(static item => item.Definition.Key, StringComparer.Ordinal).ToArray());
    }

    private static bool CanWriteFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDirectoryWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".codealta-write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool CanCreateDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var parent = Directory.GetParent(directory);
        return parent is not null && Directory.Exists(parent.FullName) && IsDirectoryWritable(parent.FullName);
    }
}
