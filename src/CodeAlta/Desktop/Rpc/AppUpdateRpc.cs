using System.Diagnostics;
using CodeAlta.Hosting;
using NeoAstra.Rpc;
using NuGet.Versioning;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Whether a newer CodeAlta package is published, checked once at start as the terminal application does:
/// the page shows the new version, its release notes and the command that updates the tool.
/// </summary>
[NeoRpcService("appUpdate", Version = 1)]
internal sealed class AppUpdateService : IDisposable
{
    /// <summary>The package the desktop is installed from.</summary>
    internal const string PackageId = "CodeAlta";

    private readonly string _version;
    private readonly Func<string, NuGetVersion, bool, CancellationToken, Task<CodeAltaNuGetUpdateCheckResult>>? _check;
    private readonly Func<string, bool> _open;
    private readonly CancellationTokenSource _closing = new();
    private readonly Lock _gate = new();
    private Task<AppUpdateResponse>? _result;

    /// <summary>Creates an unavailable service for a window without an installed application behind it.</summary>
    internal AppUpdateService()
    {
        _version = "development";
        _open = static _ => false;
    }

    /// <param name="version">The running version, as the assembly records it.</param>
    /// <param name="check">Looks the package up; nuget.org by default.</param>
    /// <param name="open">Opens an address in the user's browser; the system's opener by default.</param>
    /// <exception cref="ArgumentException"><paramref name="version"/> is blank.</exception>
    internal AppUpdateService(string version,
        Func<string, NuGetVersion, bool, CancellationToken, Task<CodeAltaNuGetUpdateCheckResult>>? check = null, Func<string, bool>? open = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        _version = version;
        _check = check ?? ((package, current, prerelease, token) => CodeAltaNuGetUpdateChecker.CheckNuGetOrgAsync(package, current, prerelease, token));
        _open = open ?? OpenInBrowser;
    }

    /// <summary>Starts the one check of this run; the page asks for its result later.</summary>
    internal void Start() => _ = Result();

    /// <summary>
    /// The result of this run's check: <c>available</c> with the newer version, its release notes and the
    /// update command, <c>latest</c>, <c>not_found</c> (the package is not published), <c>failed</c>
    /// (nuget.org could not be read) or <c>unavailable</c> (a build that is not a published version).
    /// </summary>
    [NeoRpcMethod("check")]
    public async Task<AppUpdateResponse> CheckAsync(AppUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await Result().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens the release notes of the newer version in the user's browser.</summary>
    [NeoRpcMethod("openReleaseNotes")]
    public async Task<AppUpdateOpenResponse> OpenReleaseNotesAsync(AppUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await Result().WaitAsync(cancellationToken).ConfigureAwait(false);
        // The address is the host's own: the page cannot make the host open anything else.
        return new(result is { Status: "available", ReleaseNotes: { } address } && _open(address) ? "ok" : "unavailable");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }

    private Task<AppUpdateResponse> Result()
    {
        lock (_gate) return _result ??= Task.Run(CheckOnceAsync);
    }

    private async Task<AppUpdateResponse> CheckOnceAsync()
    {
        // A build output has no published version to compare (its version carries the commit only as metadata).
        if (_check is null || !NuGetVersion.TryParse(_version, out var current)) return new("unavailable", PackageId, _version, null, null, null);
        try
        {
            var result = await _check(PackageId, current, current.IsPrerelease, _closing.Token).ConfigureAwait(false);
            if (!result.PackageFound) return new("not_found", PackageId, result.CurrentVersionText, null, null, null);
            if (!result.HasNewerVersion || result.LatestVersion is not { } latest) return new("latest", PackageId, result.CurrentVersionText, result.LatestVersionText, null, null);
            return new("available", PackageId, result.CurrentVersionText, result.LatestVersionText,
                CodeAltaNuGetUpdateChecker.UpdateCommand(PackageId, latest.IsPrerelease), CodeAltaNuGetUpdateChecker.ReleaseNotesUri(result.LatestVersionText));
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or InvalidOperationException or IOException)
        {
            return new("failed", PackageId, current.ToNormalizedString(), null, null, null);
        }
    }

    private static bool OpenInBrowser(string address)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo { FileName = address, UseShellExecute = true });
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}

internal sealed record AppUpdateRequest;

/// <param name="Status"><c>available</c>, <c>latest</c>, <c>not_found</c>, <c>failed</c> or <c>unavailable</c>.</param>
/// <param name="PackageId">The package that was checked.</param>
/// <param name="CurrentVersion">The running version.</param>
/// <param name="LatestVersion">The newest published version, when known.</param>
/// <param name="Command">The command that updates the installed tool, when a newer version exists.</param>
/// <param name="ReleaseNotes">The address of the newer version's release notes.</param>
internal sealed record AppUpdateResponse(string Status, string PackageId, string CurrentVersion, string? LatestVersion, string? Command, string? ReleaseNotes);

/// <summary><c>ok</c> or <c>unavailable</c>.</summary>
internal sealed record AppUpdateOpenResponse(string Status);
