using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeAlta.Agent.OpenAI.Codex;

/// <summary>
/// Reads what a ChatGPT plan has left for Codex (its windows of five hours and of a week, its other limits and its
/// credits) by asking the Codex CLI of OpenAI, which is the one that is given them.
/// </summary>
/// <remarks>
/// <para>
/// The sign-in of CodeAlta is not given the usage of the plan: the answers to its turns carry no limit, and the
/// service refuses the usage to its token. The Codex CLI has it and answers other programs through its app server
/// (<c>codex app-server</c>, the request <c>account/rateLimits/read</c>). A short-lived process answers and exits;
/// no model is called, and CodeAlta never reads the credentials of the CLI.
/// </para>
/// <para>
/// Without the CLI on the machine, or when it is signed in with another account than CodeAlta, there is no usage to
/// show.
/// </para>
/// </remarks>
public static class CodexAccountUsage
{
    private const int MaximumLimits = 16;
    private const int MaximumLineLength = 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

    /// <summary>Starts the Codex CLI, asks it for the usage of its account, and stops it.</summary>
    /// <param name="expectedAccount">The e-mail address of the account CodeAlta is signed in with, when it is known: another account is not shown.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <param name="command">The path of the CLI, or null to look for <c>codex</c> on the machine.</param>
    /// <returns>
    /// The usage; or that the CLI is missing, that it is not signed in with a ChatGPT plan of the same account, or
    /// that this account has no usage to show.
    /// </returns>
    /// <exception cref="InvalidOperationException">The CLI did not answer.</exception>
    /// <exception cref="OperationCanceledException">The reading was canceled.</exception>
    public static async Task<AgentSubscriptionUsageReading> ReadAsync(string? expectedAccount, CancellationToken cancellationToken, string? command = null)
    {
        if (ResolveCli(command) is not { } cli)
        {
            return AgentSubscriptionUsageReading.NoTool;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        Process? process = null;
        try
        {
            process = Process.Start(CreateStartInfo(cli)) ?? throw new InvalidOperationException($"The Codex CLI at '{cli}' did not start.");
            // What the CLI logs is not read: left unread, it would fill the pipe and stop the CLI.
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
            var input = process.StandardInput;
            var output = process.StandardOutput;
            return await ExchangeAsync(
                async (line, token) =>
                {
                    await input.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
                    await input.FlushAsync(token).ConfigureAwait(false);
                },
                token => output.ReadLineAsync(token),
                expectedAccount,
                DateTimeOffset.UtcNow,
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"The Codex CLI at '{cli}' did not answer in time.", exception);
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException($"The Codex CLI at '{cli}' did not answer.", exception);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // The process ended on its own.
                }

                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Speaks to an app server over its lines of JSON: the handshake, the account, then the limits of the account.
    /// </summary>
    internal static async Task<AgentSubscriptionUsageReading> ExchangeAsync(
        Func<string, CancellationToken, ValueTask> send,
        Func<CancellationToken, ValueTask<string?>> receive,
        string? expectedAccount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await send("""{"id":1,"method":"initialize","params":{"clientInfo":{"name":"codealta","title":"CodeAlta","version":"1"}}}""", cancellationToken).ConfigureAwait(false);
        if (await AnswerAsync(receive, 1, cancellationToken).ConfigureAwait(false) is null)
        {
            return AgentSubscriptionUsageReading.NotAvailable;
        }

        await send("""{"method":"initialized"}""", cancellationToken).ConfigureAwait(false);
        await send("""{"id":2,"method":"account/read","params":{"refreshToken":false}}""", cancellationToken).ConfigureAwait(false);
        using (var account = await AnswerAsync(receive, 2, cancellationToken).ConfigureAwait(false))
        {
            // A CLI that is signed out or signed in with an API key has no plan; one of another account is not the one to show.
            if (account is null || !account.RootElement.TryGetProperty("account", out var signedIn) || signedIn.ValueKind != JsonValueKind.Object ||
                String(signedIn, "type") != "chatgpt" || !SameAccount(expectedAccount, String(signedIn, "email")))
            {
                return AgentSubscriptionUsageReading.ToolNotSignedIn;
            }
        }

        await send("""{"id":3,"method":"account/rateLimits/read"}""", cancellationToken).ConfigureAwait(false);
        using var limits = await AnswerAsync(receive, 3, cancellationToken).ConfigureAwait(false);
        if (limits is null)
        {
            return AgentSubscriptionUsageReading.NotAvailable;
        }

        var usage = Parse(limits.RootElement, now);
        return usage.Limits.Count == 0 ? AgentSubscriptionUsageReading.NotAvailable : new(AgentSubscriptionUsageReading.Ok, usage);
    }

    /// <summary>Reads the usage of the answer to <c>account/rateLimits/read</c>. Its shape is not a contract: what is missing is left out.</summary>
    internal static AgentSubscriptionUsage Parse(JsonElement result, DateTimeOffset now)
    {
        var limits = new List<AgentSubscriptionLimit>();
        var buckets = new List<(string Id, JsonElement Snapshot)>();
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
        {
            // The limits of the plan first, then the ones of a model or a feature.
            buckets.AddRange(byId.EnumerateObject()
                .Where(static property => property.Value.ValueKind == JsonValueKind.Object)
                .OrderBy(static property => property.Name != "codex").ThenBy(static property => property.Name, StringComparer.Ordinal)
                .Select(static property => (property.Name, property.Value)));
        }

        JsonElement main = default;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("rateLimits", out var single) && single.ValueKind == JsonValueKind.Object)
        {
            main = single;
            if (buckets.Count == 0)
            {
                buckets.Add((String(single, "limitId") ?? "codex", single));
            }
        }

        string? plan = main.ValueKind == JsonValueKind.Object ? String(main, "planType") : null;
        foreach (var (id, snapshot) in buckets)
        {
            plan ??= String(snapshot, "planType");
            var name = id == "codex" ? null : String(snapshot, "limitName") ?? id;
            foreach (var window in (string[])["primary", "secondary"])
            {
                if (!snapshot.TryGetProperty(window, out var value) || value.ValueKind != JsonValueKind.Object || Number(value, "usedPercent") is not { } used)
                {
                    continue;
                }

                limits.Add(new AgentSubscriptionLimit(
                    id + ":" + window,
                    name,
                    UsedPercent: Math.Max(0, used),
                    ResetsAt: Number(value, "resetsAt") is > 0 and var at ? DateTimeOffset.FromUnixTimeSeconds((long)at) : null,
                    WindowMinutes: Number(value, "windowDurationMins") is > 0 and var minutes ? (long)minutes : null));
            }
        }

        var owner = main.ValueKind == JsonValueKind.Object ? main : buckets.Count > 0 ? buckets[0].Snapshot : default;
        if (owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object &&
            Flag(credits, "hasCredits") == true)
        {
            if (Flag(credits, "unlimited") == true)
            {
                limits.Add(new AgentSubscriptionLimit("credits", Unit: "credits", Unlimited: true));
            }
            else if (Number(credits, "balance") is > 0 and var balance)
            {
                limits.Add(new AgentSubscriptionLimit("credits", Unit: "credits", Remaining: Math.Round(balance)));
            }
        }

        return new AgentSubscriptionUsage(plan, limits.Count > MaximumLimits ? limits[..MaximumLimits] : limits, now);
    }

    /// <summary>Finds the Codex CLI: a path that is given, or <c>codex</c> on the path, where its installers put it.</summary>
    internal static string? ResolveCli(string? command)
    {
        if (Text(command) is { } given)
        {
            return File.Exists(given) ? Path.GetFullPath(given) : null;
        }

        var names = OperatingSystem.IsWindows() ? (string[])["codex.exe", "codex.cmd"] : ["codex"];
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Path.IsPathRooted(folder))
            {
                continue;
            }

            foreach (var name in names)
            {
                try
                {
                    var path = Path.Combine(folder, name);
                    if (File.Exists(path))
                    {
                        return path;
                    }
                }
                catch (ArgumentException)
                {
                    // An entry of the path that is not a folder name.
                }
            }
        }

