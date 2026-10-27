using System.Collections.Immutable;
using CodeAlta.Catalog.Logging;
using XenoAtom.Logging;

namespace CodeAlta.Catalog.Tests;

/// <summary>The features of the log, and the level of each one in <c>[logging]</c>.</summary>
[TestClass]
public sealed class LoggingSettingsTests
{
    [TestMethod]
    [DataRow("CodeAlta.Issues", LogFeatures.IssueTrackers)]
    [DataRow("CodeAlta.Issues.Git", LogFeatures.IssueTrackers)]
    [DataRow("CodeAlta.Plugin.builtin:jira", LogFeatures.IssueTrackers)]
    [DataRow("CodeAlta.Plugin.builtin:git", LogFeatures.Git)]
    [DataRow("CodeAlta.Plugin.builtin:mobile", LogFeatures.Mobile)]
    [DataRow("CodeAlta.Plugin.someone.thing", LogFeatures.Plugins)]
    [DataRow("CodeAlta.Desktop.Plugins", LogFeatures.Plugins)]
    [DataRow("CodeAlta.Agent.OpenAI", LogFeatures.Providers)]
    [DataRow("CodeAlta.Provider.anthropic", LogFeatures.Providers)]
    [DataRow("acp.client", LogFeatures.Providers)]
    [DataRow("CodeAlta.Agent.Runtime", LogFeatures.Sessions)]
    [DataRow("CodeAlta.AgentSessionConnection", LogFeatures.Sessions)]
    [DataRow("CodeAlta.Desktop.ToolEndpoints", LogFeatures.Tools)]
    [DataRow("CodeAlta.Desktop.Mcp", LogFeatures.Mcp)]
    [DataRow("CodeAlta.Desktop.McpApps", LogFeatures.Mcp)]
    [DataRow("CodeAlta.Search", LogFeatures.Search)]
    [DataRow("CodeAlta.Desktop.Speech", LogFeatures.Speech)]
    [DataRow("CodeAlta.Desktop", LogFeatures.System)]
    [DataRow("CodeAlta.Desktop.Rpc", LogFeatures.System)]
    [DataRow("CodeAlta", LogFeatures.System)]
    [DataRow("CodeAltaX", LogFeatures.System)]
    [DataRow("CodeAlta.Issuesx", LogFeatures.System)]
    [DataRow("External.Component", LogFeatures.System)]
    [DataRow("", LogFeatures.System)]
    public void Of_IsTheFeatureOfTheLongestPrefixOnADot(string logger, string feature)
        => Assert.AreEqual(feature, LogFeatures.Of(logger));

