using Microsoft.Mxc.Sdk.V1;

namespace CodeAlta.Sandbox.Mxc;

/// <summary>The mxc request of a policy.</summary>
internal static class MxcRequests
{
    // The most entries denied beside the parent folders of the grants: a folder with more is left out of the chain.
    private const int MaximumChainEntries = 20_000;

    // The services of macOS that tools need beside those of mxc's baseline:
    // - the names of users and groups (getpwuid, the groups of a file), which .NET asks when it writes an apphost;
    // - the check of a certificate: .NET validates TLS with Security.framework, which needs securityd and trustd
    //   (NuGet fails with "bad certificate format" without them). The keychains stay out of reach: their files are
    //   not granted, and mxc's `keychainAccess`, which grants them, is never set.
    private static readonly string[] SeatbeltMachServices =
        ["com.apple.system.opendirectoryd.libinfo", "com.apple.SecurityServer", "com.apple.trustd", "com.apple.trustd.agent"];

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

        if (OperatingSystem.IsWindows()) AddParentChain(filesystem, grantParents: true);
        else if (OperatingSystem.IsMacOS()) AddParentChain(filesystem, grantParents: false, policy.DataRoot, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        return new ContainerRequest(commandLine)
        {
            Containment = OperatingSystem.IsWindows() ? new Containment.ProcessContainer()
                : OperatingSystem.IsMacOS() ? new Containment.Seatbelt { ExtraMachLookups = [.. SeatbeltMachServices] }
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
    // On macOS (grantParents false) the parents are not granted: Seatbelt lets every path be looked up, but shows the
    // metadata of every file, so a file in a parent folder (.editorconfig, Directory.Build.props) seems there and
    // cannot be opened, and builds fail. Its other entries are denied with their metadata, so that they are not there,
    // as on Windows and Linux. A link is left out: mxc denies what a denied link names, which may be a grant. The
    // grants in the data folder of the sandboxes (`unchained`) are left out too: tools walk up from the project, and
    // mxc takes about half a second more to start with the hundreds of entries of `~/Library` and its caches denied.
    // Only the folders inside `within` (the home folder) deny their entries: those of `/private` are the system's own.
    internal static void AddParentChain(FilesystemPolicy filesystem, bool grantParents, string? unchained = null, string? within = null)
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

        foreach (var grant in filesystem.ReadwritePaths.Where(grant => unchained is null || !SandboxPolicyBuilder.IsInside(unchained, grant)).ToList()) AddParents(grant);
        // A read-only grant below a folder of the chain (a tool folder under the home folder) chains its own parents
        // too: otherwise its parent (~/.cargo) would stay readable whole, with what it holds beside the grant.
        foreach (var grant in filesystem.ReadonlyPaths.ToList())
        {
            if (chain.Any(folder => !comparer.Equals(folder, grant) && SandboxPolicyBuilder.IsInside(folder, grant))) AddParents(grant);
        }

        if (grantParents)
        {
            foreach (var root in roots.Where(root => !filesystem.ReadwritePaths.Contains(root, comparer))) filesystem.ReadwritePaths.Add(root);
        }

        var denied = 0;
        foreach (var folder in chain.Where(folder => !grants.Any(grant => SandboxPolicyBuilder.IsInside(grant, folder))
                     && (within is null || SandboxPolicyBuilder.IsInside(within, folder))).Order(comparer))
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
            if (grantParents) filesystem.ReadonlyPaths.Add(folder);
            foreach (var entry in entries)
            {
                // An entry that leads to a grant stays (the data folder of the sandboxes, when it is not chained).
                if (chain.Contains(entry) || grants.Any(grant => SandboxPolicyBuilder.IsInside(entry, grant))) continue;
                if (grantParents ? File.Exists(entry) && IsHeldOpen(entry) : IsLink(entry)) continue;
                filesystem.DeniedPaths.Add(entry);
                denied++;
            }
        }
    }

    // The addresses of the networks of this computer and of the cloud it may run in (private, carrier-grade, link-local
    // with its metadata service, multicast and reserved), which a network that is on never reaches. The loopback of the
    // container stays its own: that of the host is closed by the ingress policy.
    private static readonly string[] LocalNetworks =
    [
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "169.254.0.0/16", "172.16.0.0/12", "192.168.0.0/16", "224.0.0.0/4", "240.0.0.0/4",
        "fc00::/7", "fe80::/10", "ff00::/8",
    ];

    private static NetworkEgressPolicy InternetOnly() => new()
    {
        Default = NetworkAction.Allow,
        Deny = [new NetworkRulePolicy { To = [.. LocalNetworks.Select(static cidr => new NetworkPeerPolicy(cidr))] }],
    };

    private static NetworkPolicy Network(SandboxPolicy policy, string? proxyEndpoint) => OperatingSystem.IsWindows() ? WindowsNetwork(policy, proxyEndpoint) : policy.ThroughProxy
        // Nothing comes in, and the loopback of this computer stays closed: Seatbelt opens every port of this computer
        // for an allowed host loopback (only the port of the proxy is opened then), and Bubblewrap refuses ingress. A
        // network that is on goes through the proxy too, with every host (SandboxPolicy.ThroughProxy).
        ? new NetworkPolicy
        {
            Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny },
            Ingress = new NetworkIngressPolicy { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny },
            RuntimeConfig = new NetworkRuntimeConfig { NetworkProxy = proxyEndpoint },
        }
        // The container has a network namespace of its own, with its own loopback.
        : new NetworkPolicy { Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny } };

    private static NetworkPolicy WindowsNetwork(SandboxPolicy policy, string? proxyEndpoint) => policy.Network switch
    {
        // Through the proxy alone: the process container lets the container reach that loopback port and nothing else
        // (its proxy without a peer identity needs host loopback and ingress allowed).
        SandboxNetwork.Hosts => new NetworkPolicy
        {
            Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny },
            Ingress = new NetworkIngressPolicy { Default = NetworkAction.Allow, HostLoopback = NetworkAction.Allow },
            RuntimeConfig = new NetworkRuntimeConfig { NetworkProxy = proxyEndpoint },
        },
        SandboxNetwork.On => new NetworkPolicy
        {
            Egress = InternetOnly(),
            Ingress = new NetworkIngressPolicy { Default = NetworkAction.Allow, HostLoopback = NetworkAction.Deny },
        },
        // No network; the loopback of the container is allowed, so that its own programs talk to each other (a test
        // host and its runner), while the services of the host stay out of reach.
        _ => new NetworkPolicy
        {
            Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny, Allow = [new NetworkRulePolicy { To = [new NetworkPeerPolicy("127.0.0.0/8")] }] },
            Ingress = new NetworkIngressPolicy { Default = NetworkAction.Allow, HostLoopback = NetworkAction.Deny },
        },
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

    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

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
