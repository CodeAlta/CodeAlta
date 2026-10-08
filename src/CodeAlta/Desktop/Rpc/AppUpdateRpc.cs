using System.Diagnostics;
using CodeAlta.Hosting;
using NeoAstra.Rpc;
using NuGet.Versioning;

namespace CodeAlta.Desktop.Rpc;

/// <summary>
/// Whether a newer CodeAlta package is published, checked at start as the terminal application does: the page
/// shows the new version, its release notes and the command that updates the tool. The desktop stays open, or
/// in the notification area, for days: the page asks again as time passes, and a check that is old enough is
/// then made again.
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
    private long _checkedAt;

    /// <summary>How old a check is before the page's next question makes it again.</summary>
    internal static readonly TimeSpan Period = TimeSpan.FromHours(4);

    /// <summary>
    /// How old a check is before it is made again for a question that asks for it (the About page was
    /// opened), or after it failed: nuget.org is not asked more often than this.
    /// </summary>
    internal static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(5);

    /// <summary>The clock the age of a check is measured with.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

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

    /// <summary>
    /// Updates the installed tool and starts the application again: it hands the update to a helper that
    /// outlives this process and asks the application to exit. Its argument says whether the newer version
    /// is a prerelease. Null where the application is not an installed tool.
    /// </summary>
    internal Func<bool, bool>? Install { get; init; }

    /// <summary>Calls a started update off, when the user decided not to exit after all.</summary>
    internal Action? CancelInstall { get; init; }

    /// <summary>How the update started by the previous run went (<c>ok</c> or <c>failed</c>); null when none ran.</summary>
    internal string? Installed { get; init; }

    /// <summary>Starts the first check of this run; the page asks for its result later.</summary>
    internal void Start() => _ = Result(refresh: false);

    /// <summary>
    /// The result of the check: <c>available</c> with the newer version, its release notes and the update
    /// command, <c>latest</c>, <c>not_found</c> (the package is not published), <c>failed</c> (nuget.org
    /// could not be read) or <c>unavailable</c> (a build that is not a published version). A check older than
    /// <see cref="Period"/> is made again first, and one older than <see cref="MinimumAge"/> when the request
    /// asks for it or when it failed.
    /// </summary>
    [NeoRpcMethod("check")]
    public async Task<AppUpdateResponse> CheckAsync(AppUpdateCheckRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await Result(request.Refresh).WaitAsync(cancellationToken).ConfigureAwait(false);
        return result with { CanInstall = Install is not null && result.Status == "available", Installed = Installed };
    }

    /// <summary>
    /// Updates to the newer version and starts the application again. <c>started</c>: the application now
    /// exits as it does for Exit, with its questions. <c>unavailable</c>: there is nothing to install, or
    /// the application is not an installed tool. <c>failed</c>: the helper could not be started.
    /// </summary>
    [NeoRpcMethod("install")]
    public async Task<AppUpdateOpenResponse> InstallAsync(AppUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await Known().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Install is null || result is not { Status: "available", LatestVersion: { } latest }) return new("unavailable");
        return new(Install(NuGetVersion.TryParse(latest, out var version) && version.IsPrerelease) ? "started" : "failed");
    }

    /// <summary>Calls off an update whose exit the user canceled.</summary>
    [NeoRpcMethod("cancelInstall")]
    public AppUpdateOpenResponse CancelInstallation(AppUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (CancelInstall is null) return new("unavailable");
        CancelInstall();
        return new("ok");
    }

    /// <summary>Opens the release notes of the newer version in the user's browser.</summary>
    [NeoRpcMethod("openReleaseNotes")]
    public async Task<AppUpdateOpenResponse> OpenReleaseNotesAsync(AppUpdateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await Known().WaitAsync(cancellationToken).ConfigureAwait(false);
        // The address is the host's own: the page cannot make the host open anything else.
        return new(result is { Status: "available", ReleaseNotes: { } address } && _open(address) ? "ok" : "unavailable");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _closing.Cancel();
        _closing.Dispose();
    }

    // What the page was last told: installing a version or opening its notes asks nuget.org nothing more.
    private Task<AppUpdateResponse> Known()
    {
        lock (_gate) return _result ?? Result(refresh: false);
    }

    private Task<AppUpdateResponse> Result(bool refresh)
    {
        lock (_gate)
        {
            if (_result is not null && !Expired(_result, refresh)) return _result;
            _checkedAt = Time.GetTimestamp();
            return _result = Task.Run(CheckOnceAsync);
        }
    }

    // One check at a time; a build without a published version has nothing to ask again.
    private bool Expired(Task<AppUpdateResponse> last, bool refresh)
    {
        if (!last.IsCompleted) return false;
        if (!last.IsCompletedSuccessfully) return true;
        var status = last.Result.Status;
        if (status == "unavailable") return false;
        return Time.GetElapsedTime(_checkedAt) >= (refresh || status == "failed" ? MinimumAge : Period);
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

/// <param name="Refresh">The user is looking at the result (the About page): a check of a few minutes is made again.</param>
internal sealed record AppUpdateCheckRequest(bool Refresh = false);

/// <param name="Status"><c>available</c>, <c>latest</c>, <c>not_found</c>, <c>failed</c> or <c>unavailable</c>.</param>
/// <param name="PackageId">The package that was checked.</param>
/// <param name="CurrentVersion">The running version.</param>
/// <param name="LatestVersion">The newest published version, when known.</param>
/// <param name="Command">The command that updates the installed tool, when a newer version exists.</param>
/// <param name="ReleaseNotes">The address of the newer version's release notes.</param>
internal sealed record AppUpdateResponse(string Status, string PackageId, string CurrentVersion, string? LatestVersion, string? Command, string? ReleaseNotes)
{
    /// <summary>The application can install the newer version itself and start again.</summary>
    public bool CanInstall { get; init; }

    /// <summary>How the update started by the previous run went: <c>ok</c>, <c>failed</c>, or null when none ran.</summary>
    public string? Installed { get; init; }
}

/// <summary><c>ok</c>, <c>started</c>, <c>failed</c> or <c>unavailable</c>.</summary>
internal sealed record AppUpdateOpenResponse(string Status);
