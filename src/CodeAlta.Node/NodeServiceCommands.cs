using CodeAlta.Fleet.Identity;
using CodeAlta.Hosting;
using CodeAlta.Fleet.Update;
using CodeAlta.Fleet.Update.Service;

namespace CodeAlta.Node;

/// <summary>The options of <c>altanode install</c> and <c>uninstall</c>.</summary>
internal sealed class NodeInstallOptions
{
    /// <summary>Gets the options of the <c>run</c> the service starts: the profile, where it listens, its name.</summary>
    public NodeRunOptions Run { get; } = new();

    /// <summary>Gets or sets whether a service of the current user was asked for (the default).</summary>
    public bool User { get; set; }

    /// <summary>Gets or sets whether a system service was asked for.</summary>
    public bool System { get; set; }

    /// <summary>Gets or sets the account of a system service.</summary>
    public string? Account { get; set; }

    /// <summary>Gets or sets whether the plan is printed and nothing changes.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets whether the password of the account of a Windows service is read from standard input.</summary>
    public bool PasswordStdin { get; set; }
}

/// <summary>
/// <c>altanode install</c> and <c>uninstall</c>: the service of the OS that runs <c>altanode run</c> for a profile, as
/// the user whose logins it uses (<c>doc/fleet/node.md</c>, "Running as a service"); and <c>altanode status</c>, which
/// tells what the node of a profile does and whether it will still run after a restart or a logout.
/// </summary>
internal sealed class NodeServiceCommands(TextWriter output, TextWriter error, Func<ServiceEnvironment?> environment, Func<ServiceExecutable?> executable)
{
    /// <summary>Gets or sets what stops a call to the node, for tests; none by default.</summary>
    public CancellationToken Stop { get; init; }

    /// <summary>Gets or sets what reads the password of <c>--password-stdin</c>: the first line of standard input by default.</summary>
    public Func<Task<string?>> ReadPassword { get; init; } = static async () => await Console.In.ReadLineAsync().ConfigureAwait(false);

    /// <summary>Gets or sets the version of the installed entry point (this executable); null when this process runs a staged version.</summary>
    public string? InstalledVersion { get; init; }

    public async ValueTask<int> InstallAsync(NodeInstallOptions options)
    {
        var (manager, plan, failure) = await PlanAsync(options, "install").ConfigureAwait(false);
        if (manager is null || plan is null) return failure;
        if (options.DryRun)
        {
            await WritePlanAsync(plan, install: true).ConfigureAwait(false);
            return NodeExitCodes.Success;
        }

        var profile = options.Run.Profile.Resolve(out _)!;
        var held = NodeProfileStatus.Read(profile);
        // The password goes to Windows only: read here, never printed, never written.
        var password = options.PasswordStdin ? await ReadPassword().ConfigureAwait(false) : null;
        var code = manager.Install(plan, output, error, password);
        if (code == NodeExitCodes.Success)
        {
            await output.WriteLineAsync(ServiceExecutableLocator.Describe(UpdateProduct.Node, executable()!)).ConfigureAwait(false);
            // The node of the service it replaced stopped with it (launchd, systemd): only an instance that still holds the
            // profile keeps the new one waiting.
            if (held.IsHeld && plan.Spec.Scope == ServiceScope.User && NodeProfileStatus.Read(profile) is { IsHeld: true } still && still.ProcessId == held.ProcessId)
            {
                await output.WriteLineAsync($"{held.Describe()} The service's node starts once that instance exits (one instance runs per profile).")
                    .ConfigureAwait(false);
            }

            await output.WriteLineAsync("altanode status tells whether it runs; altanode logs shows its log.").ConfigureAwait(false);
        }

        return code;
    }

    public async ValueTask<int> UninstallAsync(NodeInstallOptions options)
    {
        var (manager, plan, failure) = await PlanAsync(options, "uninstall").ConfigureAwait(false);
        if (manager is null || plan is null) return failure;
        if (options.DryRun)
        {
            await WritePlanAsync(plan, install: false).ConfigureAwait(false);
            return NodeExitCodes.Success;
        }

        var installed = manager.Query(plan).Installed;
        var code = manager.Uninstall(plan, output, error);
        if (code != NodeExitCodes.Success || !installed || !plan.LeavesProcessRunning) return code;
        return await StopNodeAsync(options.Run.Profile.Resolve(out _)!).ConfigureAwait(false);
    }

