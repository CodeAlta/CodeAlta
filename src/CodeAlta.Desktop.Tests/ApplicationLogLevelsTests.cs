using CodeAlta.Catalog;
using CodeAlta.Catalog.Logging;
using CodeAlta.Desktop.Rpc;
using XenoAtom.Logging;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The levels of the features of the log: read from <c>[logging]</c> when the log starts, changed in Settings and
/// taken at once. Initializes the process logger, so it never runs beside other logging tests.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ApplicationLogLevelsTests
{
    private const string Epoch = "0f6b1c2d-3e4f-4a5b-8c6d-7e8f9a0b1c2d";

    [TestMethod]
    public void TheLevelsOfTheFile_AreThoseTheLogStartsWith_AndARowSaysItsFeature()
    {
        WithLog("[logging.features]\nmcp = \"debug\"\n", (capture, _) =>
        {
            Assert.IsTrue(LogManager.GetLogger("CodeAlta.Desktop.Mcp").IsEnabled(LogLevel.Debug));
            Assert.IsFalse(LogManager.GetLogger("CodeAlta.Desktop").IsEnabled(LogLevel.Debug));
            Assert.IsTrue(LogManager.GetLogger("CodeAlta.Desktop").IsEnabled(LogLevel.Info));
            Assert.AreEqual(LogLevel.Debug, capture.Levels!.Current.LevelOf(LogFeatures.Mcp));

            LogManager.GetLogger("CodeAlta.Desktop.Mcp").Debug("an MCP detail");
            Assert.IsTrue(SpinWait.SpinUntil(() => capture.Snapshot().Rows.Any(static row => row.Text == "an MCP detail"), TimeSpan.FromSeconds(5)));
            Assert.AreEqual(LogFeatures.Mcp, capture.Snapshot().Rows.Single(static row => row.Text == "an MCP detail").Feature);
        });
    }

    [TestMethod]
    public void ALevelChosenInSettings_IsSavedAndTakenAtOnce_AndTheFileEditedElsewhereToo()
    {
        WithLog(null, (capture, store) =>
        {
            var rpc = new ApplicationLogsService(capture, store, Epoch);
            var read = rpc.Levels(new());
            Assert.AreEqual(("ok", "info", true), (read.Status, read.Level, read.Editable));
            CollectionAssert.AreEqual(LogFeatures.All.Select(static feature => feature.Id).ToArray(), read.Features.Select(static feature => feature.Id).ToArray());
            Assert.IsTrue(read.Features.All(static feature => feature.Level is null));
            CollectionAssert.AreEqual(new[] { "trace", "debug", "info", "warn", "error" }, read.Levels.ToArray());
            var issues = LogManager.GetLogger("CodeAlta.Issues.Git");
            Assert.IsFalse(issues.IsEnabled(LogLevel.Debug));

            var saved = rpc.SaveLevels(new(Epoch, "warn", [new(LogFeatures.IssueTrackers, "debug"), new(LogFeatures.Git, null)]));

            Assert.AreEqual(("ok", "warn", "debug"), (saved.Status, saved.Level, saved.Features.Single(static feature => feature.Id == LogFeatures.IssueTrackers).Level));
            Assert.IsTrue(issues.IsEnabled(LogLevel.Debug), "The loggers already created take the level at once.");
            Assert.IsFalse(LogManager.GetLogger("CodeAlta.Desktop").IsEnabled(LogLevel.Info));
            Assert.IsTrue(LogManager.GetLogger("CodeAlta.Desktop").IsEnabled(LogLevel.Warn));
            var text = File.ReadAllText(store.ConfigPath);
            StringAssert.Contains(text, "level = \"warn\"");
            StringAssert.Contains(text, "issues = \"debug\"");

            Assert.AreEqual("stale_epoch", rpc.SaveLevels(new("another", "info", [])).Status);
            Assert.AreEqual("invalid_request", rpc.SaveLevels(new(Epoch, "loud", [])).Status);
            Assert.AreEqual("invalid_request", rpc.SaveLevels(new(Epoch, "info", [new("nothing", "debug")])).Status);
            Assert.AreEqual("invalid_request", rpc.SaveLevels(new(Epoch, "info", null)).Status);
            Assert.IsTrue(issues.IsEnabled(LogLevel.Debug), "A refused change changes nothing.");
            Assert.AreEqual("unavailable", new ApplicationLogsService(capture).SaveLevels(new(Epoch, "info", [])).Status);
            Assert.IsFalse(new ApplicationLogsService(capture).Levels(new()).Editable);

            // The configuration editor of Settings saves the whole file: the log reads it again.
            File.WriteAllText(store.ConfigPath, "[logging]\nlevel = \"info\"\n");
            rpc.Reload();
            Assert.IsFalse(issues.IsEnabled(LogLevel.Debug));
            Assert.IsTrue(LogManager.GetLogger("CodeAlta.Desktop").IsEnabled(LogLevel.Info));
            Assert.AreEqual("info", rpc.Levels(new()).Level);
        });
    }

    [TestMethod]
    public void TheLoggersADeveloperNames_StayAtDebug_WhateverTheLevelOfTheirFeature()
    {
        var config = new LogManagerConfig();
        var levels = new DesktopLogLevels(config, ["CodeAlta.ClaudeCode.Protocol"]);

        levels.Apply(LoggingSettings.Default with { Level = LogLevel.Error });
        Assert.AreEqual(LogLevel.Debug, config.GetLoggerConfig("CodeAlta.ClaudeCode.Protocol").MinimumLevel);
        levels.Apply(LoggingSettings.Default with { Features = LoggingSettings.Default.Features.Add(LogFeatures.Providers, LogLevel.Trace) });
        Assert.AreEqual(LogLevel.Trace, config.GetLoggerConfig("CodeAlta.ClaudeCode.Protocol").MinimumLevel, "Never less than its feature.");
    }

    private static void WithLog(string? configText, Action<DesktopLogCapture, CodeAltaConfigStore> test)
    {
        var root = Directory.CreateTempSubdirectory("codealta-log-levels-").FullName;
        try
        {
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = root });
            if (configText is not null) File.WriteAllText(store.ConfigPath, configText);
            var capture = DesktopLogging.Initialize(Path.Combine(root, "data"), store.LoadGlobalLoggingSettings, out _)!;
            try { test(capture, store); }
            finally { LogManager.Shutdown(); }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
