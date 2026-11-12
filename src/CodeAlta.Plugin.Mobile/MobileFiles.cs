using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using CodeAlta.Catalog;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Mobile;

/// <summary>
/// The files that an answer of a session, or a prompt the user sent to it, links, which the phone reads a page at a time. The
/// phone names a link of such a message, never a path: the server finds that link in the message itself, and reads its target
/// as the desktop reads it (<see cref="FileLink"/>): from the folder the session works in for a relative path, and never a path
/// of another computer, which looking at would send the credentials of the user to that computer. A prompt that came from
/// elsewhere (another session, a job, a reminder) links no file: nothing the user wrote or an agent answered vouches for it.
/// </summary>
internal sealed class MobileFiles(IPluginAltaService alta, MobileSessions sessions)
{
    /// <summary>The largest file read. A page needs the file decoded up to it, so each read costs at most this.</summary>
    internal const int MaximumBytes = 16 * 1024 * 1024;

    /// <summary>What the phone reads of a file at a time, as of the output of a tool.</summary>
    internal const int PageCharacters = MobileApi.ToolOutputPage;

    // What is read of a file to tell text from anything else, as the desktop does.
    private const int SniffedBytes = 8192;

    /// <summary>
    /// A page of a file that a message links: the page from a character, the page before one, or else the start of the file or
    /// the page around the lines the link names, with where these lines are in the file for the phone to mark them.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="sequence">The sequence number of the message.</param>
    /// <param name="link">The target of the link, as the page has it.</param>
    /// <param name="offset">The character the page starts from, when it follows a page the phone has.</param>
    /// <param name="before">The character the page ends before, when it comes before a page the phone has.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The page, or what prevents reading it; null when the phone does not see the session or asks for nothing.</returns>
    internal async Task<JsonObject?> ReadAsync(string sessionId, long? sequence, string? link, int? offset, int? before, CancellationToken cancellationToken)
    {
        if (sequence is not > 0 || link is not { Length: > 0 and <= FileLink.MaximumLength } || offset < 0 || before <= 0) return null;
        if (await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false) is not { } status) return null;
        var message = await LinkingTextAsync(sessionId, sequence.Value, cancellationToken).ConfigureAwait(false);
        var wanted = Comparable(link);
        if (message is null || MobileTimeline.LinkedFiles(message).FirstOrDefault(target => Comparable(target) == wanted) is not { } found || !FileLink.TryParse(found, out var file))
            return Refused("This message has no such link to a file of this computer.");
        var folder = MobileAlta.Text(status, "worktreeDirectory") ?? MobileAlta.Text(status, "workingDirectory");
        if (file.Locate(folder, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is not { } full) return Refused("This file is not there.");

        var (text, failure) = await ReadTextAsync(full, cancellationToken).ConfigureAwait(false);
        if (text is null) return Refused(failure!);
        var marked = offset is null && before is null && file.Line is { } number ? Lines(text, number, file.EndLine ?? number) : null;
        var start = before is { } end ? Math.Max(0, Math.Min(end, text.Length) - PageCharacters) : offset ?? (marked is { } lines ? PageAround(text, lines.Start) : 0);
        start = Math.Min(start, text.Length);
        var stop = before is { } last ? Math.Min(last, text.Length) : Math.Min(text.Length, start + PageCharacters);
        // A character of two parts stays whole, in the page that holds its first part.
        if (start > 0 && start < text.Length && char.IsLowSurrogate(text[start])) start--;
        if (stop < text.Length && stop > start && char.IsHighSurrogate(text[stop - 1]) && before is null) stop--;
        var page = new JsonObject
        {
            ["error"] = null, ["path"] = full, ["output"] = text[start..stop], ["outputOffset"] = start, ["outputLength"] = text.Length,
        };
        if (marked is { } mark)
        {
            page["markStart"] = mark.Start;
            page["markEnd"] = mark.End;
        }

        return page;
    }

    /// <summary>
    /// A data file that a chart of a message names (<see cref="ChartDataFiles"/>), for the phone to draw the chart: the server finds
    /// the chart that names the path in the message itself, and reads the file below the folder the session works in, as the
    /// desktop does.
    /// </summary>
    /// <param name="sessionId">The session.</param>
    /// <param name="sequence">The sequence number of the message.</param>
    /// <param name="path">The path of the file, as the phone read it in the chart.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The status and, for <c>ok</c>, the text of the file; null when the phone does not see the session or asks for nothing.</returns>
    internal async Task<JsonObject?> ReadChartDataAsync(string sessionId, long? sequence, string? path, CancellationToken cancellationToken)
    {
        if (sequence is not > 0 || path is not { Length: > 0 }) return null;
        var parsed = ChartDataFiles.Parse(path, out var parts);
        if (parsed != "ok") return new JsonObject { ["status"] = parsed };
        if (await sessions.FindAsync(sessionId, cancellationToken).ConfigureAwait(false) is not { } status) return null;
        var wanted = string.Join('/', parts);
        var message = await LinkingTextAsync(sessionId, sequence.Value, cancellationToken).ConfigureAwait(false);
        var named = ChartDataFiles.NamedIn(message).Any(url => ChartDataFiles.Parse(url, out var written) == "ok" && string.Join('/', written) == wanted);
        if (!named) return new JsonObject { ["status"] = "not_named" };
        var folder = MobileAlta.Text(status, "worktreeDirectory") ?? MobileAlta.Text(status, "workingDirectory");
        var read = await ChartDataFiles.ReadAsync(folder, null, wanted, cancellationToken).ConfigureAwait(false);
        return new JsonObject { ["status"] = read.Status, ["text"] = read.Text };
    }

