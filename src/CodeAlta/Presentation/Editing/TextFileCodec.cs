using System.Text;

namespace CodeAlta.Presentation.Editing;

internal static class TextFileCodec
{
    public static async Task<TextFileSnapshot> LoadAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        var bomProbe = new byte[Math.Min(4, (int)Math.Min(stream.Length, 4))];
        var bomLength = await stream.ReadAsync(bomProbe.AsMemory(0, bomProbe.Length), cancellationToken);
        stream.Position = 0;

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        var text = await reader.ReadToEndAsync(cancellationToken);
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
        return new TextFileSnapshot(
            text,
            reader.CurrentEncoding,
            HasByteOrderMark(bomProbe.AsSpan(0, bomLength)),
            lastWriteTimeUtc);
    }

    public static async Task<TextFileSnapshot> SaveAsync(
        string fullPath,
        string text,
        Encoding encoding,
        bool hasByteOrderMark,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(encoding);

        if (Path.GetDirectoryName(fullPath) is { Length: > 0 } directoryPath)
        {
            Directory.CreateDirectory(directoryPath);
        }

        var resolvedEncoding = ResolveEncoding(encoding, hasByteOrderMark);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (var writer = new StreamWriter(stream, resolvedEncoding))
        {
            await writer.WriteAsync(text.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }

        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(fullPath);
        return new TextFileSnapshot(text, resolvedEncoding, hasByteOrderMark, lastWriteTimeUtc);
    }

    private static bool HasByteOrderMark(ReadOnlySpan<byte> bytes)
    {
        return bytes switch
        {
            [0xEF, 0xBB, 0xBF, ..] => true,
            [0xFF, 0xFE, 0x00, 0x00, ..] => true,
            [0x00, 0x00, 0xFE, 0xFF, ..] => true,
            [0xFF, 0xFE, ..] => true,
            [0xFE, 0xFF, ..] => true,
            _ => false,
        };
    }

    private static Encoding ResolveEncoding(Encoding encoding, bool hasByteOrderMark)
    {
        return encoding.CodePage switch
        {
            65001 => new UTF8Encoding(hasByteOrderMark),
            _ => encoding,
        };
    }
}
