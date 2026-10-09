using System.Globalization;
using System.Text.Json;

namespace CodeAlta.Agent.Claude;

/// <summary>
/// Reads what a Claude subscription has left by asking the Claude Code CLI, which is the one that knows the account:
/// the five-hour window, the week, the limits of a model and the extra usage. A short-lived process answers and
/// exits; no model is called, and CodeAlta never reads the credentials of the CLI.
/// </summary>
public static class ClaudeCodeAccountUsage
{
    /// <summary>The window of five hours.</summary>
    public const string FiveHour = "five_hour";

    /// <summary>The window of seven days.</summary>
    public const string SevenDay = "seven_day";

    private const long FiveHourMinutes = 5 * 60;
    private const long SevenDayMinutes = 7 * 24 * 60;
    private static readonly TimeSpan UsageTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LongestStartup = TimeSpan.FromSeconds(30);

    /// <summary>Starts the CLI, asks it for the usage of its account, and stops it.</summary>
    /// <param name="options">The options of the Claude Code provider: where its CLI is.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>
    /// The usage; that the CLI is missing; or not available when the CLI is too old to answer or its account has no
    /// limits of a subscription (an API key, a cloud platform).
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The CLI did not answer.</exception>
    /// <exception cref="OperationCanceledException">The reading was canceled.</exception>
    public static async Task<AgentSubscriptionUsageReading> ReadAsync(ClaudeCodeModelProviderRuntimeOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var resolution = options.ResolveCli?.Invoke() ?? ClaudeCodeCliLocator.Resolve(options.Command);
        if (resolution.Path is null)
        {
            return AgentSubscriptionUsageReading.NoTool;
        }

        // As for the list of models: the process works in the profile of the user and saves no session.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var launch = ClaudeCodeLauncher.Create(
            resolution.Path,
            options,
            new ClaudeCodeLaunchKey(string.IsNullOrWhiteSpace(home) ? null : home, null, null),
            newSessionId: null,
            resumeSessionId: null,
            withTools: false,
            // The process is only asked what it runs, which bills nothing: it loses the key only when a turn would.
            withoutApiKey: ClaudeCodeApiKey.Decide(options) == ClaudeCodeApiKeyDecision.Ignore);
        launch = launch with { Arguments = [.. launch.Arguments, "--no-session-persistence"] };

        var connection = new ClaudeCodeConnection((options.TransportFactory ?? ClaudeCodeProcessTransportFactory.Instance).Start(launch), Silent.Instance);
        try
        {
            connection.Start();
            var startup = options.StartupTimeout < LongestStartup ? options.StartupTimeout : LongestStartup;
            try
            {
                await connection.RequestAsync("initialize", null, startup, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or ClaudeCodeControlException)
            {
                throw new InvalidOperationException($"Claude Code at '{resolution.Path}' did not answer. {connection.DescribeExit()}", exception);
            }

            JsonElement response;
            try
            {
                // Without the habits of the last days, which the CLI would read from seven days of transcripts.
                response = await connection.RequestAsync("get_usage", static writer => writer.WriteBoolean("skip_behaviors", true), UsageTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ClaudeCodeControlException)
            {
                // A CLI that does not know the request, or cannot answer it here.
                return AgentSubscriptionUsageReading.NotAvailable;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                throw new InvalidOperationException($"Claude Code at '{resolution.Path}' did not answer. {connection.DescribeExit()}", exception);
            }

            return Parse(response, DateTimeOffset.UtcNow);
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reads the usage of an answer of the CLI. Its shape is not a contract: what is missing is left out.</summary>
    internal static AgentSubscriptionUsageReading Parse(JsonElement response, DateTimeOffset now)
    {
        if (!ClaudeCodeJson.TryGetObject(response, "rate_limits", out var rateLimits))
        {
            return AgentSubscriptionUsageReading.NotAvailable;
        }

        var limits = new List<AgentSubscriptionLimit>();
        Add(limits, rateLimits, FiveHour, FiveHour, null, FiveHourMinutes);
        Add(limits, rateLimits, SevenDay, SevenDay, null, SevenDayMinutes);
        Add(limits, rateLimits, "seven_day_opus", SevenDay + ":opus", "Opus", SevenDayMinutes);
        Add(limits, rateLimits, "seven_day_sonnet", SevenDay + ":sonnet", "Sonnet", SevenDayMinutes);
        if (ClaudeCodeJson.TryGetArray(rateLimits, "model_scoped", out var scoped))
        {
            foreach (var entry in scoped.EnumerateArray().Take(8))
            {
                if (ClaudeCodeJson.GetString(entry, "display_name") is { Length: > 0 and <= 64 } name &&
                    !limits.Any(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)) &&
                    Window(entry, SevenDay + ":" + name.ToLowerInvariant(), name, SevenDayMinutes) is { } limit)
                {
                    limits.Add(limit);
                }
            }
        }

        if (ClaudeCodeJson.TryGetObject(rateLimits, "extra_usage", out var extra) && ClaudeCodeJson.GetBoolean(extra, "is_enabled") &&
            ClaudeCodeJson.GetDouble(extra, "utilization") is { } spent)
        {
            limits.Add(new AgentSubscriptionLimit("extra_usage", UsedPercent: Math.Max(0, spent)));
        }

        return limits.Count == 0
            ? AgentSubscriptionUsageReading.NotAvailable
            : new(AgentSubscriptionUsageReading.Ok, new AgentSubscriptionUsage(ClaudeCodeJson.GetString(response, "subscription_type"), limits, now));
    }

    private static void Add(List<AgentSubscriptionLimit> limits, JsonElement rateLimits, string property, string id, string? name, long minutes)
    {
        if (ClaudeCodeJson.TryGetObject(rateLimits, property, out var window) && Window(window, id, name, minutes) is { } limit)
        {
            limits.Add(limit);
        }
    }

    // A window the account does not have comes without a utilization.
    private static AgentSubscriptionLimit? Window(JsonElement window, string id, string? name, long minutes)
        => ClaudeCodeJson.GetDouble(window, "utilization") is { } used
            ? new AgentSubscriptionLimit(
                id,
                name,
                UsedPercent: Math.Max(0, used),
                ResetsAt: ClaudeCodeJson.GetString(window, "resets_at") is { } text &&
                          DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
                    ? at
                    : null,
                WindowMinutes: minutes)
            : null;

    private sealed class Silent : IClaudeCodeConnectionHandler
    {
        public static Silent Instance { get; } = new();

        public void OnMessage(string type, JsonElement message)
        {
        }

        public void OnControlRequest(string requestId, string subtype, JsonElement request)
        {
        }

        public void OnControlCancel(string requestId)
        {
        }

        public void OnClosed(Exception? failure)
        {
        }
    }
}
