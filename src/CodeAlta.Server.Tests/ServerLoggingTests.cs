using CodeAlta.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;

namespace CodeAlta.Server.Tests;

/// <summary>
/// Where <c>altaserver run</c> keeps its log beside the console, which a service manager drops: the file of its data
/// root, the journal of systemd when standard error goes to it, the Application event log on Windows.
/// </summary>
[TestClass]
[DoNotParallelize] // The file is the log of the process: one is open at a time.
public sealed class ServerLoggingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Run_KeepsItsLogInTheFileOfTheDataRoot_AndClosesItWhenItStops()
    {
        var token = TestContext.CancellationToken;
        using var folder = new TempDirectory();
        var paths = new ServerPaths(Path.Combine(folder.Path, "data"));
        var error = new StringWriter();
        var started = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new ServerApplication(TextWriter.Synchronized(new StringWriter()), TextWriter.Synchronized(error))
        {
            KeepsLog = true,
            Started = (_, stop) => started.TrySetResult(stop),
            ConfigureServer = static options => new CoordinatorServerOptions
            {
                DataRoot = options.DataRoot, Urls = options.Urls, AddLogging = options.AddLogging, EnvironmentVariable = static _ => null,
            },
        };
        var running = server.RunAsync(["run", "--data-root", paths.DataRoot, "--urls", "http://127.0.0.1:0"]);
        var stop = await started.Task.WithinAsync(TestTime.Seconds(30), token);
        stop();
        Assert.AreEqual(ServerExitCodes.Success, await running.WithinAsync(TestTime.Seconds(30), token), error.ToString());

        Assert.IsFalse(XenoAtom.Logging.LogManager.IsInitialized, "the file is closed: a version the launcher starts next opens it itself");
        var log = File.ReadAllText(paths.LogPath);
        StringAssert.Matches(log, new System.Text.RegularExpressions.Regex(@"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+ INF Microsoft\.Hosting\.Lifetime Now listening on: http://127\.0\.0\.1:\d+",
            System.Text.RegularExpressions.RegexOptions.Multiline), "what the console shows, a line per record, as the log of a node");
        StringAssert.Contains(log, "Application is shutting down...", "to the last record");
    }

    [TestMethod]
    public void AServerWithoutItsLogFolder_RunsWithoutTheFile_AndSaysSo()
    {
        using var folder = new TempDirectory();
        // A file where the folder of the logs goes: it cannot be made.
        var paths = new ServerPaths(Path.Combine(folder.Path, "data"));
        Directory.CreateDirectory(paths.DataRoot);
        File.WriteAllText(Path.GetDirectoryName(paths.LogPath)!, "");
        var error = new StringWriter();

        Assert.IsNull(ServerLogFile.Open(paths, error));
        StringAssert.Contains(error.ToString(), $"altaserver: the log is not kept in {paths.LogPath}: ");
        Assert.IsFalse(XenoAtom.Logging.LogManager.IsInitialized);
    }

    [TestMethod]
    public void TheJournalIsKnownByTheInodeOfTheStream_NotByTheVariableAlone()
    {
        Assert.IsTrue(ServerLogging.IsJournalStream("8:41823", "socket:[41823]"), "standard error is the stream systemd connected to the journal");
        // A shell of a desktop session inherits the variable of its terminal, with a terminal or a file as standard error.
        Assert.IsFalse(ServerLogging.IsJournalStream("8:41823", "/dev/pts/3"));
        Assert.IsFalse(ServerLogging.IsJournalStream("8:41823", "socket:[41824]"), "another socket: a pipe of the process that started it");
        Assert.IsFalse(ServerLogging.IsJournalStream("8:41823", null), "no /proc");
        Assert.IsFalse(ServerLogging.IsJournalStream(null, "socket:[41823]"), "not started by systemd");
        foreach (var malformed in new[] { "", "41823", ":41823", "8:", "8:-1", "8:x" })
            Assert.IsFalse(ServerLogging.IsJournalStream(malformed, "socket:[41823]"), malformed);
        Assert.IsFalse(ServerLogging.IsJournalStream("8:41823", "socket:[41823"));
    }

    [TestMethod]
    public void ToTheJournal_TheConsoleWritesOneLinePerRecordWithItsPriority_OnStandardError()
    {
        // The unit of altaserver install drops standard output and sends standard error to the journal.
        using var services = new ServiceCollection().AddLogging(static logging =>
        {
            logging.AddSimpleConsole();
            ServerLogging.AddJournal(logging);
        }).BuildServiceProvider();
        var console = services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value;

        Assert.AreEqual(ConsoleFormatterNames.Systemd, console.FormatterName);
        Assert.AreEqual(LogLevel.Trace, console.LogToStandardErrorThreshold);
    }

    [TestMethod]
    public void TheLogOfTheSystem_IsTheEventLogOnWindows_AndTheJournalOnlyWhenStandardErrorGoesToIt()
    {
        var collection = new ServiceCollection().AddLogging(static logging =>
        {
            logging.AddSimpleConsole();
            // The variable alone, as a shell inherits it: standard error of a test run is not that stream.
            ServerLogging.AddSystemLog(logging, static name => name == ServerLogging.JournalStreamVariable ? "8:1" : null);
        });
        using var services = collection.BuildServiceProvider();

        Assert.AreNotEqual(ConsoleFormatterNames.Systemd, services.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName);
        var eventLog = collection.Any(static service => service.ImplementationType == typeof(EventLogLoggerProvider));
        Assert.AreEqual(OperatingSystem.IsWindows(), eventLog);
        if (!eventLog) return;
        var rule = services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules.Single(static rule => rule.ProviderName == typeof(EventLogLoggerProvider).FullName);
        Assert.IsFalse(rule.Filter!(null, null, LogLevel.Information), "warnings and errors only, as ASP.NET Core does by default");
        Assert.IsTrue(rule.Filter(null, null, LogLevel.Warning));
    }
}
