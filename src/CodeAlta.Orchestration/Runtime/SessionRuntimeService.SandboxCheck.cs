using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using CodeAlta.Orchestration.Runtime.Sandbox;
using CodeAlta.Sandbox;

namespace CodeAlta.Orchestration.Runtime;

/// <summary>One step of the check of a sandbox.</summary>
/// <param name="Id">What the step checks: <c>available</c>, <c>profile</c>, <c>start</c>, <c>dotnet</c>, <c>git</c>, <c>write</c>, <c>secrets</c> or <c>network</c>.</param>
/// <param name="Passed">Whether the sandbox did what it must.</param>
/// <param name="Detail">What it answered, in a line or a few: a version, the time to start, why it failed.</param>
public sealed record SandboxCheckStep(string Id, bool Passed, string Detail);

public sealed partial class SessionRuntimeService
{
    private static readonly TimeSpan SandboxCheckCommandTime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Checks a sandbox profile on this computer: that the sandbox is there, that a worker starts in it, that the
    /// developer tools run, that it writes in its project, that it does not read the files of CodeAlta, and that its
    /// network answers. Each step comes as it ends; the first that fails a requirement ends the check.
    /// </summary>
    /// <param name="profileName">The profile to check.</param>
    /// <param name="cancellationToken">Stops the check and its worker.</param>
    /// <returns>The steps.</returns>
    /// <exception cref="ArgumentException"><paramref name="profileName"/> is empty.</exception>
    public async IAsyncEnumerable<SandboxCheckStep> CheckSandboxAsync(string profileName, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        var availability = GetSandboxAvailability();
        yield return new("available", availability.IsAvailable, availability.IsAvailable
            ? availability.Backend
            : $"{availability.Reason}{(availability.Setup is null ? string.Empty : " " + availability.Setup)}");
        if (!availability.IsAvailable) yield break;

        ResolvedSandboxProfile? profile = null;
        string? problem = null;
        try
        {
            profile = SandboxProfiles.Resolve(profileName, ReadSandboxSettings().Profiles);
        }
        catch (SandboxProfileException exception)
        {
            problem = exception.Message;
        }

        problem ??= availability.Refusal(profile!.Network);
        yield return new("profile", problem is null, problem ?? $"{profile!.Name}: network {profile.Network.ToString().ToLowerInvariant()}");
        if (problem is not null) yield break;

        // Not in the temporary folder of the user: a parent with that many entries is left out of reach of the sandbox,
        // and the shell would not start in a folder below it.
        var folder = Directory.CreateDirectory(Path.Combine(SandboxDataRoot ?? SandboxHostDiscovery.DefaultDataRoot(), "check", Guid.NewGuid().ToString("N")[..12])).FullName;
        var sessionId = "sandbox-check-" + Guid.NewGuid().ToString("N");
        try
        {
            var start = new SessionSandboxStart(sessionId, "check", profile!, new SandboxSessionFolders { WorkingDirectory = folder }, [], CommandGuard: false);
            var stopwatch = Stopwatch.StartNew();
            var (lease, failure) = await TryAcquireCheckWorkerAsync(start, cancellationToken).ConfigureAwait(false);
            yield return new("start", lease is not null, lease is null ? failure! : $"{stopwatch.ElapsedMilliseconds} ms");
            if (lease is null) yield break;
            using (lease)
            {
                foreach (var (id, command, mustSucceed) in SandboxCheckCommands(profile!))
                {
                    var (exitCode, output) = await RunCheckCommandAsync(lease, command, folder, cancellationToken).ConfigureAwait(false);
                    var passed = mustSucceed ? exitCode == 0 : exitCode != 0;
                    yield return new(id, passed, Shorten(output.Length == 0 ? $"exit code {exitCode}" : output));
                }
            }
        }
        finally
        {
            await SandboxWorkers.StopAsync(sessionId).ConfigureAwait(false);
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task<(SessionSandboxWorkers.Lease? Lease, string? Failure)> TryAcquireCheckWorkerAsync(SessionSandboxStart start, CancellationToken cancellationToken)
    {
        try
        {
            return (await SandboxWorkers.AcquireAsync(start, cancellationToken).ConfigureAwait(false), null);
        }
        catch (SandboxUnavailableException unavailable)
        {
            return (null, unavailable.Message);
        }
    }

    // What the check runs, in the shell of the tools: what must work, and the file of CodeAlta that must stay out of reach.
    private IEnumerable<(string Id, string Command, bool MustSucceed)> SandboxCheckCommands(ResolvedSandboxProfile profile)
    {
        var config = Path.Combine(_catalogOptions.GlobalRoot, "config.toml");
        var windows = OperatingSystem.IsWindows();
        yield return ("dotnet", "dotnet --version", true);
        yield return ("git", "git --version", true);
        yield return ("write", windows ? "Set-Content -Path check.txt -Value ok; Get-Content check.txt" : "echo ok > check.txt && cat check.txt", true);
        yield return ("secrets", windows ? $"Get-Content -LiteralPath '{config.Replace("'", "''", StringComparison.Ordinal)}' -ErrorAction Stop | Out-Null" : $"cat '{config.Replace("'", "'\\''", StringComparison.Ordinal)}' > /dev/null", false);
        if (profile.Network != SandboxNetwork.Off)
        {
            var host = profile.Network == SandboxNetwork.On ? "https://api.nuget.org/v3/index.json"
                : profile.Hosts.FirstOrDefault(static host => !host.StartsWith('*')) is { } first ? $"https://{first}/" : null;
            if (host is not null)
                yield return ("network", windows ? $"(Invoke-WebRequest -Uri '{host}' -Method Head -UseBasicParsing -TimeoutSec 20).StatusCode" : $"curl -sSI --max-time 20 -o /dev/null -w '%{{http_code}}' '{host}'", true);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunCheckCommandAsync(SessionSandboxWorkers.Lease lease, string command, string folder, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var process = lease.Client.StartProcess(command, folder, text => { lock (output) output.Append(text); });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(SandboxCheckCommandTime);
        try
        {
            var exitCode = await process.Completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            lock (output) return (exitCode, output.ToString().Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill();
            return (-1, $"no answer in {SandboxCheckCommandTime.TotalSeconds:0} s");
        }
    }

    private static string Shorten(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3);
        var joined = string.Join(" · ", lines);
        return joined.Length <= 300 ? joined : joined[..300] + "…";
    }
}
