using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CodeAlta.Desktop.Mcp;

/// <summary>This computer on its Tailscale network: the name clients reach it by, and its addresses there.</summary>
/// <param name="Name">Its MagicDNS name, or its IPv4 address on the tailnet when MagicDNS is off.</param>
/// <param name="Addresses">Its addresses on the tailnet, IPv4 first.</param>
internal sealed record DesktopTailnetNode(string Name, IReadOnlyList<IPAddress> Addresses);

/// <summary>
/// Whether the MCP server listens on the Tailscale network, and how it finds this computer there. The choice is the
/// user's; the server keeps it for the next starts through <paramref name="Remember"/>.
/// </summary>
/// <param name="Enabled">Whether the server listens on the tailnet when this computer is on one.</param>
/// <param name="Remember">Keeps the choice for the next starts.</param>
/// <param name="FindAsync">Finds this computer on its tailnet; null when Tailscale is not installed or not running.</param>
internal sealed record DesktopTailnetChoice(bool Enabled, Action<bool>? Remember, Func<CancellationToken, Task<DesktopTailnetNode?>> FindAsync);

/// <summary>
/// Asks the Tailscale command line of this computer where the computer is on its tailnet. Nothing is changed in
/// Tailscale: the server listens on the addresses of the tailnet itself.
/// </summary>
internal static class DesktopTailnet
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Finds this computer on its tailnet; null when Tailscale is not installed, not running or does not answer.</summary>
    /// <param name="cancellationToken">Stops the search.</param>
    internal static async Task<DesktopTailnetNode?> FindAsync(CancellationToken cancellationToken)
    {
        if (FindCli() is not { } cli) return null;
        var info = new ProcessStartInfo(cli) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("status");
        info.ArgumentList.Add("--json");
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            patience.CancelAfter(Patience);
            try
            {
                await process.WaitForExitAsync(patience.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* It ended meanwhile. */ }

                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }

            return Parse(await output.ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Reads the answer of <c>tailscale status --json</c>; null when Tailscale does not run or this computer has no address.</summary>
    /// <param name="json">What the command wrote.</param>
    internal static DesktopTailnetNode? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Self", out var self) || self.ValueKind != JsonValueKind.Object) return null;
            if (!string.Equals(Text(root, "BackendState"), "Running", StringComparison.OrdinalIgnoreCase)) return null;
            IPAddress[] addresses = self.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array
                ? [.. ips.EnumerateArray().Select(static ip => ip.ValueKind == JsonValueKind.String && IPAddress.TryParse(ip.GetString(), out var address) ? address : null)
                    .OfType<IPAddress>().Distinct().OrderBy(static address => address.AddressFamily != AddressFamily.InterNetwork)]
                : [];
            if (addresses.Length == 0) return null;
            // Without MagicDNS the computer has no name on the tailnet: clients reach it by its address.
            var name = Text(self, "DNSName")?.TrimEnd('.');
            return new DesktopTailnetNode(string.IsNullOrEmpty(name) ? addresses[0].ToString() : name, addresses);
        }
        catch (JsonException)
        {
            // It answers with a line when its service does not run.
            return null;
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // On the path, or where its installer puts it.
    private static string? FindCli()
    {
        var name = OperatingSystem.IsWindows() ? "tailscale.exe" : "tailscale";
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A folder of the path that is no path.
            }
        }

        string[] usual = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe")]
            : OperatingSystem.IsMacOS()
                ? ["/Applications/Tailscale.app/Contents/MacOS/Tailscale", "/usr/local/bin/tailscale", "/opt/homebrew/bin/tailscale"]
                : ["/usr/bin/tailscale", "/usr/local/bin/tailscale"];
        return usual.FirstOrDefault(File.Exists);
    }
}
