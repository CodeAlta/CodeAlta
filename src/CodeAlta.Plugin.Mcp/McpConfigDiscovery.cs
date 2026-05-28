using System.Text.Json;

namespace CodeAlta.Plugin.Mcp;

internal sealed class McpConfigDiscovery
{
    public McpConfigSnapshot Discover(McpConfigPathOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var globalPath = GetGlobalConfigPath(options.UserHomeDirectory);
        var projectPath = string.IsNullOrWhiteSpace(options.ProjectDirectory)
            ? null
            : Path.Combine(Path.GetFullPath(options.ProjectDirectory), ".alta", "mcp.json");

        var sources = new List<McpConfigSource>(2)
        {
            ReadSource(McpConfigScope.Global, globalPath),
        };
        if (projectPath is not null)
        {
            sources.Add(ReadSource(McpConfigScope.Project, projectPath));
        }

        var (effective, shadowed) = BuildOverlay(sources);
        return new McpConfigSnapshot
        {
            Sources = sources,
            EffectiveServers = effective,
            ShadowedGlobalServers = shadowed,
            DefaultWriteScope = projectPath is null ? McpConfigScope.Global : McpConfigScope.Project,
        };
    }

    public static string GetGlobalConfigPath(string? userHomeDirectory = null)
    {
        var home = string.IsNullOrWhiteSpace(userHomeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userHomeDirectory;
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME") ?? Environment.CurrentDirectory;
        }

        return Path.Combine(home, ".alta", "mcp.json");
    }

    public static string GetProjectConfigPath(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        return Path.Combine(Path.GetFullPath(projectDirectory), ".alta", "mcp.json");
    }

    private static McpConfigSource ReadSource(McpConfigScope scope, string path)
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
                IsWritable = directoryExists ? IsDirectoryWritable(directory!) : CanCreateDirectory(directory),
            };
        }

        try
        {
            var document = McpConfigFormatAdapter.ParseDocument(File.ReadAllText(path));
            var servers = McpConfigFormatAdapter.ReadServers(document, scope, path);
            return new McpConfigSource
            {
                Scope = scope,
                Path = path,
                Exists = true,
                DirectoryExists = directoryExists,
                IsWritable = CanWriteFile(path),
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
                IsWritable = CanWriteFile(path),
                IsValid = false,
                Diagnostic = ex.Message,
            };
        }
    }

    private static (IReadOnlyList<McpEffectiveServer> Effective, IReadOnlyList<McpServerDefinition> Shadowed) BuildOverlay(IReadOnlyList<McpConfigSource> sources)
    {
        var byKey = new Dictionary<string, McpEffectiveServer>(StringComparer.Ordinal);
        var shadowed = new List<McpServerDefinition>();
        foreach (var source in sources.Where(static source => source.Exists && source.IsValid))
        {
            foreach (var server in source.Servers)
            {
                if (server.SourceScope == McpConfigScope.Project && byKey.TryGetValue(server.Key, out var existing) && existing.Definition.SourceScope == McpConfigScope.Global)
                {
                    shadowed.Add(existing.Definition);
                    byKey[server.Key] = new McpEffectiveServer
                    {
                        Definition = server,
                        OverridesGlobal = true,
                        ShadowedGlobalDefinition = existing.Definition,
                    };
                    continue;
                }

                byKey[server.Key] = new McpEffectiveServer { Definition = server };
            }
        }

        return (byKey.Values.OrderBy(static item => item.Definition.Key, StringComparer.Ordinal).ToArray(), shadowed.OrderBy(static item => item.Key, StringComparer.Ordinal).ToArray());
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
