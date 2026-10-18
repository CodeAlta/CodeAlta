using System.ComponentModel;
using System.Diagnostics;
using CodeAlta.Agent.Runtime;
using XenoAtom.Logging;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>
/// Converts a document attached to a prompt to Markdown with <see href="https://github.com/elbruno/ElBruno.MarkItDotNet">MarkItDotNet</see>,
/// for the models that cannot read it as it is. The tool is not part of CodeAlta: <c>dotnet dnx</c> runs the pinned
/// version of its NuGet package, which the .NET SDK downloads on first use and keeps in its package cache.
/// </summary>
/// <remarks>
/// The Markdown is written beside the attached copy (<see cref="AgentAttachedFiles.ConvertedPath"/>), where each request
/// finds it. A conversion that fails or cannot run writes nothing: the model is then given the path of the file.
/// </remarks>
public sealed class AttachedDocumentConverter
{
    /// <summary>The NuGet package of the converter.</summary>
    public const string PackageId = "ElBruno.MarkItDotNet.Cli";

    /// <summary>The version of the package that runs: a pinned version, never the latest one.</summary>
    public const string PackageVersion = "0.9.2";

    /// <summary>The longest a conversion may take, its first download included.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private static readonly Logger Log = LogManager.GetLogger("CodeAlta.AttachedDocuments");

    private readonly string? _dotnet;
    private readonly Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<int>> _run;

    /// <summary>Creates a converter that runs the <c>dotnet</c> of the runtime CodeAlta runs on.</summary>
    public AttachedDocumentConverter()
        : this(FindDotnet(), RunAsync)
    {
    }

    internal AttachedDocumentConverter(string? dotnet, Func<ProcessStartInfo, TimeSpan, CancellationToken, Task<int>> run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _dotnet = dotnet;
        _run = run;
    }

    /// <summary>Converts an attached document to Markdown, unless it already was.</summary>
    /// <param name="path">The full path of the attached copy; its extension names its format.</param>
    /// <param name="cancellationToken">Cancels the conversion.</param>
    /// <returns>The path of the Markdown, or null when the document could not be converted.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not a full path.</exception>
    /// <exception cref="OperationCanceledException">The conversion was canceled.</exception>
    public async Task<string?> ConvertAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The path of the document must be a full path.", nameof(path));
        var destination = AgentAttachedFiles.ConvertedPath(path);
        if (File.Exists(destination)) return destination;
        if (_dotnet is null)
        {
            Warn("No dotnet was found to convert an attached document to Markdown.");
            return null;
        }

        // Written under another name, then renamed: a request never reads half a conversion.
        var temporary = destination + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            var exitCode = await _run(CreateStartInfo(_dotnet, path, temporary), Timeout, cancellationToken).ConfigureAwait(false);
            if (exitCode != 0 || !File.Exists(temporary))
            {
                Warn($"The conversion of an attached document to Markdown failed (exit code {exitCode}).");
                return null;
            }

            File.Move(temporary, destination, overwrite: true);
            return destination;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or TimeoutException)
        {
            Warn($"The conversion of an attached document to Markdown could not run: {exception.Message}");
            return null;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The command that converts <paramref name="source"/> to the Markdown file <paramref name="destination"/>.</summary>
    internal static ProcessStartInfo CreateStartInfo(string dotnet, string source, string destination)
    {
        var start = new ProcessStartInfo
        {
            FileName = dotnet,
            WorkingDirectory = Path.GetDirectoryName(source) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // A host without a console (the desktop) must not get a console window for the conversion.
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "dnx", $"{PackageId}@{PackageVersion}", "--yes", "--", source, "--quiet", "--output", destination })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return start;
    }

    /// <summary>The <c>dotnet</c> of the installation this process runs on, else the first one of the path; null when none is found.</summary>
    internal static string? FindDotnet()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        // The runtime is <root>/shared/Microsoft.NETCore.App/<version>: its root holds the dotnet that runs it.
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var root = runtime is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(runtime)));
        if (root is not null && File.Exists(Path.Combine(root, name))) return Path.Combine(root, name);
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    // A host that has not set up its logging (a test) logs nothing.
    private static void Warn(string message)
    {
        if (LogManager.IsInitialized) Log.Warn(message);
    }

    private static async Task<int> RunAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The converter did not start.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        // Both outputs are read to their end, or a full pipe stops the tool; what it says is not used.
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"The conversion took longer than {timeout.TotalMinutes:0} minutes.");
        }

        if (process.ExitCode != 0 && error.IsCompletedSuccessfully && error.Result.Trim() is { Length: > 0 } message)
        {
            Warn($"MarkItDotNet: {(message.Length > 500 ? message[..500] : message)}");
        }

        return process.ExitCode;
    }
}
