namespace CodeAlta.Agent;

internal static class AgentDataUri
{
    public static bool TryParseBase64(string value, out string mediaType, out string base64Data)
    {
        mediaType = string.Empty;
        base64Data = string.Empty;
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = value.IndexOf(',', StringComparison.Ordinal);
        if (commaIndex < 0)
        {
            return false;
        }

        var metadata = value.AsSpan(5, commaIndex - 5);
        var isBase64 = false;
        foreach (var segmentRange in metadata.Split(';'))
        {
            if (metadata[segmentRange].Equals("base64", StringComparison.OrdinalIgnoreCase))
            {
                isBase64 = true;
                break;
            }
        }

        if (!isBase64)
        {
            return false;
        }

        var payload = value[(commaIndex + 1)..].Trim();
        if (payload.Length == 0)
        {
            return false;
        }

        try
        {
            _ = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return false;
        }

        var separatorIndex = metadata.IndexOf(';');
        var parsedMediaType = separatorIndex < 0 ? metadata : metadata[..separatorIndex];
        mediaType = parsedMediaType.IsWhiteSpace() ? "application/octet-stream" : parsedMediaType.Trim().ToString();
        base64Data = payload;
        return true;
    }
}