    /// <summary>How long <c>uninstall</c> waits for the node to drain its turns and release the profile.</summary>
    internal static readonly TimeSpan StopPatience = TimeSpan.FromSeconds(75);

    // Stops the node of a profile the service manager leaves running: gracefully through its local control, at once when
    // it does not listen. An instance that is not a node (CodeAlta Desktop, TUI) is left alone.
    private async Task<int> StopNodeAsync(CodeAltaInstanceProfile profile)
    {
        var control = NodeLocalControl.Read(profile);
        if (!control.Status.IsHeld) return NodeExitCodes.Success;
        if (!string.Equals(control.Status.ProcessName, "altanode", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync($"{control.Status.Describe()} It is not a node: it keeps running.").ConfigureAwait(false);
            return NodeExitCodes.Success;
        }

        if (control.NodeListens)
        {
            await output.WriteLineAsync($"Stopping the node of this profile (PID {control.Status.ProcessId}): running turns get a bound to finish...").ConfigureAwait(false);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var (client, identity) = await control.ConnectAsync(timeout.Token).ConfigureAwait(false);
                using (identity)
                await using (client.ConfigureAwait(false))
                {
                    await client.StopNodeAsync(timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !Stop.IsCancellationRequested)
            {
                await error.WriteLineAsync($"altanode uninstall: the node did not take the request to stop ({NodeClientCommands.Describe(exception)}).").ConfigureAwait(false);
            }
        }
        else if (control.Status.ProcessId is { } processId)
        {
            // A node that does not listen has no local control: it is stopped at once, as the service manager would.
            await output.WriteLineAsync($"Stopping the node of this profile (PID {processId}) at once: it does not listen, so it cannot be asked.").ConfigureAwait(false);
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                process.Kill();
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        var deadline = DateTime.UtcNow + StopPatience;
        while (NodeProfileStatus.Read(profile).IsHeld)
        {
            if (DateTime.UtcNow > deadline)
            {
                await error.WriteLineAsync($"altanode uninstall: the node still runs after {StopPatience.TotalSeconds} s (PID {control.Status.ProcessId}).").ConfigureAwait(false);
                return NodeExitCodes.Failure;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), Stop).ConfigureAwait(false);
        }

        await output.WriteLineAsync("The node stopped.").ConfigureAwait(false);
        return NodeExitCodes.Success;
    }

    public async ValueTask<int> StatusAsync(NodeProfileOptions options)
    {
        if (options.Resolve(out var invalid) is not { } profile) return await UsageAsync(invalid!).ConfigureAwait(false);
        var control = NodeLocalControl.Read(profile);
        await output.WriteLineAsync($"Profile: {profile.StateRoot}").ConfigureAwait(false);
        await output.WriteLineAsync(control.Status.Describe()).ConfigureAwait(false);
        var reached = false;
        Fleet.Protocol.FleetUpdateStatus? update = null;
        if (control.NodeListens)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var (client, identity) = await control.ConnectAsync(timeout.Token).ConfigureAwait(false);
                using (identity)
                await using (client.ConfigureAwait(false))
                {
                    var status = await client.GetStatusAsync(timeout.Token).ConfigureAwait(false);
                    await output.WriteLineAsync($"Node: {status.Name} ({status.DeviceId}), CodeAlta {status.Version}{(status.AcceptsWork ? "" : status.Updating ? ", switching versions" : ", stopping")}.")
                        .ConfigureAwait(false);
                    update = status.Update;
                    await output.WriteLineAsync($"Listening on: {string.Join(", ", control.Listener!.Addresses)}").ConfigureAwait(false);
                    await output.WriteLineAsync($"Paired devices: {status.TrustedDevices}{(status.RevokedDevices > 0 ? $" ({status.RevokedDevices} revoked)" : "")}; connected now: {status.Channels}.")
                        .ConfigureAwait(false);
                    await output.WriteLineAsync($"Sessions at work: {status.SessionsAtWork}.").ConfigureAwait(false);
                    if (status.Sandbox is { } sandbox) await output.WriteLineAsync(SandboxLine(sandbox)).ConfigureAwait(false);
                    reached = true;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !Stop.IsCancellationRequested)
            {
                await output.WriteLineAsync($"Listening on: {string.Join(", ", control.Listener!.Addresses)}, but the node did not answer: {NodeClientCommands.Describe(exception)}")
                    .ConfigureAwait(false);
            }
        }
        else if (control.Status.IsHeld)
        {
            await output.WriteLineAsync("Listening: no (a node that is still starting or was started with --no-listen, or CodeAlta Desktop).").ConfigureAwait(false);
        }

        if (!reached)
        {
            try
            {
                var devices = new TrustedDeviceStore(control.Catalog).List(includeRevoked: true);
                var revoked = devices.Count(static device => device.IsRevoked);
                await output.WriteLineAsync($"Paired devices: {devices.Count - revoked}{(revoked > 0 ? $" ({revoked} revoked)" : "")}.").ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                await output.WriteLineAsync($"Paired devices: unreadable ({exception.Message}).").ConfigureAwait(false);
            }
        }

        // The versions: as the running node reports them, otherwise as the folder of versions of the profile says.
        if (update is null)
        {
            var state = NodeUpdatePaths.Store(profile).Read();
            var running = state.Current ?? InstalledVersion;
            if (running is not null) update = Fleet.Update.UpdateStatusReport.Describe(state, running, InstalledVersion);
        }

        if (update is not null)
        {
            foreach (var line in Fleet.Update.UpdateStatusReport.Lines(update, "altanode")) await output.WriteLineAsync(line).ConfigureAwait(false);
        }

        await new NodeServerCommands(output, error).WriteStatusAsync(control.Catalog, control.Status.IsHeld).ConfigureAwait(false);
        if (environment() is { } env)
        {
            var manager = new ServiceManager(env);
            try
            {
                var plan = manager.Plan(Request(profile, options, new NodeRunOptions(), ServiceScope.User, null, executable() ?? new ServiceExecutable(ServiceExecutableKind.Executable, "altanode", [])));
                foreach (var line in ServiceManager.Describe(manager.Query(plan))) await output.WriteLineAsync(line).ConfigureAwait(false);
            }
            catch (ServiceException exception)
            {
                await output.WriteLineAsync("Service: " + exception.Message).ConfigureAwait(false);
            }
        }
        else
        {
            await output.WriteLineAsync("Service: altanode install does not know the service manager of this OS.").ConfigureAwait(false);
        }

        return control.Status.IsHeld ? NodeExitCodes.Success : NodeExitCodes.Failure;
    }

    // The manager and the plan of the service the options name, or the exit code of what was reported instead.
    // The sandbox the sessions run in, and what to install when it cannot run: the owner sets it up from here.
    internal static string SandboxLine(Fleet.NodeApi.FleetNodeSandboxStatus sandbox)
        => !sandbox.Available
            ? $"Sandbox: cannot run ({sandbox.Backend}): {sandbox.Reason}" + (sandbox.Setup is { } setup ? $" To set it up: {setup}" : "")
                + (sandbox.Profile is { } named ? $" The sessions sandboxed with '{named}' run no command until then." : " Its sessions run without one until then.")
            : sandbox.Profile is { } profile ? $"Sandbox: its sessions run in the profile '{profile}' ({sandbox.Backend})."
            : "Sandbox: its sessions run without one ([sandbox] all_sessions = \"off\").";

    private async Task<(ServiceManager? Manager, ServicePlan? Plan, int Failure)> PlanAsync(NodeInstallOptions options, string command)
    {
        var run = options.Run;
        if (run.Profile.Resolve(out var invalid) is not { } profile) return (null, null, await UsageAsync(invalid!).ConfigureAwait(false));
        if (options.User && options.System) return (null, null, await UsageAsync("--user and --system cannot go together.").ConfigureAwait(false));
        // A Windows service runs as its own virtual account without one; launchd and systemd need the account.
        if (options.System && options.Account is null && !OperatingSystem.IsWindows())
            return (null, null, await UsageAsync("--system needs --account <name>, the account the node runs as.").ConfigureAwait(false));
        if (options.PasswordStdin && options.Account is null) return (null, null, await UsageAsync("--password-stdin goes with --account.").ConfigureAwait(false));
        if (!options.System && options.Account is not null) return (null, null, await UsageAsync("--account goes with --system only.").ConfigureAwait(false));
        if (NodeApplication.ResolveListen(run, out var listenError) is null) return (null, null, await UsageAsync(listenError!).ConfigureAwait(false));
        if (run.Name is { } name)
        {
            try
            {
                run.Name = TrustedDeviceStore.NormalizeName(name);
            }
            catch (ArgumentException exception)
            {
                return (null, null, await UsageAsync("--name: " + exception.Message).ConfigureAwait(false));
            }
        }

        if (environment() is not { } env)
        {
            await error.WriteLineAsync($"altanode {command}: this OS has no service manager altanode knows (Windows, macOS and Linux with systemd).").ConfigureAwait(false);
            return (null, null, NodeExitCodes.Failure);
        }

        if (executable() is not { } path)
        {
            await error.WriteLineAsync($"altanode {command}: the executable of this process cannot be found: run altanode from its installed path.").ConfigureAwait(false);
            return (null, null, NodeExitCodes.Failure);
        }

        var manager = new ServiceManager(env);
        try
        {
            var scope = options.System ? ServiceScope.System : ServiceScope.User;
            return (manager, manager.Plan(Request(profile, run.Profile, run, scope, options.Account, path)), NodeExitCodes.Success);
        }
        catch (ServiceException exception)
        {
            await error.WriteLineAsync($"altanode {command}: {exception.Message}").ConfigureAwait(false);
            return (null, null, NodeExitCodes.Failure);
        }
    }

    /// <summary>The service a profile and the options of its <c>run</c> ask for.</summary>
    internal static ServiceRequest Request(CodeAltaInstanceProfile profile, NodeProfileOptions profileOptions, NodeRunOptions run, ServiceScope scope,
        string? account, ServiceExecutable executable)
    {
        var name = ServiceDefinition.ServiceName(UpdateProduct.Node, profile.StateRoot, profileOptions.DataRoot is null, profileOptions.Developer);
        var arguments = new List<string> { "run" };
        if (profileOptions.Developer) arguments.Add("--dev");
        if (profileOptions.DataRoot is not null) arguments.AddRange(["--data-root", profile.GlobalRoot]);
        if (run.NoListen) arguments.Add("--no-listen");
        if (run.Port is { } port) arguments.AddRange(["--port", port]);
        if (run.Bind is { } bind) arguments.AddRange(["--bind", bind]);
        if (run.ListenAll) arguments.Add("--listen-all");
        if (run.Name is { } nodeName) arguments.AddRange(["--name", nodeName]);
        if (run.Offline) arguments.Add("--offline");
        if (run.NoPlugins) arguments.Add("--no-plugins");
        var system = scope == ServiceScope.System;
        return new ServiceRequest
        {
            Product = UpdateProduct.Node, Name = name, Scope = scope, Account = account, Executable = executable, RunArguments = arguments,
            StateRoot = !system || profileOptions.DataRoot is not null ? profile.StateRoot : null,
            StateRootInHome = profileOptions.Developer ? Path.Combine(".alta", CodeAltaInstanceProfile.DeveloperStateDirectoryName) : ".alta",
        };
    }

    private async Task WritePlanAsync(ServicePlan plan, bool install)
    {
        await output.WriteLineAsync($"altanode {(install ? "install" : "uninstall")} would {(install ? "install" : "remove")} {plan.Kind}: {plan.Location}.").ConfigureAwait(false);
        if (install)
        {
            await output.WriteLineAsync().ConfigureAwait(false);
            await output.WriteLineAsync($"{plan.DefinitionPath}{(plan.DefinitionIsTemporary ? " (given to the service manager, then deleted)" : "")}:").ConfigureAwait(false);
            await output.WriteLineAsync(plan.Definition.TrimEnd()).ConfigureAwait(false);
            await output.WriteLineAsync().ConfigureAwait(false);
        }

        await output.WriteLineAsync("Commands:").ConfigureAwait(false);
        foreach (var command in install ? plan.Install : plan.Uninstall) await output.WriteLineAsync("  " + command).ConfigureAwait(false);
        if (!install && !plan.DefinitionIsTemporary) await output.WriteLineAsync($"  (then deletes {plan.DefinitionPath})").ConfigureAwait(false);
        if (install && plan.LingerUser is { } user) await output.WriteLineAsync($"  loginctl enable-linger {user} (when lingering is off)").ConfigureAwait(false);
        foreach (var note in plan.Notes) await output.WriteLineAsync("Note: " + note).ConfigureAwait(false);
    }

    private async ValueTask<int> UsageAsync(string message)
    {
        await error.WriteLineAsync("altanode: " + message).ConfigureAwait(false);
        return NodeExitCodes.InvalidUsage;
    }
}
