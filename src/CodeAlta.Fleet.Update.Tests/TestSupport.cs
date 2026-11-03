using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace CodeAlta.Fleet.Update.Tests;

/// <summary>A temporary folder, deleted at the end of a test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "codealta-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Makes runtime packages of a tool, as <c>dotnet pack</c> lays them out.</summary>
internal static class Packages
{
    public static readonly UpdateProduct Product = UpdateProduct.Node;

    public const string Runtime = "win-x64";

    /// <summary>Writes a runtime package with the entry assembly, the app host and a file in a sub-folder.</summary>
    public static string Write(string folder, string version, string? runtime = null, IEnumerable<(string Name, string Content)>? extra = null,
        bool withAssembly = true)
    {
        runtime ??= Runtime;
        var path = System.IO.Path.Combine(folder, $"{Product.RuntimePackageId(runtime)}.{version}.nupkg");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(archive, $"{Product.RuntimePackageId(runtime)}.nuspec", "<package/>");
        var prefix = $"tools/net10.0/{runtime}/";
        if (withAssembly) Add(archive, prefix + Product.AssemblyFileName, "assembly " + version);
        Add(archive, prefix + Product.AppHostFileName(windows: true), "host " + version);
        Add(archive, prefix + Product.AppHostFileName(windows: false), "host " + version);
        Add(archive, prefix + "content/user-guide/readme.md", "guide");
        foreach (var (name, content) in extra ?? []) Add(archive, name, content);
        return path;
    }

    public static string Sha512(string file) => Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(file)));

    private static void Add(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}

/// <summary>A NuGet v3 feed served from memory: service index, registrations, catalog and flat container.</summary>
internal sealed class FakeNuGetFeed : HttpMessageHandler
{
    public const string Index = "https://feed.test/v3/index.json";

    private readonly Dictionary<string, (string Version, byte[] Package, bool Listed, string? Hash)> _packages = new(StringComparer.OrdinalIgnoreCase);

    public bool Registrations { get; init; } = true;

    public bool Catalog { get; init; } = true;

    public bool PagedRegistration { get; init; }

    public List<string> Requests { get; } = [];

    public void Add(string packageId, string version, byte[] package, bool listed = true, string? hash = null)
        => _packages[$"{packageId.ToLowerInvariant()}/{version}"] = (version, package, listed, hash ?? Convert.ToBase64String(SHA512.HashData(package)));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(path);
        string? json = null;
        byte[]? bytes = null;
        if (path == "/v3/index.json")
        {
            json = Registrations
                ? """{"version":"3.0.0","resources":[{"@id":"https://feed.test/flat/","@type":"PackageBaseAddress/3.0.0"},{"@id":"https://feed.test/reg","@type":"RegistrationsBaseUrl/3.6.0"}]}"""
                : """{"version":"3.0.0","resources":[{"@id":"https://feed.test/flat/","@type":"PackageBaseAddress/3.0.0"}]}""";
        }
        else if (path.StartsWith("/flat/", StringComparison.Ordinal))
        {
            var parts = path["/flat/".Length..].Split('/');
            var versions = _packages.Where(pair => pair.Key.StartsWith(parts[0] + "/", StringComparison.Ordinal)).Select(static pair => pair.Value).ToArray();
            if (parts.Length == 2 && parts[1] == "index.json" && versions.Length > 0) json = $"{{\"versions\":[{string.Join(',', versions.Select(static v => $"\"{v.Version}\""))}]}}";
            else if (parts.Length == 3 && _packages.TryGetValue($"{parts[0]}/{parts[1]}", out var package)) bytes = package.Package;
        }
        else if (path.StartsWith("/reg/", StringComparison.Ordinal) && Registrations)
        {
            var parts = path["/reg/".Length..].Split('/');
            var versions = _packages.Where(pair => pair.Key.StartsWith(parts[0] + "/", StringComparison.Ordinal)).Select(static pair => pair.Value).ToArray();
            string Leaves() => string.Join(',', versions.Select(v =>
                $"{{\"catalogEntry\":{{\"version\":\"{v.Version}\",\"listed\":{(v.Listed ? "true" : "false")}}}}}"));
            if (parts is [_, "index.json"] && versions.Length > 0)
            {
                json = PagedRegistration
                    ? $"{{\"items\":[{{\"@id\":\"https://feed.test/reg/{parts[0]}/page1.json\",\"count\":{versions.Length}}}]}}"
                    : $"{{\"items\":[{{\"items\":[{Leaves()}]}}]}}";
            }
            else if (parts is [_, "page1.json"]) json = $"{{\"items\":[{Leaves()}]}}";
            else if (parts.Length == 2 && parts[1].EndsWith(".json", StringComparison.Ordinal)
                && _packages.TryGetValue($"{parts[0]}/{parts[1][..^5]}", out var leaf))
            {
                json = Catalog ? $"{{\"catalogEntry\":\"https://feed.test/catalog/{parts[0]}/{leaf.Version}.json\"}}" : "{}";
            }
        }
        else if (path.StartsWith("/catalog/", StringComparison.Ordinal))
        {
            var parts = path["/catalog/".Length..].Split('/');
            if (_packages.TryGetValue($"{parts[0]}/{parts[1][..^5]}", out var entry))
                json = $"{{\"packageHash\":\"{entry.Hash}\",\"packageHashAlgorithm\":\"SHA512\"}}";
        }

        var response = json is not null ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
            : bytes is not null ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
        return Task.FromResult(response);
    }
}

/// <summary>A process of a version that a test ends when it wants.</summary>
internal sealed class FakeProcess(int id, UpdateCommand command) : IUpdateProcess
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Id => id;

    public UpdateCommand Command => command;

    public int StopRequests { get; private set; }

    public bool Killed { get; private set; }

    public bool HonorsStop { get; set; } = true;

    public void Exit(int code) => _exit.TrySetResult(code);

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

    public bool RequestStop()
    {
        StopRequests++;
        if (HonorsStop) Exit(0);
        return true;
    }

    public void Kill()
    {
        Killed = true;
        Exit(137);
    }

    public void Dispose()
    {
    }
}

/// <summary>Starts fake processes and lets a test script each start.</summary>
internal sealed class FakeRunner : IUpdateProcessRunner
{
    private readonly Func<FakeProcess, int, Task> _script;
    private int _count;

    public FakeRunner(Func<FakeProcess, int, Task> script) => _script = script;

    public List<FakeProcess> Started { get; } = [];

    public List<IReadOnlyDictionary<string, string?>> Environments { get; } = [];

    public IUpdateProcess Start(UpdateCommand command, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?> environment)
    {
        var process = new FakeProcess(++_count, command);
        Started.Add(process);
        Environments.Add(environment);
        _ = Task.Run(() => _script(process, _count));
        return process;
    }
}
