namespace CodeAlta.Browser.Launch;

/// <summary>A Chromium browser CodeAlta started, with the address of its DevTools endpoint.</summary>
/// <param name="Process">The process.</param>
/// <param name="Endpoint">The WebSocket address of the browser target.</param>
internal sealed record ChromiumLaunch(BrowserProcess Process, Uri Endpoint);

/// <summary>
/// Starts a Chromium browser (Chrome, Edge, Brave, Vivaldi, Chromium) on a profile folder of CodeAlta's with its
/// DevTools endpoint on a port of the loopback that the browser picks, and reads that port from the
/// <c>DevToolsActivePort</c> file it writes in the profile.
/// </summary>
internal static class ChromiumLauncher
{
    /// <summary>The arguments of a start. The flags are the ones Puppeteer starts Chrome with for automation.</summary>
    public static IReadOnlyList<string> Arguments(BrowserLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var arguments = new List<string>
        {
            $"--user-data-dir={options.ProfileDirectory}",
            "--remote-debugging-port=0",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-sync",
            "--disable-search-engine-choice-screen",
            "--disable-background-networking",
            "--disable-client-side-phishing-detection",
            "--disable-default-apps",
            "--disable-hang-monitor",
            "--disable-prompt-on-repost",
            "--disable-features=Translate,OptimizationHints,MediaRouter,DialMediaRouteProvider",
            "--hide-crash-restore-bubble",
            "--enable-automation",
            "--metrics-recording-only",
            // Every connection goes through the proxy of its context when it has one: WebRTC sends no UDP of its own, and no
            // name is resolved ahead of a request, so that a sandboxed session's pages reach the network of its sandbox only.
            "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
            "--dns-prefetch-disable",
        };
        // A temporary profile keeps no secret worth the system keychain; a kept one, where the user signs in, uses it.
        if (options.Temporary) arguments.AddRange(["--password-store=basic", "--use-mock-keychain"]);
        if (options.Headless) arguments.AddRange(["--headless", "--hide-scrollbars", "--mute-audio"]);
        if (options.Proxy is { } proxy)
        {
            // <-loopback> removes the browser's own exception for the loopback, so that local addresses are proxied too.
            arguments.Add($"--proxy-server=http://{proxy}");
            arguments.Add("--proxy-bypass-list=<-loopback>");
        }

        arguments.Add("about:blank");
        return arguments;
    }

    /// <summary>Starts the browser and waits for its endpoint.</summary>
    /// <exception cref="BrowserLaunchException">The browser did not start, or exited before it opened its endpoint.</exception>
    public static async Task<ChromiumLaunch> LaunchAsync(BrowserLaunchOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.ProfileDirectory);
        var portFile = Path.Combine(options.ProfileDirectory, "DevToolsActivePort");
        // A file left by an earlier run would give the port of a browser that is gone.
        TryDelete(portFile);
        var process = BrowserProcess.Start(options.Executable, Arguments(options), options.Temporary ? options.ProfileDirectory : null, options.Environment);
        try
        {
            var deadline = DateTime.UtcNow + options.StartTimeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ReadPortFile(portFile) is { } endpoint) return new ChromiumLaunch(process, endpoint);
                if (process.HasExited)
                {
                    throw new BrowserLaunchException(
                        $"{Path.GetFileName(options.Executable)} exited before it opened its DevTools endpoint. When its profile is open in another window of the browser, close that window first."
                        + Tail(process));
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            throw new BrowserLaunchException($"{Path.GetFileName(options.Executable)} did not open its DevTools endpoint within {options.StartTimeout.TotalSeconds:0} s." + Tail(process));
        }
        catch
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reads <c>DevToolsActivePort</c>: the port on its first line, the path of the browser target on its second.</summary>
    /// <returns>The address, or null while the file is missing or not complete.</returns>
    internal static Uri? ReadPortFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length < 2 || !int.TryParse(lines[0], out var port) || port is <= 0 or > 65535 || !lines[1].StartsWith('/')) return null;
            return new Uri($"ws://127.0.0.1:{port}{lines[1]}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Tail(BrowserProcess process) => process.Output() is { Length: > 0 } output ? $" It wrote:\n{output}" : string.Empty;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
