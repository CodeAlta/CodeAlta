using System.Diagnostics;

namespace CodeAlta.Sandbox.Mxc;

/// <summary>
/// Whether a sandbox with a network can start on Linux. Every network of a sandbox goes through the proxy of CodeAlta
/// (<see cref="SandboxPolicy.ThroughProxy"/>), which mxc 1.0.1 enforces from user and network namespaces made by util-linux
/// <c>unshare</c>; it checks them first with <see cref="ProbeArguments"/>. Ubuntu 24.04 and later refuse those namespaces
/// to a program without an AppArmor profile that allows them (<c>kernel.apparmor_restrict_unprivileged_userns</c>), as
/// they do for <c>bwrap</c>: the sandbox then starts without a network only.
/// </summary>
internal static class LinuxNetworkNamespaces
{
    private static readonly TimeSpan ProbeTime = TimeSpan.FromSeconds(10);

    /// <summary>The pre-flight check of mxc, which must succeed for a sandbox with a network to start.</summary>
    internal static readonly string[] ProbeArguments = ["--user", "--map-current-user", "--keep-caps", "--net", "sh", "-c", ":"];

    /// <summary>Runs the check of mxc on this computer.</summary>
    /// <param name="onPath">Finds a program on <c>PATH</c>, or null.</param>
    /// <returns>Why a sandbox with a network cannot start here, and what to do; null when it can.</returns>
    internal static (string Reason, string Setup)? Check(Func<string, string?> onPath)
    {
        if (onPath("unshare") is not { } unshare)
            return ("util-linux `unshare` is not on PATH: a sandbox with a network cannot start.", "Install the `util-linux` package.");
        string error;
        try
        {
            var start = new ProcessStartInfo(unshare) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var argument in ProbeArguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return Refused(RealPath(unshare), null);
            var read = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ProbeTime))
            {
                process.Kill(entireProcessTree: true);
                return Refused(RealPath(unshare), $"no answer in {ProbeTime.TotalSeconds:0} s");
            }

            if (process.ExitCode == 0) return null;
            error = read.Wait(ProbeTime) ? read.Result.Trim() : "";
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            error = exception.Message;
        }

        return Refused(RealPath(unshare), error);
    }

    /// <summary>Says why a sandbox with a network cannot start, and the commands that let <c>unshare</c> make its namespaces.</summary>
    /// <param name="unshare">The path of <c>unshare</c>, its links followed: the path an AppArmor profile names.</param>
    /// <param name="error">What <c>unshare</c> wrote, or null.</param>
    /// <returns>The reason and the setup.</returns>
    internal static (string Reason, string Setup) Refused(string unshare, string? error)
        => ("This system refuses the user and network namespaces that filter the network of a sandbox"
                + (string.IsNullOrWhiteSpace(error) ? "" : $" (`unshare`: {Shorten(error)})") + ": a sandbox with a network cannot start.",
            "On Ubuntu 24.04 and later, give `unshare` an AppArmor profile that allows user namespaces, as for `bwrap`: "
                + $@"printf 'abi <abi/4.0>,\ninclude <tunables/global>\n\nprofile unshare {unshare} flags=(unconfined) {{\n  userns,\n\n  include if exists <local/unshare>\n}}\n' | sudo tee /etc/apparmor.d/unshare > /dev/null && sudo apparmor_parser -r /etc/apparmor.d/unshare"
                + " - then restart CodeAlta, or the service of the node (`systemctl --user restart altanode`).");

    private static string RealPath(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return path;
        }
    }

    private static string Shorten(string error)
    {
        var line = error.ReplaceLineEndings(" ").Trim();
        return line.Length <= 160 ? line : line[..160] + "…";
    }
}
