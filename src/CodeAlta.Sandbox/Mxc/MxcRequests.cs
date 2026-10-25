using Microsoft.Mxc.Sdk.V1;

namespace CodeAlta.Sandbox.Mxc;

/// <summary>The mxc request of a policy.</summary>
internal static class MxcRequests
{
    // The most entries denied beside the parent folders of the grants: a folder with more is left out of the chain.
    private const int MaximumChainEntries = 20_000;

    public static ContainerRequest Create(SandboxPolicy policy, string commandLine, string? proxyEndpoint)
    {
        var filesystem = new FilesystemPolicy();
        foreach (var path in policy.ReadWrite) filesystem.ReadwritePaths.Add(path);
        foreach (var path in policy.ReadOnly.Where(static path => Directory.Exists(path) || File.Exists(path))) filesystem.ReadonlyPaths.Add(path);
        // A denied path needs its parent folder (the container refuses a path it cannot find); one without it cannot
        // be created by the sandbox either, unless inside a folder it writes, where Prepare made the parent.
        foreach (var path in policy.Denied.Where(Reachable)) filesystem.DeniedPaths.Add(path);
        // An entry that exists is read-only; one that does not is denied, which keeps it from being created
        // without creating it (a read-only grant of a missing path protects nothing).
        foreach (var path in policy.Protected)
        {
            if (Directory.Exists(path) || File.Exists(path)) filesystem.ReadonlyPaths.Add(path);
            else if (Reachable(path)) filesystem.DeniedPaths.Add(path);
        }

        if (OperatingSystem.IsWindows()) AddParentChain(filesystem);

        return new ContainerRequest(commandLine)
        {
            Containment = OperatingSystem.IsWindows() ? new Containment.ProcessContainer()
                : OperatingSystem.IsMacOS() ? new Containment.Seatbelt()
                : new Containment.Bubblewrap(),
            WorkingDirectory = policy.WorkingDirectory,
            Environment = new Dictionary<string, string>(policy.Environment),
            Filesystem = filesystem,
            Network = Network(policy, proxyEndpoint),
            // Disabled UI stops the processes of tools at start (win32k lockdown); the clipboard stays closed.
            Ui = new UiPolicy { Disable = false, Clipboard = ClipboardPolicy.None, AllowInputInjection = false },
        };
    }

    // Tools resolve a path one folder at a time from the volume root (git, node) and fail on a folder that is not
    // granted. A read-only grant cascades to everything below it, so each parent folder of a read-write grant is
    // granted read-only with every other entry denied; the volume root goes in the read-write list, where a root does
    // not cascade and cannot be written. A file the system holds open (NTUSER.DAT) cannot be denied, and nobody else
    // can read it: it is left out.
    internal static void AddParentChain(FilesystemPolicy filesystem)
    {
        var comparer = SandboxPolicyBuilder.PathComparer;
        var grants = new HashSet<string>(filesystem.ReadwritePaths.Concat(filesystem.ReadonlyPaths).Select(static path => Path.TrimEndingDirectorySeparator(path)), comparer);
        var chain = new HashSet<string>(comparer);
        var roots = new HashSet<string>(comparer);
        void AddParents(string path)
        {
            for (var folder = Directory.GetParent(path); folder is not null; folder = folder.Parent)
            {
                if (folder.Parent is null) roots.Add(folder.FullName);
                else chain.Add(Path.TrimEndingDirectorySeparator(folder.FullName));
            }
        }

        foreach (var grant in filesystem.ReadwritePaths.ToList()) AddParents(grant);
        // A read-only grant below a folder of the chain (a tool folder under the home folder) chains its own parents
        // too: otherwise its parent (~/.cargo) would stay readable whole, with what it holds beside the grant.
        foreach (var grant in filesystem.ReadonlyPaths.ToList())
        {
            if (chain.Any(folder => !comparer.Equals(folder, grant) && SandboxPolicyBuilder.IsInside(folder, grant))) AddParents(grant);
        }

        foreach (var root in roots.Where(root => !filesystem.ReadwritePaths.Contains(root, comparer))) filesystem.ReadwritePaths.Add(root);
        var denied = 0;
        foreach (var folder in chain.Where(folder => !grants.Any(grant => SandboxPolicyBuilder.IsInside(grant, folder))).Order(comparer))
        {
            List<string> entries;
            try
            {
                entries = [.. Directory.EnumerateFileSystemEntries(folder)];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (denied + entries.Count > MaximumChainEntries) continue;
            filesystem.ReadonlyPaths.Add(folder);
            foreach (var entry in entries)
            {
                if (chain.Contains(entry) || grants.Contains(entry)) continue;
                if (File.Exists(entry) && IsHeldOpen(entry)) continue;
                filesystem.DeniedPaths.Add(entry);
                denied++;
            }
        }
    }

    private static NetworkPolicy Network(SandboxPolicy policy, string? proxyEndpoint) => policy.Network switch
    {
        // Through the proxy alone: mxc lets the container reach that loopback port and nothing else.
        SandboxNetwork.Hosts => new NetworkPolicy
        {
            Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny },
            Ingress = new NetworkIngressPolicy { Default = NetworkAction.Allow, HostLoopback = NetworkAction.Allow },
            RuntimeConfig = new NetworkRuntimeConfig { NetworkProxy = proxyEndpoint },
        },
        SandboxNetwork.On => new NetworkPolicy
        {
            Egress = new NetworkEgressPolicy { Default = NetworkAction.Allow },
            Ingress = new NetworkIngressPolicy { Default = NetworkAction.Allow, HostLoopback = NetworkAction.Deny },
        },
        // No network; on Windows the loopback of the container is allowed, so that its own programs talk to each
        // other (a test host and its runner), while the services of the host stay out of reach. Elsewhere the
        // container has a network namespace of its own, with its own loopback.
        _ => OperatingSystem.IsWindows()
            ? new NetworkPolicy
            {
                Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny, Allow = [new NetworkRulePolicy { To = [new NetworkPeerPolicy("127.0.0.0/8")] }] },
                Ingress = new NetworkIngressPolicy { Default = NetworkAction.Allow, HostLoopback = NetworkAction.Deny },
            }
            : new NetworkPolicy { Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny } },
    };

    /// <summary>
    /// Makes the parent folders of the protected entries that are missing inside the writable folders: empty
    /// folders, which git does not track (an empty <c>.alta</c> lets <c>.alta/plans</c> be created while
    /// <c>.alta/plugins</c> stays denied).
    /// </summary>
    public static void Prepare(SandboxPolicy policy)
    {
        foreach (var folder in policy.ReadWrite) Directory.CreateDirectory(folder);
        foreach (var path in policy.Protected)
        {
            if (Directory.Exists(path) || File.Exists(path) || Path.GetDirectoryName(path) is not { } parent || Directory.Exists(parent)) continue;
            if (policy.ReadWrite.Any(root => SandboxPolicyBuilder.IsInside(root, parent))) Directory.CreateDirectory(parent);
        }
    }

    private static bool Reachable(string path) => Directory.Exists(path) || File.Exists(path) || Directory.Exists(Path.GetDirectoryName(path));

    private static bool IsHeldOpen(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
