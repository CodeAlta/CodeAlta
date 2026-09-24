using System.Text;

namespace CodeAlta.Catalog.Skills;

/// <summary>Status of a bounded text read. Only <see cref="Complete"/> has authoritative content.</summary>
public enum SkillBoundedReadStatus
{
    /// <summary>The entire file was read.</summary>
    Complete,
    /// <summary>The file did not exist when opened.</summary>
    Missing,
    /// <summary>The file exceeded the byte budget; its contents were discarded.</summary>
    TooLarge,
    /// <summary>The file could not be read.</summary>
    ReadError,
    /// <summary>The complete text could not be parsed as configuration.</summary>
    Invalid,
}

internal sealed record SkillBoundedTextReadResult(SkillBoundedReadStatus Status, string? Content);

/// <summary>Reads only up to the caller's byte limit plus one sentinel byte, even if the file grows.</summary>
internal static class SkillBoundedTextReader
{
    internal const int MaximumBytes = 256 * 1024;

    internal static async Task<SkillBoundedTextReadResult> ReadAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, MaximumBytes);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[maxBytes + 1];
            var read = 0;
            while (read < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    using var textStream = new MemoryStream(buffer, 0, read, writable: false);
                    using var reader = new StreamReader(textStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    return new SkillBoundedTextReadResult(SkillBoundedReadStatus.Complete, reader.ReadToEnd());
                }

                read += count;
            }

            return new SkillBoundedTextReadResult(SkillBoundedReadStatus.TooLarge, null);
        }
        catch (FileNotFoundException)
        {
            return new SkillBoundedTextReadResult(SkillBoundedReadStatus.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new SkillBoundedTextReadResult(SkillBoundedReadStatus.Missing, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new SkillBoundedTextReadResult(SkillBoundedReadStatus.ReadError, null);
        }
    }
}
