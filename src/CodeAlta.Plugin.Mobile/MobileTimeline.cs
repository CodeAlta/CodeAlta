using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CodeAlta.Catalog;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// One event of <c>alta session events</c> as the phone reads it: its kind and text, and what the timeline of the desktop
/// tells from its metadata. A prompt the host sent says what it is (its provenance) and the session it comes from; a tool
/// call says what it works on, on one line, and names the call that ran it (a call of a script); an output lists the images
/// it gave the model; a record of a script of Code Mode has its facts; an answer lists the pictures of the disk its Markdown
/// shows. An image has an id, which the server reads from the records of the session itself, never a path. The rest of a
/// tool call is read when the phone opens it (<see cref="MobileApi.ToolAsync"/>).
/// </summary>
internal static partial class MobileTimeline
{
    /// <summary>The longest text of an event the phone gets.</summary>
    internal const int MaximumText = 20_000;

    /// <summary>The most pictures of the disk an answer shows on the phone.</summary>
    internal const int MaximumLinkedImages = 16;

    private const int MaximumSteps = 64;
    private const int MaximumWords = 500;

    /// <summary>The event the phone gets of a record.</summary>
    /// <param name="record">The record of <c>alta session events</c>.</param>
    /// <param name="sequence">Its sequence number.</param>
    /// <param name="kind">Its kind.</param>
    internal static JsonObject Entry(JsonObject record, long sequence, string kind)
    {
        var text = string.Concat((record["content"] as JsonArray ?? []).Select(static part => (string?)part?["text"] ?? ""));
        var metadata = record["metadata"] as JsonObject;
        JsonArray? attachments = null;
        if (kind == "user.message" && metadata?["attachments"] is JsonArray listed && listed.Count > 0) (text, attachments) = MobileSessions.WithoutPaths(text, listed);
        var linked = kind == "assistant.message" ? LinkedImages(text) : [];
        // The output of a tool comes for its images only: its text is read when the phone opens the call.
        var tool = kind.StartsWith("tool.", StringComparison.Ordinal);
        if (tool) text = "";
        if (text.Length > MaximumText) text = text[..MaximumText] + "…";
        var activityId = MobileAlta.Text(metadata, "activityId");
        var entry = new JsonObject
        {
            ["seq"] = sequence, ["at"] = MobileAlta.Text(record, "timestamp"), ["kind"] = kind, ["text"] = text, ["activityId"] = activityId,
        };
        if (attachments is { Count: > 0 }) entry["attachments"] = attachments;
        if (kind == "user.message")
        {
            if (Provenance(MobileAlta.Text(metadata, "provenance")) is { } provenance) entry["provenance"] = provenance;
            if (MobileAlta.Text(metadata, "sourceSessionId") is { } source && MobileApi.IsId(source)) entry["sourceSessionId"] = source;
        }

        if ((kind.StartsWith("activity.", StringComparison.Ordinal) || tool) && MobileAlta.Text(metadata, "parentActivityId") is { } parent && parent != activityId)
            entry["parentId"] = parent;
        if (kind.StartsWith("activity.", StringComparison.Ordinal) && MobileAlta.Text(metadata, "subject") is { } subject) entry["subject"] = subject;
        if (tool && SavedImages(metadata?["images"] as JsonArray) is { Count: > 0 } images) entry["images"] = images;
        if (linked.Count > 0)
        {
            entry["images"] = new JsonArray([.. linked.Take(MaximumLinkedImages).Select(static (image, index) => (JsonNode)new JsonObject
            {
                ["id"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture), ["title"] = image.Alt, ["src"] = image.Target,
            })]);
        }

        if (kind.StartsWith("workflow.", StringComparison.Ordinal) && metadata is not null) entry["workflow"] = Workflow(metadata);
        return entry;
    }

    /// <summary>A provenance as the journal records it: lowercase words separated by hyphens, at most 64 characters; else null.</summary>
    internal static string? Provenance(string? value)
        => value is { Length: <= 64 } && ProvenancePattern().IsMatch(value) ? value : null;

    /// <summary>Whether an id names a saved copy of a session: a file name, without a folder, never hidden.</summary>
    internal static bool IsSavedCopy(string? id)
        => id is { Length: > 0 and <= 200 } && id[0] != '.' && id.IndexOfAny(['/', '\\', ':', '\0']) < 0 && !id.Any(char.IsControl);

    /// <summary>
    /// The pictures of the disk that a Markdown text shows (<c>![alt](path)</c>), in their order, outside blocks and spans of
    /// code: a picture of the web or of a <c>data:</c> address is the page's own business.
    /// </summary>
    internal static IReadOnlyList<(string Alt, string Target)> LinkedImages(string markdown)
    {
        if (!markdown.Contains("![", StringComparison.Ordinal)) return [];
        var found = new List<(string, string)>();
        foreach (var line in LinesOutsideCode(markdown))
        {
            foreach (Match match in ImagePattern().Matches(line))
            {
                var target = match.Groups["angle"].Success ? match.Groups["angle"].Value : match.Groups["bare"].Value;
                // The rule of every link: no address of the web, and nothing of another computer, which the phone would have read
                // with no tap, as it loads the pictures it shows. The target stays as written: reading it again decodes it.
                if (!FileLink.TryParse(target, out _)) continue;
                found.Add((match.Groups["alt"].Value.Trim(), target));
                if (found.Count >= MaximumLinkedImages) return found;
            }
        }

        return found;
    }

