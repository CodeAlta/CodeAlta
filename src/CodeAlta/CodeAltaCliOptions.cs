using System.Diagnostics.CodeAnalysis;
using CodeAlta.Plugins;
using XenoAtom.CommandLine;
using XenoAtom.CommandLine.Terminal;

namespace CodeAlta;

internal sealed class CodeAltaCliOptions
{
    private static readonly TimeSpan DefaultTestDuration = TimeSpan.FromSeconds(10);

    private CodeAltaCliOptions(bool testMode, TimeSpan? testDuration, bool pluginSafeMode, bool pluginsStatus, bool waitForEnterAfterPluginLiveOutput)
    {
        TestMode = testMode;
        TestDuration = testDuration;
        PluginSafeMode = pluginSafeMode;
        PluginsStatus = pluginsStatus;
        WaitForEnterAfterPluginLiveOutput = waitForEnterAfterPluginLiveOutput;
    }

    public bool TestMode { get; }

    public TimeSpan? TestDuration { get; }

    public bool PluginSafeMode { get; }

    public bool PluginsStatus { get; }

    public bool WaitForEnterAfterPluginLiveOutput { get; }

    public static CodeAltaPluginBootstrapOptions GetPluginBootstrapOptions(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var state = new ParseState();
        var app = CreatePluginBootstrapCommandApp(state);
        var result = app.Parse(args);
        if (result.HasErrors)
        {
            return new CodeAltaPluginBootstrapOptions(
                PluginRuntimeConfigResolver.IsSafeModeEnabled([]),
                PluginsStatus: false,
                WaitForEnterAfterPluginLiveOutput: false);
        }

        return new CodeAltaPluginBootstrapOptions(
            state.PluginSafeMode || PluginRuntimeConfigResolver.IsSafeModeEnabled([]),
            state.PluginsStatus,
            state.WaitForEnterAfterPluginLiveOutput);
    }

    public static bool TryParse(
        IReadOnlyList<string> args,
        [NotNullWhen(true)] out CodeAltaCliOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        var state = new ParseState();
        var app = CreateCommandAppCore(
            state,
            static _ => ValueTask.FromResult(0));

        var result = app.Parse(args);
        if (result.HasErrors)
        {
            options = null;
            error = result.Errors[0].Message;
            return false;
        }

        return TryCreateOptions(state, out options, out error);
    }

    public static CommandApp CreateCommandApp(
        Func<CodeAltaCliOptions, ValueTask<int>> execute,
        IReadOnlyList<CommandNode>? pluginCommandLineContributions = null)
    {
        ArgumentNullException.ThrowIfNull(execute);

        return CreateCommandAppCore(
            new ParseState(),
            execute,
            pluginCommandLineContributions);
    }

    private static CommandApp CreateCommandAppCore(
        ParseState state,
        Func<CodeAltaCliOptions, ValueTask<int>> execute,
        IReadOnlyList<CommandNode>? pluginCommandLineContributions = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(execute);

        const string _ = "";

        var app = new CommandApp(
            "alta",
            config: new CommandConfig
            {
                OutputFactory = static _ => new TerminalVisualCommandOutput(new TerminalVisualOutputOptions
                {
                    UseTableForOptions = true,
                    SectionGroupMinWidth = 70,
                    ErrorGroupMinWidth = 70,
                }),
            })
        {
            new CommandUsage(),
            _,
            "Options:",
            { "test", "Run the terminal smoke test", value => state.TestMode = value is not null },
            { "test-duration=", "Smoke-test duration in {SECONDS}", (int value) => state.TestDurationSeconds = value },
            { "no-plugins", "Disable plugin discovery, build, and load for this process", value => state.PluginSafeMode = value is not null },
            { "plugin-safe-mode", "Disable plugin discovery, build, and load for this process", value => state.PluginSafeMode = value is not null },
            { "plugins-status", "Print plugin discovery/config status and exit without starting the TUI", value => state.PluginsStatus = value is not null },
            { "plugins-wait-for-enter", "Wait for Enter after source plugin live progress finishes", value => state.WaitForEnterAfterPluginLiveOutput = value is not null },
        };

        if (pluginCommandLineContributions is not null)
        {
            foreach (var contribution in pluginCommandLineContributions)
            {
                app.Add(contribution);
            }
        }

        app.Add(new HelpOption());
        app.Add((_, _) =>
            {
                if (!TryCreateOptions(state, out var options, out var error))
                {
                    throw new CommandException(error!);
                }

                return execute(options!);
            });
        return app;
    }

    private static bool TryCreateOptions(
        ParseState state,
        [NotNullWhen(true)] out CodeAltaCliOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.TestDurationSeconds is <= 0)
        {
            options = null;
            error = "The value for --test-duration must be a positive integer number of seconds.";
            return false;
        }

        if (!state.TestMode && state.TestDurationSeconds is not null)
        {
            options = null;
            error = "--test-duration requires --test.";
            return false;
        }

        options = new CodeAltaCliOptions(
            state.TestMode,
            state.TestMode
                ? TimeSpan.FromSeconds(state.TestDurationSeconds ?? DefaultTestDuration.TotalSeconds)
                : null,
            state.PluginSafeMode || PluginRuntimeConfigResolver.IsSafeModeEnabled([]),
            state.PluginsStatus,
            state.WaitForEnterAfterPluginLiveOutput);
        error = null;
        return true;
    }

    private static CommandApp CreatePluginBootstrapCommandApp(ParseState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new CommandApp(
            "alta",
            config: new CommandConfig { StrictOptionParsing = false })
        {
            { "no-plugins", "Disable plugin discovery, build, and load for this process", value => state.PluginSafeMode = value is not null },
            { "plugin-safe-mode", "Disable plugin discovery, build, and load for this process", value => state.PluginSafeMode = value is not null },
            { "plugins-status", "Print plugin discovery/config status and exit without starting the TUI", value => state.PluginsStatus = value is not null },
            { "plugins-wait-for-enter", "Wait for Enter after source plugin live progress finishes", value => state.WaitForEnterAfterPluginLiveOutput = value is not null },
            { "<>", "Arguments parsed by the full command app after plugin startup" },
            static _ => ValueTask.FromResult(0),
        };
    }

    private sealed class ParseState
    {
        public bool TestMode { get; set; }

        public int? TestDurationSeconds { get; set; }

        public bool PluginSafeMode { get; set; }

        public bool PluginsStatus { get; set; }

        public bool WaitForEnterAfterPluginLiveOutput { get; set; }
    }
}

internal readonly record struct CodeAltaPluginBootstrapOptions(
    bool PluginSafeMode,
    bool PluginsStatus,
    bool WaitForEnterAfterPluginLiveOutput);
