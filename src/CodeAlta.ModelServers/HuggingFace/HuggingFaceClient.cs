using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeAlta.ModelServers.Downloads;

namespace CodeAlta.ModelServers.HuggingFace;

/// <summary>A model of the Hub, as a list of search results shows it.</summary>
/// <param name="Id">Its repository, <c>org/name</c>.</param>
/// <param name="Author">Its publisher.</param>
/// <param name="Downloads">Its downloads over the last month.</param>
/// <param name="Likes">Its likes.</param>
/// <param name="LastModified">When it was last changed.</param>
/// <param name="PipelineTag">What it does (<c>text-generation</c>, <c>feature-extraction</c>...), or null.</param>
/// <param name="Tags">Its tags.</param>
/// <param name="Gated">Whether Hugging Face asks for a licence before its files.</param>
/// <param name="Trending">Its trending score, or null.</param>
public sealed record HubModel(string Id, string Author, long Downloads, long Likes, DateTimeOffset? LastModified, string? PipelineTag, IReadOnlyList<string> Tags,
    bool Gated, double? Trending);

/// <summary>A file of a repository.</summary>
/// <param name="Path">Its path in the repository.</param>
/// <param name="Size">Its size.</param>
/// <param name="Sha256">Its SHA-256 when it is stored in LFS or Xet, or null.</param>
public sealed record HubFile(string Path, long Size, string? Sha256);

/// <summary>A model of the Hub with what its page shows.</summary>
/// <param name="Model">What a list shows of it.</param>
/// <param name="Revision">The commit of its main branch.</param>
/// <param name="License">Its licence, or null.</param>
/// <param name="BaseModels">The models it derives from.</param>
/// <param name="Parameters">Its number of parameters, from its GGUF metadata, or null.</param>
/// <param name="Architecture">Its architecture, from its GGUF metadata, or null.</param>
/// <param name="ContextLength">The context it was trained for, or null.</param>
/// <param name="Files">Its files.</param>
public sealed record HubModelDetails(HubModel Model, string Revision, string? License, IReadOnlyList<string> BaseModels, long? Parameters, string? Architecture,
    int? ContextLength, IReadOnlyList<HubFile> Files);

/// <summary>
/// The public API of the Hugging Face Hub: search, a model's page, its files with their sizes and SHA-256, its card,
/// and the address of a file. A file's address is resolved by hand (<c>/resolve/</c> answers a redirect to the CDN), so
/// that the token goes to the Hub only, never to the CDN.
/// </summary>
public sealed partial class HuggingFaceClient
{
    /// <summary>The address of the Hub.</summary>
    public const string DefaultEndpoint = "https://huggingface.co";

    private readonly HttpClient _api;
    private readonly HttpClient _noRedirect;
    private readonly string _endpoint;
    private readonly Func<string?> _token;