    /// <summary>
    /// The targets of the links of a Markdown text that name a file of this computer, as Markdown reads them (escapes undone),
    /// outside blocks and spans of code. An address of the web or of another scheme, and a path of another computer, are none.
    /// </summary>
    internal static IReadOnlyList<string> LinkedFiles(string markdown)
    {
        var found = new List<string>();
        foreach (var line in LinesOutsideCode(markdown))
        {
            foreach (Match match in LinkPattern().Matches(line))
            {
                var target = match.Groups["angle"].Success ? match.Groups["angle"].Value : match.Groups["bare"].Value;
                target = WebUtility.HtmlDecode(MarkdownEscapePattern().Replace(target, "$1"));
                if (FileLink.TryParse(target, out _)) found.Add(target);
            }

            // An address the page makes a link by itself; what ends a sentence is not part of it.
            foreach (Match match in FileAddressPattern().Matches(line))
            {
                var target = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
                if (FileLink.TryParse(target, out _)) found.Add(target);
            }
        }

        return found;
    }

    // The lines of a Markdown text outside blocks of code, with their spans of code blanked.
    private static IEnumerable<string> LinesOutsideCode(string markdown)
    {
        string? fence = null;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (FencePattern().Match(trimmed) is { Success: true } marker)
            {
                if (fence is null) fence = marker.Value;
                else if (marker.Value[0] == fence[0] && marker.Value.Length >= fence.Length && trimmed.Trim() == marker.Value) fence = null;
                continue;
            }

            if (fence is not null || line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t')) continue;
            yield return WithoutCodeSpans(line);
        }
    }

    // The line with its spans of code blanked, so that a picture written as code is not one.
    private static string WithoutCodeSpans(string line)
        => line.Contains('`', StringComparison.Ordinal) ? CodeSpanPattern().Replace(line, static match => new string(' ', match.Length)) : line;

    private static JsonArray? SavedImages(JsonArray? listed)
    {
        if (listed is null) return null;
        var images = new JsonArray();
        foreach (var item in listed.OfType<JsonObject>())
        {
            if (MobileAlta.Text(item, "id") is not { } id || !IsSavedCopy(id)) continue;
            images.Add(new JsonObject { ["title"] = MobileAlta.Text(item, "title") ?? id, ["mediaType"] = MobileAlta.Text(item, "mediaType") ?? "image/*", ["id"] = id });
        }

        return images;
    }

    // The facts of a record of a run of a script, as the timeline of the desktop reads them.
    private static JsonObject Workflow(JsonObject metadata)
    {
        var workflow = new JsonObject();
        foreach (var name in (string[])["runId", "callId", "stepId", "childSessionId", "status", "title", "providerKey", "modelId", "wakeAt"])
            if (MobileAlta.Text(metadata, name) is { } value) workflow[name] = value.Length > MaximumWords ? value[..MaximumWords] + "…" : value;
        if (MobileAlta.Text(metadata, "message") is { } message) workflow["message"] = message.Length > MaximumWords ? message[..MaximumWords] + "…" : message;
        foreach (var name in (string[])["durationMs", "calls", "inputTokens", "outputTokens"])
            if (metadata[name] is JsonValue value && value.TryGetValue<long>(out var number)) workflow[name] = number;
        // A run the host started for the user: its value is for the user, as the desktop shows it.
        if (metadata["forUser"] is JsonValue forUser && forUser.TryGetValue<bool>(out var ofUser) && ofUser) workflow["forUser"] = true;
        if (metadata["steps"] is JsonArray steps)
        {
            workflow["steps"] = new JsonArray([.. steps.OfType<JsonObject>().Take(MaximumSteps)
                .Where(static step => MobileAlta.Text(step, "id") is not null)
                .Select(static step => (JsonNode)new JsonObject { ["id"] = MobileAlta.Text(step, "id"), ["title"] = MobileAlta.Text(step, "title") ?? MobileAlta.Text(step, "id") })]);
        }

        return workflow;
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProvenancePattern();

    [GeneratedRegex("^(`{3,}|~{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex FencePattern();

    [GeneratedRegex("(`+)[^`]*?\\1", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSpanPattern();

    [GeneratedRegex("!\\[(?<alt>[^\\]\\n]*)\\]\\(\\s*(?:<(?<angle>[^>\\n]+)>|(?<bare>[^\\s)]+))(?:\\s+\"[^\"\\n]*\")?\\s*\\)", RegexOptions.CultureInvariant)]
    private static partial Regex ImagePattern();

    // The end of a link: what it says may hold anything, also a picture or code. A target may hold balanced parentheses.
    [GeneratedRegex("\\]\\(\\s*(?:<(?<angle>[^>\\n]+)>|(?<bare>(?:[^\\s()]|\\([^\\s()]*\\))+))(?:\\s+\"[^\"\\n]*\")?\\s*\\)", RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    [GeneratedRegex("\\\\([!-/:-@\\[-`{-~])", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownEscapePattern();

    [GeneratedRegex("file:///[^\\s<>()\\[\\]]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FileAddressPattern();
}
