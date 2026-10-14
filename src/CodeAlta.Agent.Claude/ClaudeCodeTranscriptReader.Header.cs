using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.Claude;

// What the head and the tail of a transcript say of its session, read as the session list of VS Code reads them.
internal static partial class ClaudeCodeTranscriptReader
{
    private const int HeaderChunkBytes = 64 * 1024;
    private const int MaxTitleLength = 200;
    private const string ParentUuidMarker = "\"parentUuid\":";
    private const string ContinuedInMarker = "\"type\":\"continued-in\"";

    /// <summary>
    /// Reads what discovery needs of a transcript from its first and last 64 KB only.
    /// </summary>
    /// <param name="transcriptPath">The path of the <c>&lt;id&gt;.jsonl</c> file.</param>
    /// <returns>The header, or null when the file is not a session (its name is not a uuid), is empty or cannot be read.</returns>
    /// <exception cref="ArgumentException"><paramref name="transcriptPath"/> is empty.</exception>
    public static ClaudeCodeTranscriptHeader? ReadHeader(string transcriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcriptPath);
        var sessionId = Path.GetFileNameWithoutExtension(transcriptPath);
        if (!string.Equals(Path.GetExtension(transcriptPath), ".jsonl", StringComparison.OrdinalIgnoreCase) ||
            !IsUuid(sessionId))
        {
            return null;
        }

        string head;
        string tail;
        long length;
        DateTime lastWriteTime;
        try
        {
            using var stream = OpenShared(transcriptPath);
            length = stream.Length;
            if (length == 0)
            {
                return null;
            }

            lastWriteTime = File.GetLastWriteTimeUtc(transcriptPath);
            head = Encoding.UTF8.GetString(ReadRange(stream, 0, Math.Min(length, HeaderChunkBytes)));
            tail = length > HeaderChunkBytes ? Encoding.UTF8.GetString(ReadRange(stream, length - HeaderChunkBytes, length)) : head;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var newline = head.IndexOf('\n');
        var firstLine = newline >= 0 ? head[..newline] : head;
        var entrypoint = FirstValue(head, "entrypoint") ?? LastValue(tail, "entrypoint");
        var kindLine = head.Split('\n').FirstOrDefault(static line => line.Contains(ParentUuidMarker, StringComparison.Ordinal)) ?? head;
        var folder = Path.GetDirectoryName(transcriptPath) ?? string.Empty;
        var continuedIn = ReadContinuedIn(tail);
        var title = LastValue(tail, "customTitle") ??
                    ReadCustomTitleFile(folder, sessionId) ??
                    LastValue(head, "customTitle") ??
                    LastValue(tail, "aiTitle") ??
                    LastValue(head, "aiTitle") ??
                    LastValue(tail, "lastPrompt") ??
                    LastValue(tail, "summary") ??
                    ReadFirstPrompt(head);
        return new ClaudeCodeTranscriptHeader(
            sessionId,
            FirstValue(head, "cwd"),
            entrypoint,
            ToOrigin(entrypoint),
            FirstValue(kindLine, "sessionKind"),
            firstLine.Contains("\"isSidechain\":true", StringComparison.Ordinal) || firstLine.Contains("\"isSidechain\": true", StringComparison.Ordinal),
            continuedIn,
            continuedIn is not null && HasEntries(Path.Combine(folder, continuedIn + ".jsonl")),
            title,
            lastWriteTime,
            length);
    }

    private static bool IsUuid(string value) => value.Length == 36 && Guid.TryParseExact(value, "D", out _);

