using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeAlta.Browser.Launch;

/// <summary>A Firefox CodeAlta started, with the address of its WebDriver BiDi endpoint.</summary>
/// <param name="Process">The process.</param>
/// <param name="Endpoint">The address of the BiDi endpoint: <c>ws://127.0.0.1:&lt;port&gt;/session</c>.</param>
internal sealed record FirefoxLaunch(BrowserProcess Process, Uri Endpoint);

/// <summary>
/// Starts Firefox with its own WebDriver BiDi endpoint (<c>--remote-debugging-port</c>), without geckodriver, on a
/// port of the loopback Firefox picks and announces on its standard error: a port picked here could be taken before
/// Firefox binds it.
/// </summary>
internal static partial class FirefoxLauncher
{
    // The preferences Puppeteer gives a profile for automation: no update, no first-run pages, no telemetry, no
    // calls of its own to Mozilla's services.
    private static readonly ImmutableArray<(string Name, string Value)> Preferences =
    [
        ("app.update.disabledForTesting", "true"),
        ("browser.shell.checkDefaultBrowser", "false"),
        ("browser.startup.homepage_override.mstone", "\"ignore\""),
        ("browser.startup.page", "0"),
        ("browser.startup.homepage", "\"about:blank\""),
        ("startup.homepage_welcome_url", "\"about:blank\""),
        ("browser.aboutwelcome.enabled", "false"),
        ("browser.newtabpage.enabled", "false"),
        ("browser.tabs.warnOnClose", "false"),
        ("browser.warnOnQuit", "false"),
        ("datareporting.policy.dataSubmissionEnabled", "false"),
        ("datareporting.healthreport.uploadEnabled", "false"),
        ("toolkit.telemetry.reportingpolicy.firstRun", "false"),
        ("toolkit.telemetry.enabled", "false"),
        ("remote.bidi.dismiss_file_pickers.enabled", "true"),
        ("services.settings.server", "\"data:,#remote-settings-dummy/v1\""),
        ("extensions.update.enabled", "false"),
        ("browser.safebrowsing.malware.enabled", "false"),
        ("browser.safebrowsing.phishing.enabled", "false"),
        // A proxy, of the browser or of a user context, also gets the addresses of the loopback: the proxy of a sandbox
        // decides which local ports a session reaches.
        ("network.proxy.allow_hijacking_localhost", "true"),
    ];

    [GeneratedRegex(@"WebDriver BiDi listening on (ws://[^\s]+)")]
    private static partial Regex Listening();

    /// <summary>The <c>user.js</c> of a profile of CodeAlta's: the preferences of automation, and the proxy when there is one.</summary>
    public static string UserJs(BrowserLaunchOptions options)
    {
        var builder = new StringBuilder("// Written by CodeAlta for automation.\n");
        foreach (var (name, value) in Preferences) builder.Append("user_pref(\"").Append(name).Append("\", ").Append(value).Append(");\n");
        if (options.Proxy is { } proxy && proxy.LastIndexOf(':') is var colon and > 0)
        {
            var host = proxy[..colon];
            var port = proxy[(colon + 1)..];
            builder.Append("user_pref(\"network.proxy.type\", 1);\n");
            foreach (var scheme in new[] { "http", "ssl" })
            {
                builder.Append("user_pref(\"network.proxy.").Append(scheme).Append("\", \"").Append(host).Append("\");\n");
                builder.Append("user_pref(\"network.proxy.").Append(scheme).Append("_port\", ").Append(port).Append(");\n");
            }

            // Local addresses go through the proxy too, which decides what a sandbox reaches.
            builder.Append("user_pref(\"network.proxy.no_proxies_on\", \"\");\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Whether a Firefox runs on a profile: on Windows it holds <c>parent.lock</c> open, elsewhere it leaves a <c>lock</c>
    /// link, which a crash can leave behind, so a Firefox must also run.
    /// </summary>
    /// <param name="profileDirectory">The folder of the profile.</param>
    /// <param name="firefoxRuns">Whether a program named firefox runs.</param>
    public static bool ProfileInUse(string profileDirectory, Func<bool> firefoxRuns)
    {
        ArgumentNullException.ThrowIfNull(firefoxRuns);
        if (OperatingSystem.IsWindows())
        {
            var file = Path.Combine(profileDirectory, "parent.lock");
            if (!File.Exists(file)) return false;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }

        var link = new FileInfo(Path.Combine(profileDirectory, "lock"));
        return (link.Exists || link.LinkTarget is not null) && firefoxRuns();
    }

    /// <summary>The arguments of a start on a profile, with the endpoint on a port.</summary>
    public static IReadOnlyList<string> Arguments(BrowserLaunchOptions options, int port)
    {
        var arguments = new List<string> { "-profile", options.ProfileDirectory, "-no-remote", "-wait-for-browser", "--remote-debugging-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (options.Headless) arguments.Add("-headless");
        arguments.Add("about:blank");
        return arguments;
    }

    /// <summary>Starts Firefox and waits for its endpoint.</summary>
    /// <param name="options">How to start it.</param>
    /// <param name="writePreferences">Whether to write the <c>user.js</c> of automation: never in the user's own profile.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="BrowserLaunchException">Firefox did not start, or exited before it opened its endpoint.</exception>
    public static async Task<FirefoxLaunch> LaunchAsync(BrowserLaunchOptions options, bool writePreferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.ProfileDirectory);
        if (writePreferences) await File.WriteAllTextAsync(Path.Combine(options.ProfileDirectory, "user.js"), UserJs(options), cancellationToken).ConfigureAwait(false);
        const int port = 0;
        var announced = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["MOZ_CRASHREPORTER_DISABLE"] = "1" };
        if (options.Environment is not null) foreach (var (name, value) in options.Environment) environment[name] = value;
        var process = BrowserProcess.Start(options.Executable, Arguments(options, port), options.Temporary ? options.ProfileDirectory : null, environment);
        process.Line += line =>
        {
            if (Listening().Match(line) is { Success: true } match && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var address)) announced.TrySetResult(address);
        };
        try
        {
            var deadline = DateTime.UtcNow + options.StartTimeout;
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (announced.Task.IsCompletedSuccessfully)
                {
                    var address = announced.Task.Result;
                    return new FirefoxLaunch(process, new Uri(address, address.AbsolutePath.EndsWith("/session", StringComparison.Ordinal) ? string.Empty : "/session"));
                }

                if (process.HasExited)
                {
                    throw new BrowserLaunchException("Firefox exited before it opened its remote endpoint. When its profile is open in another Firefox window, close Firefox first." +
                        (process.Output() is { Length: > 0 } output ? $" It wrote:\n{output}" : string.Empty));
                }

                await Task.WhenAny(announced.Task, process.Exited, Task.Delay(100, cancellationToken)).ConfigureAwait(false);
            }

            throw new BrowserLaunchException($"Firefox did not open its remote endpoint within {options.StartTimeout.TotalSeconds:0} s.");
        }
        catch
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