    /// <summary>Creates a client.</summary>
    /// <param name="api">The client of the API calls (it may follow redirects).</param>
    /// <param name="noRedirect">A client that follows no redirect, to read where a file is.</param>
    /// <param name="token">Gives the token of the Hub, or null; read at each call.</param>
    /// <param name="endpoint">The address of the Hub.</param>
    public HuggingFaceClient(HttpClient api, HttpClient noRedirect, Func<string?> token, string endpoint = DefaultEndpoint)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(noRedirect);
        ArgumentNullException.ThrowIfNull(token);
        _api = api;
        _noRedirect = noRedirect;
        _token = token;
        _endpoint = endpoint.TrimEnd('/');
    }

    /// <summary>Searches the GGUF models of the Hub.</summary>
    /// <param name="search">Words, or null.</param>
    /// <param name="author">A publisher, or null.</param>
    /// <param name="pipeline">What the models do (<c>text-generation</c>, <c>image-text-to-text</c>, <c>feature-extraction</c>, <c>text-ranking</c>), or null.</param>
    /// <param name="sort"><c>trendingScore</c>, <c>downloads</c>, <c>likes</c> or <c>lastModified</c>.</param>
    /// <param name="limit">How many, 1 to 100.</param>
    /// <param name="cancellationToken">Ends the search.</param>
    /// <returns>The models.</returns>
    /// <exception cref="ModelHostException">The Hub could not be read.</exception>
    public async Task<IReadOnlyList<HubModel>> SearchAsync(string? search, string? author, string? pipeline, string sort, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 100);
        // Without a kind, the GGUF files of image, video and speech models come too: more are asked, those llama-server serves kept.
        var asked = pipeline is { Length: > 0 } ? limit : Math.Min(100, limit * 2);
        var query = new List<string> { "filter=gguf", "full=false", "limit=" + asked.ToString(CultureInfo.InvariantCulture) };
        if (search is { Length: > 0 }) query.Add("search=" + Uri.EscapeDataString(search));
        if (author is { Length: > 0 }) query.Add("author=" + Uri.EscapeDataString(author));
        if (pipeline is { Length: > 0 }) query.Add("pipeline_tag=" + Uri.EscapeDataString(pipeline));
        query.Add("sort=" + (sort is "downloads" or "likes" or "lastModified" ? sort : "trendingScore"));
        foreach (var expand in new[] { "downloads", "likes", "lastModified", "pipeline_tag", "tags", "gated", "trendingScore", "author" }) query.Add("expand[]=" + expand);
        using var document = await GetJsonAsync("/api/models?" + string.Join('&', query), cancellationToken).ConfigureAwait(false);
        return document.RootElement.EnumerateArray().Select(ReadModel).Where(ServedByLlamaServer).Take(limit).ToList();
    }

    /// <summary>Whether llama-server serves what a model does: text, text and images, embeddings, reranking; not the images, video or speech it makes.</summary>
    /// <param name="model">The model.</param>
    /// <returns>Whether it does, or may (a model that says nothing).</returns>
    public static bool ServedByLlamaServer(HubModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.PipelineTag is null or "text-generation" or "image-text-to-text" or "feature-extraction" or "sentence-similarity" or "text-ranking"
            or "text-classification" or "any-to-any" or "audio-text-to-text" or "visual-question-answering" or "question-answering" or "summarization" or "translation";
    }

    /// <summary>Reads a model's page and its files.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>Its details.</returns>
    /// <exception cref="ModelHostException">The repository is not there, or the Hub could not be read.</exception>
    public async Task<HubModelDetails> GetModelAsync(string repo, CancellationToken cancellationToken)
    {
        ValidateRepo(repo);
        var expand = string.Join('&', new[] { "downloads", "likes", "lastModified", "pipeline_tag", "tags", "gated", "author", "cardData", "gguf", "sha", "baseModels" }
            .Select(static name => "expand[]=" + name));
        using var document = await GetJsonAsync($"/api/models/{repo}?{expand}", cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var model = ReadModel(root);
        var revision = root.TryGetProperty("sha", out var sha) ? sha.GetString() ?? "main" : "main";
        string? license = null;
        if (root.TryGetProperty("cardData", out var card) && card.ValueKind == JsonValueKind.Object && card.TryGetProperty("license", out var l))
            license = l.ValueKind == JsonValueKind.String ? l.GetString() : null;
        var bases = new List<string>();
        if (root.TryGetProperty("baseModels", out var baseModels) && baseModels.ValueKind == JsonValueKind.Object && baseModels.TryGetProperty("models", out var list))
            bases.AddRange(list.EnumerateArray().Select(static item => item.TryGetProperty("id", out var id) ? id.GetString() : null).OfType<string>());
        long? parameters = null;
        string? architecture = null;
        int? context = null;
        if (root.TryGetProperty("gguf", out var gguf) && gguf.ValueKind == JsonValueKind.Object)
        {
            if (gguf.TryGetProperty("total", out var total) && total.TryGetInt64(out var count)) parameters = count;
            if (gguf.TryGetProperty("architecture", out var arch)) architecture = arch.GetString();
            if (gguf.TryGetProperty("context_length", out var length) && length.TryGetInt32(out var tokens)) context = tokens;
        }

        var files = await ListFilesAsync(repo, revision, cancellationToken).ConfigureAwait(false);
        return new HubModelDetails(model, revision, license, bases, parameters, architecture, context, files);
    }

    /// <summary>Lists the files of a repository at a revision, every folder included.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="revision">The revision.</param>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>Its files.</returns>
    /// <exception cref="ModelHostException">The Hub could not be read.</exception>
    public async Task<IReadOnlyList<HubFile>> ListFilesAsync(string repo, string revision, CancellationToken cancellationToken)
    {
        ValidateRepo(repo);
        var files = new List<HubFile>();
        string? next = $"/api/models/{repo}/tree/{Uri.EscapeDataString(revision)}?recursive=true";
        for (var page = 0; next is not null && page < 50; page++)
        {
            var (document, link) = await GetJsonWithLinkAsync(next, cancellationToken).ConfigureAwait(false);
            using (document)
            {
                foreach (var entry in document.RootElement.EnumerateArray())
                {
                    if (entry.GetProperty("type").GetString() != "file") continue;
                    string? sha256 = null;
                    if (entry.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object && lfs.TryGetProperty("oid", out var oid)) sha256 = oid.GetString();
                    files.Add(new HubFile(entry.GetProperty("path").GetString() ?? "", entry.TryGetProperty("size", out var size) ? size.GetInt64() : -1, sha256));
                }
            }

            next = link;
        }

        return files;
    }

    /// <summary>Reads the model card (the README) of a repository.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="revision">The revision.</param>
    /// <param name="cancellationToken">Ends the reading.</param>
    /// <returns>The Markdown of the card, at most 512 KB, or an empty text when it has none.</returns>
    public async Task<string> GetCardAsync(string repo, string revision, CancellationToken cancellationToken)
    {
        ValidateRepo(repo);
        using var request = Request($"{_endpoint}/{repo}/resolve/{Uri.EscapeDataString(revision)}/README.md");
        using var response = await SendAsync(_api, request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return "";
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return text.Length > 512 * 1024 ? text[..(512 * 1024)] : text;
    }

    /// <summary>Gives where a file is downloaded from now: the CDN address of its redirect, without the token.</summary>
    /// <param name="repo">The repository.</param>
    /// <param name="revision">The revision.</param>
    /// <param name="path">The file.</param>
    /// <param name="cancellationToken">Ends the call.</param>
    /// <returns>The source of its bytes.</returns>
    /// <exception cref="DownloadException">The file is gated, missing, or the Hub could not be reached.</exception>
    public async Task<DownloadSource> ResolveAsync(string repo, string revision, string path, CancellationToken cancellationToken)
    {
        ValidateRepo(repo);
        var address = new Uri($"{_endpoint}/{repo}/resolve/{Uri.EscapeDataString(revision)}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}");
        using var request = new HttpRequestMessage(HttpMethod.Head, address);
        var token = Token();
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await _noRedirect.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new DownloadException("network", $"Hugging Face could not be reached: {exception.Message}");
        }

        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var target = location.IsAbsoluteUri ? location : new Uri(address, location);
                // A redirect within the Hub (a renamed repository) keeps the token; the CDN never gets it.
                return new DownloadSource(target, string.Equals(target.Host, address.Host, StringComparison.OrdinalIgnoreCase) ? token : null);
            }

            var code = response.Headers.TryGetValues("X-Error-Code", out var values) ? values.FirstOrDefault() : null;
            if (code == "GatedRepo" || response.StatusCode == HttpStatusCode.Unauthorized && token is null)
                throw new DownloadException("gated", $"{repo} asks for a licence first: accept it on Hugging Face, and give the machine an HF_TOKEN secret.");
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized) throw new DownloadException("not_found", $"{path} is not in {repo}.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new DownloadException("rate_limited", "Hugging Face limits the calls of this address for now.");
            if (!response.IsSuccessStatusCode) throw new DownloadException("network", $"Hugging Face answered {(int)response.StatusCode}.");
            return new DownloadSource(address, token);
        }
    }

    /// <summary>Checks a repository name: <c>org/name</c> of letters, digits, <c>-</c>, <c>_</c> and <c>.</c>.</summary>
    /// <param name="repo">The name.</param>
    /// <exception cref="ModelHostException">It is not one.</exception>
    public static void ValidateRepo(string repo)
    {
        if (repo is null || !RepoPattern().IsMatch(repo) || repo.Contains("..", StringComparison.Ordinal))
            throw new ModelHostException("invalid", $"'{repo}' is not a Hugging Face repository (org/name).");
    }

    private static HubModel ReadModel(JsonElement model)
    {
        var id = model.GetProperty("id").GetString() ?? "";
        var author = model.TryGetProperty("author", out var a) && a.GetString() is { } name ? name : id.Split('/')[0];
        var gated = model.TryGetProperty("gated", out var g) && (g.ValueKind == JsonValueKind.String || g.ValueKind == JsonValueKind.True);
        DateTimeOffset? modified = model.TryGetProperty("lastModified", out var m) && m.ValueKind == JsonValueKind.String && m.TryGetDateTimeOffset(out var date) ? date : null;
        var tags = model.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(static tag => tag.GetString()).OfType<string>().ToList() : [];
        return new HubModel(id, author, Long(model, "downloads"), Long(model, "likes"), modified,
            model.TryGetProperty("pipeline_tag", out var p) ? p.GetString() : null, tags, gated,
            model.TryGetProperty("trendingScore", out var s) && s.TryGetDouble(out var score) ? score : null);
    }

    private static long Long(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        var (document, _) = await GetJsonWithLinkAsync(path, cancellationToken).ConfigureAwait(false);
        return document;
    }

    private async Task<(JsonDocument Document, string? Next)> GetJsonWithLinkAsync(string path, CancellationToken cancellationToken)
    {
        using var request = Request(path.StartsWith("http", StringComparison.Ordinal) ? path : _endpoint + path);
        using var response = await SendAsync(_api, request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
            throw new ModelHostException("not_found", "Hugging Face has no such repository, or it is private.");
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ModelHostException("rate_limited", "Hugging Face limits the calls of this address for now: wait a few minutes, or give this computer an HF_TOKEN secret.");
        if (!response.IsSuccessStatusCode) throw new ModelHostException("network", $"Hugging Face answered {(int)response.StatusCode}.");
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string? next = null;
        if (response.Headers.TryGetValues("Link", out var links))
        {
            var match = NextLinkPattern().Match(string.Join(',', links));
            if (match.Success) next = match.Groups[1].Value;
        }

        return (JsonDocument.Parse(body), next);
    }

    private HttpRequestMessage Request(string address)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, address);
        if (Token() is { } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private string? Token() => _token() is { Length: > 0 } token && token.StartsWith("hf_", StringComparison.Ordinal) ? token : null;

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new ModelHostException("network", $"Hugging Face could not be reached: {exception.Message}");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,95}/[A-Za-z0-9][A-Za-z0-9_.\-]{0,95}$")]
    private static partial Regex RepoPattern();

    [GeneratedRegex(@"<([^>]+)>;\s*rel=""next""")]
    private static partial Regex NextLinkPattern();
}