    // The title the user gave in VS Code, which it keeps beside the transcript.
    private static string? ReadCustomTitleFile(string folder, string sessionId)
    {
        try
        {
            var path = Path.Combine(folder, sessionId, "custom-title.json");
            if (!File.Exists(path) || ReadSmallFile(path, HeaderChunkBytes) is not { } bytes)
            {
                return null;
            }

            using var document = JsonDocument.Parse(bytes);
            if (ClaudeCodeJson.GetString(document.RootElement, "customTitle") is not { } title)
            {
                return null;
            }

            var cleaned = new string([.. title.Trim().Where(static character => !char.IsControl(character))]);
            return NonEmpty(cleaned.Length > MaxTitleLength ? cleaned[..MaxTitleLength].Trim() : cleaned);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // The first prompt of the head that reads as one, else the command it ran, else whether it was an image or a document.
    private static string? ReadFirstPrompt(string head)
    {
        string? commandFallback = null;
        string? mediaTitle = null;
        foreach (var line in head.Split('\n'))
        {
            if (!IsUserLine(line) || line.Contains("\"tool_result\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line, JsonOptions);
                if (ReadPromptTitle(document.RootElement, ref commandFallback) is { } title)
                {
                    return title;
                }

                mediaTitle ??= ReadMediaTitle(document.RootElement);
            }
            catch (JsonException)
            {
                // The line the head cuts, or a bad one.
            }
        }

        return NonEmpty(commandFallback) ?? mediaTitle;
    }

    private static bool IsUserLine(string line)
        => line.Contains("\"type\":\"user\"", StringComparison.Ordinal) || line.Contains("\"type\": \"user\"", StringComparison.Ordinal);

    // The title a prompt gives the session: its first text that is not a command, a tag or a stop, on one line.
    private static string? ReadPromptTitle(JsonElement root, ref string? commandFallback)
    {
        if (ClaudeCodeJson.GetString(root, "type") != "user" ||
            ClaudeCodeJson.GetBoolean(root, "isMeta") ||
            ClaudeCodeJson.GetBoolean(root, "isCompactSummary") ||
            !ClaudeCodeJson.TryGetObject(root, "message", out var message) ||
            !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        var texts = new List<string>();
        if (content.ValueKind == JsonValueKind.String)
        {
            texts.Add(content.GetString() ?? string.Empty);
        }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                switch (ClaudeCodeJson.GetString(block, "type"))
                {
                    case "tool_result":
                        return null;
                    case "text" when ClaudeCodeJson.GetString(block, "text") is { } text:
                        texts.Add(text);
                        break;
                }
            }
        }

        foreach (var text in texts)
        {
            var line = text.Replace('\n', ' ').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (Between(line, "<command-name>", "</command-name>") is { } command)
            {
                commandFallback ??= command;
                continue;
            }

            if (Between(line, "<bash-input>", "</bash-input>") is { } bash)
            {
                return $"! {bash.Trim()}";
            }

            if (StartsWithTag(line) || (line.StartsWith("[Request interrupted by user", StringComparison.Ordinal) && line.Contains(']')))
            {
                continue;
            }

            if (line.Length > MaxTitleLength)
            {
                var cut = char.IsHighSurrogate(line[MaxTitleLength - 1]) ? MaxTitleLength - 1 : MaxTitleLength;
                line = line[..cut].Trim() + "…";
            }

            return line;
        }

        return null;
    }

    private static string? ReadMediaTitle(JsonElement root)
    {
        if (ClaudeCodeJson.GetString(root, "type") != "user" ||
            ClaudeCodeJson.GetBoolean(root, "isMeta") ||
            !ClaudeCodeJson.TryGetObject(root, "message", out var message) ||
            !ClaudeCodeJson.TryGetArray(message, "content", out var content))
        {
            return null;
        }

        string? title = null;
        foreach (var block in content.EnumerateArray())
        {
            switch (ClaudeCodeJson.GetString(block, "type"))
            {
                case "tool_result":
                    return null;
                case "image":
                    title ??= "Image";
                    break;
                case "document":
                    title ??= "Document";
                    break;
            }
        }

        return title;
    }

    private static string? Between(string text, string open, string close)
    {
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += open.Length;
        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end];
    }

    // A text that starts with a tag (`<ide_opened_file>`, `<local-command-stdout>`, ...) is not what the user wrote.
    private static bool StartsWithTag(string text)
    {
        if (text.Length < 3 || text[0] != '<' || text[1] is < 'a' or > 'z')
        {
            return false;
        }

        for (var index = 2; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '>' || char.IsWhiteSpace(character))
            {
                return true;
            }

            if (!char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-'))
            {
                return false;
            }
        }

        return false;
    }

    // The session this one continues in: the last `continued-in` record, unless a message follows it.
    private static string? ReadContinuedIn(string tail)
    {
        if (!tail.Contains(ContinuedInMarker, StringComparison.Ordinal))
        {
            return null;
        }

        var end = tail.Length;
        while (end > 0)
        {
            var newline = tail.LastIndexOf('\n', end - 1);
            var line = tail[(newline + 1)..end];
            end = newline;
            var isRecord = line.Contains(ContinuedInMarker, StringComparison.Ordinal);
            var isMessage = IsUserLine(line) || line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal);
            if (isRecord || isMessage)
            {
                try
                {
                    using var document = JsonDocument.Parse(line, JsonOptions);
                    var root = document.RootElement;
                    if (isRecord && ClaudeCodeJson.GetString(root, "type") == "continued-in" &&
                        ClaudeCodeJson.GetString(root, "continuedInSessionId") is { } target)
                    {
                        return IsUuid(target) ? target : null;
                    }

                    if (isMessage && IsAnsweredOrPrompt(root))
                    {
                        return null;
                    }
                }
                catch (JsonException)
                {
                }
            }

            if (newline < 0)
            {
                break;
            }
        }

        return null;
    }

