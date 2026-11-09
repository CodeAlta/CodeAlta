using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeAlta.ModelServers.LlamaCpp;

/// <summary>
/// The releases of llama.cpp on GitHub. Every merged change gets a build <c>bNNNNN</c> (a pre-release, 6 to 20 a day);
/// every week or two a versioned release (<c>v0.9.0</c>) names one of them in its only file, <c>nightly-tag.txt</c>.
/// GitHub's "latest" release is that pointer and holds no binary, so the builds are listed here, and a build counts only
/// once its archives are uploaded (a release exists before its archives do).
/// </summary>
public sealed partial class LlamaReleases
{
    /// <summary>The repository of the official builds.</summary>
    public const string Repository = "ggml-org/llama.cpp";

    /// <summary>The repository of Lemonade's ROCm builds: one archive per family of AMD GPUs, with its ROCm runtime.</summary>
    public const string LemonadeRepository = "lemonade-sdk/llamacpp-rocm";

    private readonly HttpClient _http;
    private readonly string _api;
    private readonly Dictionary<string, (string ETag, string Body)> _cache = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Creates the client of the GitHub API.</summary>
    /// <param name="http">The HTTP client.</param>
    /// <param name="api">The base address of the API, <c>https://api.github.com</c> by default.</param>
    public LlamaReleases(HttpClient http, string api = "https://api.github.com")
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _api = api.TrimEnd('/');
    }

    /// <summary>Reads the build of the newest versioned release.</summary>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>The build, with the version that names it.</returns>
    /// <exception cref="ModelHostException">GitHub could not be read, or the release names no build.</exception>
    public async Task<LlamaRelease> GetStableAsync(CancellationToken cancellationToken)
    {
        using var latest = await GetJsonAsync($"/repos/{Repository}/releases/latest", cancellationToken).ConfigureAwait(false);
        var version = latest.RootElement.GetProperty("tag_name").GetString() ?? "";
        var pointer = latest.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(static asset => asset.GetProperty("name").GetString() == "nightly-tag.txt");
        if (pointer.ValueKind != JsonValueKind.Object)
            throw new ModelHostException("not_found", $"The release {version} of llama.cpp names no build.");
        var address = pointer.GetProperty("browser_download_url").GetString()!;
        string tag;
        try
        {
            tag = (await _http.GetStringAsync(address, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (HttpRequestException exception)
        {
            throw new ModelHostException("network", $"GitHub did not give the build of {version}: {exception.Message}");
        }

        if (!BuildTagPattern().IsMatch(tag)) throw new ModelHostException("not_found", $"The release {version} names '{tag}', which is not a build.");
        return await GetBuildAsync(tag, cancellationToken).ConfigureAwait(false) with { Version = version };
    }

    /// <summary>Reads one build.</summary>
    /// <param name="tag">Its tag, <c>b12110</c>.</param>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>The build.</returns>
    /// <exception cref="ModelHostException">GitHub could not be read, or the build is not there.</exception>
    public async Task<LlamaRelease> GetBuildAsync(string tag, CancellationToken cancellationToken)
    {
        if (!BuildTagPattern().IsMatch(tag)) throw new ModelHostException("invalid", $"'{tag}' is not a build of llama.cpp (bNNNNN).");
        using var release = await GetJsonAsync($"/repos/{Repository}/releases/tags/{tag}", cancellationToken).ConfigureAwait(false);
        return ReadRelease(release.RootElement) ?? throw new ModelHostException("not_found", $"The build {tag} of llama.cpp has no archive yet.");
    }

    /// <summary>Lists the newest builds whose archives are all uploaded.</summary>
    /// <param name="count">How many to read at most (up to 100).</param>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>The builds, newest first.</returns>
    /// <exception cref="ModelHostException">GitHub could not be read.</exception>
    public async Task<IReadOnlyList<LlamaRelease>> ListBuildsAsync(int count, CancellationToken cancellationToken)
    {
        using var list = await GetJsonAsync($"/repos/{Repository}/releases?per_page={Math.Clamp(count, 1, 100)}", cancellationToken).ConfigureAwait(false);
        return list.RootElement.EnumerateArray().Select(ReadRelease).OfType<LlamaRelease>().OrderByDescending(static release => release.Build).ToList();
    }

    /// <summary>
    /// Reads the newest of Lemonade's ROCm builds. They bring their ROCm runtime, so they run where the official ROCm build
    /// cannot: Windows without the HIP SDK, a Linux older than the official archives need, GPUs they have no code for.
    /// </summary>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>The build, its tag <c>lemonade-bNNNN</c> (Lemonade numbers its builds itself).</returns>
    /// <exception cref="ModelHostException">GitHub could not be read, or the release has no archive.</exception>
    public async Task<LlamaRelease> GetLemonadeAsync(CancellationToken cancellationToken)
    {
        using var latest = await GetJsonAsync($"/repos/{LemonadeRepository}/releases/latest", cancellationToken).ConfigureAwait(false);
        return ReadLemonadeRelease(latest.RootElement) ?? throw new ModelHostException("not_found", "The newest release of Lemonade's ROCm builds has no archive yet.");
    }

    /// <summary>Reads a release of Lemonade's ROCm builds; null for one whose archives are not all there.</summary>
    /// <param name="release">The release.</param>
    /// <returns>The build, or null.</returns>
    internal static LlamaRelease? ReadLemonadeRelease(JsonElement release)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!BuildTagPattern().IsMatch(tag)) return null;
        var assets = new List<LlamaAsset>();
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.TryGetProperty("state", out var state) && state.GetString() is { } value && value != "uploaded") return null;
            var match = LemonadeAssetPattern().Match(asset.GetProperty("name").GetString() ?? "");
            if (!match.Success) continue;
            var digest = asset.TryGetProperty("digest", out var d) && d.GetString() is { } text && text.StartsWith("sha256:", StringComparison.Ordinal) ? text[7..] : null;
            assets.Add(new LlamaAsset(match.Value, asset.GetProperty("browser_download_url").GetString() ?? "", asset.GetProperty("size").GetInt64(), digest,
                match.Groups["os"].Value == "windows" ? "windows" : "linux", "rocm", match.Groups["target"].Value, match.Groups["arch"].Value, false));
        }

        if (assets.Count == 0) return null;
        var published = release.TryGetProperty("published_at", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var date) ? date : DateTimeOffset.MinValue;
        return new LlamaRelease("lemonade-" + tag, int.Parse(tag.AsSpan(1), CultureInfo.InvariantCulture), published, null, assets, "lemonade");
    }

    /// <summary>Whether a Lemonade archive has the code of a GPU: its gfx, or its family (<c>gfx120X</c> for gfx1200 and gfx1201).</summary>
    /// <param name="target">The target of the archive.</param>
    /// <param name="gfx">The gfx of the GPU.</param>
    /// <returns>Whether it does.</returns>
    public static bool LemonadeCovers(string target, string gfx)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(gfx);
        return target.EndsWith('X')
            ? gfx.Length == target.Length && gfx.StartsWith(target[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(target, gfx, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reads a release of the GitHub API; null for one that is not a build or whose archives are not all there.</summary>
    /// <param name="release">The release.</param>
    /// <returns>The build, or null.</returns>
    internal static LlamaRelease? ReadRelease(JsonElement release)
    {
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!BuildTagPattern().IsMatch(tag)) return null;
        var assets = new List<LlamaAsset>();
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            // An archive still uploading is "starting" or "new": the build is not complete.
            if (asset.TryGetProperty("state", out var state) && state.GetString() is { } value && value != "uploaded") return null;
            var name = asset.GetProperty("name").GetString() ?? "";
            var digest = asset.TryGetProperty("digest", out var d) && d.GetString() is { } text && text.StartsWith("sha256:", StringComparison.Ordinal) ? text[7..] : null;
            if (ParseAsset(name, asset.GetProperty("browser_download_url").GetString() ?? "", asset.GetProperty("size").GetInt64(), digest) is { } parsed) assets.Add(parsed);
        }

        if (assets.Count == 0) return null;
        var published = release.TryGetProperty("published_at", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var date) ? date : DateTimeOffset.MinValue;
        return new LlamaRelease(tag, int.Parse(tag.AsSpan(1), CultureInfo.InvariantCulture), published, null, assets);
    }

    /// <summary>Reads the name of an archive: its system, backend, version of the backend and architecture.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="url">Its address.</param>
    /// <param name="size">Its size.</param>
    /// <param name="sha256">Its SHA-256, or null.</param>
    /// <returns>The archive, or null for one that is no build for Windows, Linux or macOS (Android, UI, frameworks).</returns>
    public static LlamaAsset? ParseAsset(string name, string url, long size, string? sha256)
    {
        var runtime = CudaRuntimePattern().Match(name);
        if (runtime.Success)
            return new LlamaAsset(name, url, size, sha256, OsOf(runtime.Groups["os"].Value), "cuda", runtime.Groups["version"].Value, runtime.Groups["arch"].Value, true);
        var match = AssetPattern().Match(name);
        if (!match.Success) return null;
        var os = OsOf(match.Groups["os"].Value);
        var arch = match.Groups["arch"].Value;
        var rest = match.Groups["rest"].Success ? match.Groups["rest"].Value : "";
        string backend;
        string? version = null;
        if (rest.Length == 0) backend = os == "macos" && arch == "arm64" ? "metal" : "cpu";
        else
        {
            var parts = rest.Split('-', 2);
            backend = parts[0];
            if (parts.Length == 2)
            {
                if (char.IsAsciiDigit(parts[1][0])) version = parts[1];
                else backend = rest; // opencl-adreno, sycl-fp16
            }
        }

        // An Ubuntu build for another distribution version (26.04-s390x) is not one of the machines of a fleet.
        if (char.IsAsciiDigit(backend[0])) return null;
        return new LlamaAsset(name, url, size, sha256, os, backend, version, arch, false);
    }

    private static string OsOf(string os) => os switch { "win" => "windows", "ubuntu" => "linux", _ => "macos" };

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _api + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodeAlta", "1.0"));
        (string ETag, string Body) cached;
        lock (_gate)
        {
            _cache.TryGetValue(path, out cached);
        }

        // Without a token GitHub allows 60 calls an hour; an answer that did not change does not count.
        if (cached.ETag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", cached.ETag);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new ModelHostException("network", $"GitHub could not be reached: {exception.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotModified && cached.Body is not null) return JsonDocument.Parse(cached.Body);
            if (response.StatusCode == HttpStatusCode.NotFound) throw new ModelHostException("not_found", "GitHub does not have this release of llama.cpp.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new ModelHostException("rate_limited", "GitHub limits the calls of this address for now: try again within the hour.");
            if (!response.IsSuccessStatusCode) throw new ModelHostException("network", $"GitHub answered {(int)response.StatusCode}.");
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.Headers.ETag?.Tag is { } etag)
            {
                lock (_gate)
                {
                    _cache[path] = (etag, body);
                }
            }

            return JsonDocument.Parse(body);
        }
    }

    [GeneratedRegex(@"^b\d+$")]
    private static partial Regex BuildTagPattern();

    [GeneratedRegex(@"^llama-b\d+-bin-(?<os>win|ubuntu|macos)(?:-(?<rest>[a-z0-9.\-]+?))?-(?<arch>x64|arm64)\.(?:zip|tar\.gz)$")]
    private static partial Regex AssetPattern();

    [GeneratedRegex(@"^llama-b\d+-(?<os>windows|ubuntu)-rocm-(?<target>gfx[0-9a-fA-FX]+)-(?<arch>x64|arm64)\.zip$")]
    private static partial Regex LemonadeAssetPattern();

    [GeneratedRegex(@"^cudart-llama-(?:b\d+-)?bin-(?<os>win|ubuntu)-cuda-(?<version>[\d.]+)-(?<arch>x64|arm64)\.(?:zip|tar\.gz)$")]
    private static partial Regex CudaRuntimePattern();
}
