using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using XenoAtom.Logging;

namespace CodeAlta.Plugin.Git;

/// <summary>
/// What the issue trackers of the hosting services write to the log of the application: the requests, and for one
/// that fails, its address, its status and what the service said, so that a failure the Issues tab shows can be
/// understood from the log. Never a credential: the requests carry them in a header that is not written.
/// </summary>
internal static partial class GitIssueLog
{
    /// <summary>The name of the logger, of the Issue trackers feature of the log.</summary>
    public const string LoggerName = "CodeAlta.Issues.Git";

    /// <summary>
    /// Gets the logger; null before the log of the application starts, as in a test: XenoAtom.Logging throws for a
    /// message with values before then, and a failure to log must never be a failure of the tracker.
    /// </summary>
    public static Logger? Log => LogManager.IsInitialized ? LogManager.GetLogger(LoggerName) : null;

    private const int MaximumAddress = 400;
    private const int MaximumMessage = 400;
    private const int MaximumBody = 256 * 1024;

    /// <summary>Gets the address of a request as the log writes it: without user information, bounded.</summary>
    public static string Address(Uri? address)
    {
        if (address is null) return "(no address)";
        var text = address.IsAbsoluteUri && address.UserInfo.Length > 0
            ? new UriBuilder(address) { UserName = string.Empty, Password = string.Empty }.Uri.ToString()
            : address.ToString();
        return Bound(text, MaximumAddress);
    }

    /// <summary>
    /// Reads what a service said of a failure: the message of an error in JSON, the title of an HTML page (a sign-in
    /// page), or the start of a text. The body stays readable by the caller.
    /// </summary>
    public static async Task<string?> ProviderMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await response.Content.LoadIntoBufferAsync(MaximumBody, cancellationToken).ConfigureAwait(false);
            return Message(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), response.Content.Headers.ContentType);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Gets what a body says of a failure; null when it says nothing that can be read.</summary>
    public static string? Message(string? body, MediaTypeHeaderValue? type)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var media = type?.MediaType ?? string.Empty;
        var trimmed = body.TrimStart();
        if (media.Contains("json", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (JsonMessage(document.RootElement) is { } said) return Bound(said, MaximumMessage);
            }
            catch (JsonException)
            {
            }
        }

        if (media.Contains("html", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith('<'))
        {
            return TitlePattern().Match(body) is { Success: true } title && Bound(title.Groups[1].Value, MaximumMessage) is { Length: > 0 } text
                ? $"an HTML page titled \"{text}\""
                : "an HTML page";
        }

        return Bound(body, MaximumMessage) is { Length: > 0 } start ? start : null;
    }

    /// <summary>Gets a text on one line, its spaces collapsed, cut at a length.</summary>
    public static string Bound(string text, int maximum)
    {
        var builder = new StringBuilder(Math.Min(text.Length, maximum + 1));
        var space = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space) builder.Append(' ');
            space = false;
            builder.Append(character);
            if (builder.Length >= maximum) return builder.Append('…').ToString();
        }

        return builder.ToString();
    }

    // GitHub, GitLab and Azure DevOps write "message"; OAuth errors "error_description"; Bitbucket {"error":{"message"}}.
    private static string? JsonMessage(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in (ReadOnlySpan<string>)["message", "error_description", "errorMessage", "error"])
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text) return text;
            if (value.ValueKind == JsonValueKind.Object && JsonMessage(value) is { } inner) return inner;
        }

        return null;
    }

    [GeneratedRegex(@"<title[^>]*>([^<]*)</title>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TitlePattern();
}
