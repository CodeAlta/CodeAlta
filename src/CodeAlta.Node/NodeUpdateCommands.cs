using CodeAlta.Fleet.Update;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode update</c>.</summary>
internal sealed class NodeUpdateOptions
{
    /// <summary>Gets the profile options.</summary>
    public NodeProfileOptions Profile { get; } = new();

    /// <summary>Gets or sets the exact version; null for the latest.</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the feed; null for the configured one, or nuget.org.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets the SHA-512 the package must have.</summary>
    public string? Sha512 { get; set; }

    /// <summary>Gets or sets whether only the feed is read.</summary>
    public bool Check { get; set; }

    /// <summary>Gets or sets whether the node switches without waiting for its sessions.</summary>
    public bool Now { get; set; }

    /// <summary>Gets or sets whether a prerelease may be chosen.</summary>
    public bool Prerelease { get; set; }

    /// <summary>Gets the words after the command (none are taken).</summary>
    public List<string> Words { get; } = [];
}

/// <summary>
/// <c>altanode update</c>: downloads a version of CodeAlta Node from a NuGet feed, checks its hash, stages it in
/// <c>fleet/versions</c> of the state root, and asks the node of the profile to switch to it when idle; without a running
/// node, the next <c>altanode run</c> starts it. The new version runs on trial and is rolled back if it does not confirm.
/// </summary>
internal sealed class NodeUpdateCommands(TextWriter output, TextWriter error, string version, string? installedVersion)
{
    /// <summary>How long <c>update</c> follows a switch before it leaves the node to it.</summary>
    internal static readonly TimeSpan FollowLimit = UpdateLauncher.DefaultTrialLimit + TimeSpan.FromSeconds(60);

    /// <summary>Gets or sets what stops the command, for tests; none by default.</summary>
    public CancellationToken Stop { get; init; }

    /// <summary>Gets or sets the HTTP handler of the feeds, for tests; null for the usual one.</summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>Gets or sets the runtime whose package is read, for tests; null for the one of this machine.</summary>
    public string? Runtime { get; init; }

    public async ValueTask<int> UpdateAsync(NodeUpdateOptions options)
    {
        if (options.Profile.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        if (options.Words.Count > 0) return await UsageAsync($"update takes no argument: '{options.Words[0]}' (--version <v> names a version).").ConfigureAwait(false);
        if (options.Check && options.Now) return await UsageAsync("--check changes nothing: --now cannot go with it.").ConfigureAwait(false);
        var store = NodeUpdatePaths.Store(profile);
        try
        {
            var state = store.Read();
            // What runs on the profile: the selected version, or the installed entry point (this executable, unless it runs a staged version).
            var running = UpdateVersion.Parse(state.Current ?? installedVersion ?? version);
            using var http = Handler is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(10) } : new HttpClient(Handler, disposeHandler: false);
            var updater = new SelfUpdater(new SelfUpdaterOptions
            {
                Product = UpdateProduct.Node, Store = store, Running = running, Http = http, Runtime = Runtime,
                Policy = UpdateSourcePolicy.Load(NodeUpdatePaths.PolicyPath(profile)),
            });
            var request = new UpdateRequest { Version = options.Version, Source = options.Source, ExpectedSha512 = options.Sha512, Prerelease = options.Prerelease, Now = options.Now };
            var check = await updater.CheckAsync(request, Stop).ConfigureAwait(false);
            await output.WriteLineAsync($"Feed: {check.Source} ({check.PackageId}).").ConfigureAwait(false);
            await output.WriteLineAsync($"Runs: {running}{(installedVersion is not null && state.Current is not null ? $" (installed {installedVersion})" : "")}; latest: {check.Latest?.ToString() ?? "none"}.")
                .ConfigureAwait(false);
            if (check.Note is { } note) await output.WriteLineAsync(note).ConfigureAwait(false);
            if (check.Target is not { } target) return NodeExitCodes.Success;
            if (options.Check)
            {
                await output.WriteLineAsync($"An update to {target} is available: altanode update{(options.Version is null ? "" : " --version " + target)} installs it.").ConfigureAwait(false);
                return NodeExitCodes.Success;
            }

            var staged = await updater.StageAsync(check, request, Stop).ConfigureAwait(false);
            await output.WriteLineAsync(staged.AlreadyStaged
                ? $"{target} is staged already in {staged.Path} (its package matches: {staged.Checked})."
                : $"Downloaded {check.PackageId} {target}, hash checked by the {staged.Checked}, staged in {staged.Path}.").ConfigureAwait(false);
            store.RequestSwitch(target, options.Now, "local");
            var holder = NodeProfileStatus.Read(profile);
            if (!holder.IsHeld || !IsNode(holder))
            {
                await output.WriteLineAsync(holder.IsHeld
                    ? $"{holder.Describe()} It is not a node: the next altanode run (or its service) starts {target} on trial."
                    : $"No node runs on this profile: the next altanode run (or its service) starts {target} on trial.").ConfigureAwait(false);
                return NodeExitCodes.Success;
            }

            await output.WriteLineAsync($"The node of this profile (PID {holder.ProcessId}) switches to {target} {(options.Now ? "now" : "when no session works")}.").ConfigureAwait(false);
            return await FollowAsync(store, target).ConfigureAwait(false);
        }
        catch (UpdateException exception)
        {
            await error.WriteLineAsync($"altanode update: {exception.Message}").ConfigureAwait(false);
            return NodeExitCodes.Failure;
        }
    }

    // Follows the switch until the new version confirmed, was rolled back, or waits for its sessions.
    private async Task<int> FollowAsync(UpdateVersionStore store, UpdateVersion target)
    {
        var (outcome, message) = await UpdateFollow.FollowAsync(store, target, FollowLimit, line => output.WriteLineAsync(line), Stop).ConfigureAwait(false);
        switch (outcome)
        {
            case UpdateFollowOutcome.Confirmed:
                await output.WriteLineAsync("CodeAlta Node " + message).ConfigureAwait(false);
                return NodeExitCodes.Success;
            case UpdateFollowOutcome.Failed:
            case UpdateFollowOutcome.Withdrawn:
                await error.WriteLineAsync("altanode update: " + message).ConfigureAwait(false);
                return NodeExitCodes.Failure;
            default:
                await output.WriteLineAsync(message + " altanode status shows where it stands.").ConfigureAwait(false);
                return NodeExitCodes.Success;
        }
    }

    // The holder of a profile is a node unless it is CodeAlta Desktop or TUI.
    private static bool IsNode(NodeProfileStatus holder)
        => holder.ProcessName is not { } name || !(string.Equals(name, "alta", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "altatui", StringComparison.OrdinalIgnoreCase));

    private async ValueTask<int> UsageAsync(string message)
    {
        await error.WriteLineAsync("altanode: " + message).ConfigureAwait(false);
        return NodeExitCodes.InvalidUsage;
    }
}
