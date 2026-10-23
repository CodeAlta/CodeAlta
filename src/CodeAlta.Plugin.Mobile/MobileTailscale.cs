using System.Diagnostics;
using System.Text.Json;

namespace CodeAlta.Plugin.Mobile;

/// <summary>What the plugin knows of Tailscale on this computer.</summary>
/// <param name="State">installed-not-found, not-running, serving, failed.</param>
/// <param name="Url">The https URL of the node on the tailnet, when it serves.</param>
/// <param name="Detail">A line for people.</param>
internal sealed record MobileTailscaleStatus(string State, string? Url, string Detail)
{
    internal static MobileTailscaleStatus Pending { get; } = new("pending", null, "Checking Tailscale…");
}

/// <summary>Finds the Tailscale CLI and asks it to serve the local port on the tailnet.</summary>
internal static class MobileTailscale
{
    internal static string? FindCli()
    {
        var name = OperatingSystem.IsWindows() ? "tailscale.exe" : "tailscale";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }

        string[] defaults = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe")]
            : OperatingSystem.IsMacOS()
                ? ["/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/usr/local/bin/tailscale", "/opt/homebrew/bin/tailscale"]
                : ["/usr/bin/tailscale", "/usr/local/bin/tailscale"];
        return defaults.FirstOrDefault(File.Exists);
    }

    internal static async Task<MobileTailscaleStatus> ServeAsync(int port, CancellationToken cancellationToken)
    {
        // CODEALTA_MOBILE_SIMULATE_TAILSCALE=missing|serve-fails tests the checks of the pairing page without touching
        // the real Tailscale: "missing" acts as if the CLI were not there, "serve-fails" stops before `tailscale serve`.
        var simulate = Environment.GetEnvironmentVariable("CODEALTA_MOBILE_SIMULATE_TAILSCALE");
        var cli = simulate == "missing" ? null : FindCli();
        if (cli is null) return new("not-installed", null, "Tailscale is not installed: the server is reachable on this computer only.");

        var (code, output) = await RunAsync(cli, ["status", "--json"], cancellationToken).ConfigureAwait(false);
        if (code != 0) return new("not-running", null, $"`tailscale status` failed ({code}): {FirstLine(output)}");

        string? dnsName = null;
        string? backend = null;
        try
        {
            using var json = JsonDocument.Parse(output);
            if (json.RootElement.TryGetProperty("BackendState", out var state)) backend = state.GetString();
            if (json.RootElement.TryGetProperty("Self", out var self) && self.TryGetProperty("DNSName", out var dns)) dnsName = dns.GetString()?.TrimEnd('.');
        }
        catch (JsonException exception)
        {
            return new("failed", null, $"`tailscale status --json` is not JSON: {exception.Message}");
        }

        if (!string.Equals(backend, "Running", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(dnsName))
            return new("not-running", null, $"Tailscale is not logged in or not running (state {backend ?? "unknown"}).");

        if (simulate == "serve-fails") return new("serve-failed", null, $"`tailscale serve --bg {port}` failed (simulated).");
        (code, output) = await RunAsync(cli, ["serve", "--bg", port.ToString(System.Globalization.CultureInfo.InvariantCulture)], cancellationToken).ConfigureAwait(false);
        if (output.Contains("Serve is not enabled", StringComparison.OrdinalIgnoreCase))
        {
            var link = output.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(static word => word.StartsWith("https://", StringComparison.Ordinal));
            return new("serve-disabled", null, $"Tailscale Serve is not enabled on the tailnet (node {dnsName}). An admin enables it at {link ?? "the Tailscale admin console"}, then restart the plugin.");
        }

        if (code != 0) return new("serve-failed", null, $"`tailscale serve --bg {port}` {(code == -1 ? "timed out" : $"failed ({code})")}: {FirstLine(output)}");
        return new("serving", $"https://{dnsName}", $"Tailscale serves https://{dnsName} → 127.0.0.1:{port}");
    }

    /// <summary>
    /// Takes the mobile view off the tailnet when the plugin stops (turned off, or the application exits): the HTTPS
    /// handler <c>tailscale serve --bg {port}</c> added (port 443, path /) is removed with <c>tailscale serve --https=443 off</c>.
    /// </summary>
    internal static async Task<string> UnserveAsync()
    {
        if (FindCli() is not { } cli) return "Tailscale is not installed.";
        try
        {
            var (code, output) = await RunAsync(cli, ["serve", "--https=443", "off"], CancellationToken.None).ConfigureAwait(false);
            return code == 0 ? "Tailscale Serve stopped." : $"`tailscale serve --https=443 off` failed ({code}): {FirstLine(output)}";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return "Tailscale could not be asked: " + exception.Message;
        }
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    private static async Task<(int Code, string Output)> RunAsync(string cli, string[] args, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(cli) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("tailscale did not start.");
        // The streams are read to their end whatever happens: a CLI that waits (Serve not enabled on the tailnet waits for
        // the admin to approve) is killed after the timeout, and what it printed still tells why.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            timedOut = true;
        }

        var text = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        return (timedOut ? -1 : process.ExitCode, text);
    }
}