    // The text of a message whose links may be read, as the phone shows it: an answer or a prompt the user sent; null for anything else.
    private async Task<string?> LinkingTextAsync(string sessionId, long sequence, CancellationToken cancellationToken)
    {
        var read = await MobileAlta.RunAsync(alta, ["session", "events", sessionId, "--since", (sequence - 1).ToString(CultureInfo.InvariantCulture), "--limit", "1",
            "--kind", "assistant.message,user.message", "--fields", "sequenceNumber,kind,content,metadata"], null, cancellationToken).ConfigureAwait(false);
        var message = read.OfType("alta.session.event").FirstOrDefault(record => record["sequenceNumber"] is { } number && (long)number == sequence);
        if (message is null) return null;
        var text = string.Concat((message["content"] as JsonArray ?? []).Select(static part => (string?)part?["text"] ?? ""));
        var kind = MobileAlta.Text(message, "kind");
        if (kind == "assistant.message") return text;
        var metadata = message["metadata"] as JsonObject;
        if (kind != "user.message" || MobileAlta.Text(metadata, "provenance") is not null || MobileAlta.Text(metadata, "sourceSessionId") is not null) return null;
        return metadata?["attachments"] is JsonArray { Count: > 0 } listed ? MobileSessions.WithoutPaths(text, listed).Text : text;
    }

    // A file as text, or why it cannot be read.
    private static async Task<(string? Text, string? Failure)> ReadTextAsync(string full, CancellationToken cancellationToken)
    {
        try
        {
            if (Directory.Exists(full)) return (null, "This link names a folder, not a file.");
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            if (stream.Length > MaximumBytes) return (null, $"This file is larger than {MaximumBytes / (1024 * 1024)} MB, too large to read on the phone.");
            // One byte more than the length shows a file that grew past the maximum while it was read.
            var buffer = new byte[(int)stream.Length + 1];
            var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (count > MaximumBytes) return (null, $"This file is larger than {MaximumBytes / (1024 * 1024)} MB, too large to read on the phone.");
            if (IsBinary(buffer.AsSpan(0, count))) return (null, "This file is not text, so the phone cannot show it.");
            using var reader = new StreamReader(new MemoryStream(buffer, 0, count), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return (null, "This file is not there.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            return (null, "This file could not be read.");
        }
    }

    // Text has no NUL; the encodings that have one start with a byte order mark.
    private static bool IsBinary(ReadOnlySpan<byte> bytes)
    {
        if (bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] or [0x00, 0x00, 0xFE, 0xFF, ..]) return false;
        return bytes[..Math.Min(bytes.Length, SniffedBytes)].Contains((byte)0);
    }

    // Where some lines of a text start and end, with the line break of the last one; null for a first line it does not have, such as after the file changed.
    private static (int Start, int End)? Lines(string text, int first, int last)
    {
        var start = 0;
        for (var current = 1; current < first; current++)
        {
            var next = text.IndexOf('\n', start);
            if (next < 0) return null;
            start = next + 1;
        }

        if (start >= text.Length) return null;
        var end = start;
        for (var current = first; current <= last && end < text.Length; current++)
        {
            var next = text.IndexOf('\n', end);
            end = next < 0 ? text.Length : next + 1;
        }

        return (start, end);
    }

    // Where the page around a place of a text starts: half a page before it, or a whole page before the end of the text, and on the start of a line.
    private static int PageAround(string text, int place)
    {
        var start = Math.Clamp(place - PageCharacters / 2, 0, Math.Max(0, text.Length - PageCharacters));
        var line = start > 0 ? text.IndexOf('\n', start, place - start) : -1;
        return line < 0 ? start : line + 1;
    }

    // A target as links are compared: decoded, with the separators of either system alike. The page encodes what it draws.
    private static string Comparable(string target) => Uri.UnescapeDataString(target.Trim()).Replace('\\', '/');

    private static JsonObject Refused(string reason) => new() { ["error"] = reason };
}