    [TestMethod]
    public void Features_HaveUniqueIdsAndPrefixes()
    {
        Assert.AreEqual(LogFeatures.All.Length, LogFeatures.All.Select(static feature => feature.Id).Distinct().Count());
        var prefixes = LogFeatures.All.SelectMany(static feature => feature.Prefixes).ToArray();
        Assert.AreEqual(prefixes.Length, prefixes.Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(LogFeatures.System, LogFeatures.All[0].Id);
    }

    [TestMethod]
    public void Read_IgnoresWhatItDoesNotUnderstand()
    {
        var settings = LoggingSettings.Read(new CodeAltaLoggingSettingsDocument
        {
            Level = "loud",
            Features = new() { ["issues"] = "Debug", ["git"] = "warning", ["nothing"] = "debug", ["mcp"] = "verbose" },
        });

        Assert.AreEqual(LogLevel.Info, settings.Level);
        Assert.AreEqual(LogLevel.Debug, settings.LevelOf(LogFeatures.IssueTrackers));
        Assert.AreEqual(LogLevel.Warn, settings.LevelOf(LogFeatures.Git));
        Assert.AreEqual(LogLevel.Info, settings.LevelOf(LogFeatures.Mcp));
        Assert.AreEqual(2, settings.Features.Count);
        Assert.AreSame(LoggingSettings.Default, LoggingSettings.Read(null));
    }

    [TestMethod]
    public void Save_WritesOnlyWhatIsNotTheDefault_AndReadsBack()
    {
        WithStore(store =>
        {
            Assert.AreEqual(LogLevel.Info, store.LoadGlobalLoggingSettings().Level);
            store.SaveGlobalCommitCoAuthor(false);

            var settings = new LoggingSettings(LogLevel.Warn, new Dictionary<string, LogLevel>
            {
                [LogFeatures.IssueTrackers] = LogLevel.Debug,
                [LogFeatures.Git] = LogLevel.Warn, // The level of every feature: not written.
            }.ToImmutableDictionary());
            store.SaveGlobalLoggingSettings(settings);

            var text = File.ReadAllText(store.ConfigPath);
            StringAssert.Contains(text, "[logging]");
            StringAssert.Contains(text, "level = \"warn\"");
            StringAssert.Contains(text, "issues = \"debug\"");
            Assert.IsFalse(text.Contains("git =", StringComparison.Ordinal));
            Assert.IsFalse(store.LoadGlobalCommitCoAuthor(), "The other settings are kept.");

            var read = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Path.GetDirectoryName(store.ConfigPath)! }).LoadGlobalLoggingSettings();
            Assert.AreEqual(LogLevel.Warn, read.Level);
            Assert.AreEqual(LogLevel.Debug, read.LevelOf(LogFeatures.IssueTrackers));
            Assert.AreEqual(LogLevel.Warn, read.LevelOf(LogFeatures.Git));

            store.SaveGlobalLoggingSettings(LoggingSettings.Default);
            Assert.IsFalse(File.ReadAllText(store.ConfigPath).Contains("[logging", StringComparison.Ordinal));
            Assert.AreEqual(LoggingSettings.Default.Level, store.LoadGlobalLoggingSettings().Level);
            Assert.IsEmpty(store.LoadGlobalLoggingSettings().Features);
        });
    }

    [TestMethod]
    public void Load_OfABrokenFile_IsTheDefault_AndSaveRefusesWhatTheLogDoesNotKnow()
    {
        WithStore(store =>
        {
            File.WriteAllText(store.ConfigPath, "[logging\nlevel = ");
            Assert.AreSame(LoggingSettings.Default, store.LoadGlobalLoggingSettings());

            File.WriteAllText(store.ConfigPath, "[logging]\nlevel = \"debug\"\n\n[logging.features]\nmcp = \"trace\"\n");
            Assert.AreEqual(LogLevel.Debug, store.LoadGlobalLoggingSettings().Level);
            Assert.AreEqual(LogLevel.Trace, store.LoadGlobalLoggingSettings().LevelOf(LogFeatures.Mcp));

            var unknown = new LoggingSettings(LogLevel.Info, ImmutableDictionary<string, LogLevel>.Empty.Add("nothing", LogLevel.Debug));
            Assert.ThrowsExactly<ArgumentException>(() => store.SaveGlobalLoggingSettings(unknown));
            Assert.ThrowsExactly<ArgumentException>(() => store.SaveGlobalLoggingSettings(LoggingSettings.Default with { Level = LogLevel.None }));
            Assert.AreEqual(LogLevel.Debug, store.LoadGlobalLoggingSettings().Level, "A refused save leaves the file as it is.");
        });
    }

    [TestMethod]
    public void ApplyTo_GivesEachFeatureItsLevel_WhateverTheLevelOfSystem()
    {
        var config = new LogManagerConfig();
        var settings = new LoggingSettings(LogLevel.Error, ImmutableDictionary<string, LogLevel>.Empty
            .Add(LogFeatures.IssueTrackers, LogLevel.Debug).Add(LogFeatures.Sessions, LogLevel.Trace));

        settings.ApplyTo(config);

        foreach (var feature in LogFeatures.All)
        {
            foreach (var prefix in feature.Prefixes)
                Assert.AreEqual(settings.LevelOf(feature.Id), config.GetLoggerConfig(prefix).MinimumLevel, prefix);
        }

        Assert.AreEqual(LogLevel.Debug, config.GetLoggerConfig("CodeAlta.Issues").MinimumLevel);
        Assert.AreEqual(LogLevel.Error, config.GetLoggerConfig("CodeAlta").MinimumLevel);
        Assert.AreEqual(LogLevel.Error, config.RootLogger.MinimumLevel, "A quieter system quiets the loggers of no feature.");

        LoggingSettings.Default.ApplyTo(config);
        Assert.AreEqual(LogLevel.Warn, config.RootLogger.MinimumLevel, "The libraries stay at Warn.");
        Assert.AreEqual(LogLevel.Info, config.GetLoggerConfig("CodeAlta.Issues").MinimumLevel);
    }

    private static void WithStore(Action<CodeAltaConfigStore> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-logging-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root })); }
        finally { Directory.Delete(root, recursive: true); }
    }
}