        return null;
    }

    internal static ProcessStartInfo CreateStartInfo(string cli)
    {
        var start = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (OperatingSystem.IsWindows() && Path.GetExtension(cli).ToLowerInvariant() is ".cmd" or ".bat")
        {
            // A script of the command interpreter, as a package manager installs one.
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } interpreter ? interpreter : "cmd.exe";
            start.Arguments = $"/d /s /c \"\"{cli}\" app-server\"";
        }
        else
        {
            start.FileName = cli;
            start.ArgumentList.Add("app-server");
        }

        return start;
    }

    // The answer to a request, among the notifications and the requests of the server; null for an error or the end of the output.
    private static async Task<JsonDocument?> AnswerAsync(Func<CancellationToken, ValueTask<string?>> receive, int id, CancellationToken cancellationToken)
    {
        while (await receive(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length is 0 or > MaximumLineLength)
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("method", out _) &&
                root.TryGetProperty("id", out var answered) && answered.ValueKind == JsonValueKind.Number && answered.TryGetInt32(out var number) && number == id)
            {
                if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
                {
                    using (document)
                    {
                        return JsonDocument.Parse(result.GetRawText());
                    }
                }

                document.Dispose();
                return null;
            }

            document.Dispose();
        }

        return null;
    }

    // Two addresses that are known must be the same one; one that is not known does not rule the other out.
    private static bool SameAccount(string? expected, string? actual)
        => Text(expected) is not { } left || Text(actual) is not { } right || !left.Contains('@') || !right.Contains('@')
           || string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? String(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Text(value.GetString()) : null;

    private static bool? Flag(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    // A number, or the same number as text (a balance is sent as text).
    private static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) => number,
            _ => null,
        };
    }
}
