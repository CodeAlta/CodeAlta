using System.Diagnostics;

namespace CodeAlta.CodexSdk.Generator;

internal static class OutputDirectoryCleaner
{
    public static async Task CleanAsync(string outputDir, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputDir);

        if (!Directory.Exists(outputDir))
        {
            return;
        }

        // `dotnet run` builds projects that may read the previously-generated files, so deletion can race
        // with file handles held briefly by build/IDE processes. Retry with small backoff and clear attributes.
        var attempts = 10;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                ClearFileAttributes(outputDir);
                Directory.Delete(outputDir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                if (attempt == attempts)
                {
                    throw new IOException(
                        $"Failed to clean generator output directory '{outputDir}'. " +
                        "Close editors/build processes that might be holding file handles and retry.",
                        ex);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        Debug.Fail("Unreachable.");
    }

    private static void ClearFileAttributes(string root)
    {
        try
        {
            foreach (var filePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(filePath, FileAttributes.Normal);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}

