namespace CodeAlta.Plugin.Mcp;

// Limit the actual read, not a preliminary file-size check that a concurrent writer can invalidate.
internal static class McpBoundedTextReader
{
    internal static string Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        var buffer = new char[1024 * 1024 + 1];
        var count = reader.ReadBlock(buffer);
        if (count == buffer.Length) throw new InvalidDataException("MCP inventory source exceeds read limit.");
        return new string(buffer, 0, count);
    }
}