    private static bool IsAnsweredOrPrompt(JsonElement root)
    {
        if (ClaudeCodeJson.GetString(root, "type") == "assistant")
        {
            return !ClaudeCodeJson.GetBoolean(root, "isApiErrorMessage") &&
                   ClaudeCodeJson.TryGetObject(root, "message", out var message) &&
                   ClaudeCodeJson.GetString(message, "stop_reason") is not null;
        }

        string? command = null;
        return ReadPromptTitle(root, ref command) is not null;
    }

    // Whether a transcript exists and holds entries (not only records about its session).
    private static bool HasEntries(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            // A file larger than the head is taken to hold entries, as the session list of VS Code does.
            return ReadSmallFile(path, HeaderChunkBytes) is not { } bytes ||
                   bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(ParentUuidMarker)) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The first value of a string property in raw JSON lines, as the SDK finds it without parsing the lines the
    // head and the tail cut; null when absent or empty.
    private static string? FirstValue(string text, string name)
    {
        foreach (var pattern in (ReadOnlySpan<string>)[$"\"{name}\":\"", $"\"{name}\": \""])
        {
            var index = text.IndexOf(pattern, StringComparison.Ordinal);
            if (index >= 0 && ReadQuoted(text, index + pattern.Length) is { } value)
            {
                return NonEmpty(value);
            }
        }

        return null;
    }

    private static string? LastValue(string text, string name)
    {
        string? found = null;
        var foundAt = -1;
        foreach (var pattern in (ReadOnlySpan<string>)[$"\"{name}\":\"", $"\"{name}\": \""])
        {
            for (var index = text.LastIndexOf(pattern, StringComparison.Ordinal); index > foundAt; index = index == 0 ? -1 : text.LastIndexOf(pattern, index - 1, StringComparison.Ordinal))
            {
                if (ReadQuoted(text, index + pattern.Length) is { } value)
                {
                    found = value;
                    foundAt = index;
                    break;
                }
            }
        }

        return NonEmpty(found);
    }

    private static string? ReadQuoted(string text, int start)
    {
        for (var index = start; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '\\':
                    index++;
                    break;
                case '"':
                    var raw = text[start..index];
                    if (!raw.Contains('\\'))
                    {
                        return raw;
                    }

                    try
                    {
                        using var document = JsonDocument.Parse($"\"{raw}\"");
                        return document.RootElement.GetString();
                    }
                    catch (JsonException)
                    {
                        return raw;
                    }
            }
        }

        return null;
    }
}
