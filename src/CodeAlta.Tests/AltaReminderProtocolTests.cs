using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AltaReminderProtocolTests
{
    [TestMethod]
    public async Task Delete_ReturnsCommittedRecordAndWarningWithSuccess()
    {
        using var fixture = new ProtocolFixture();
        var reminder = fixture.Create();
        fixture.Service.Changed += ThrowObserver;

        var result = await fixture.Dispatcher.InvokeAsync(["reminder", "delete", reminder.ReminderId]);
        var records = Records(result);

        Assert.AreEqual(AltaExitCodes.Success, result.ExitCode);
        Assert.AreEqual(0, records[0].GetProperty("exitCode").GetInt32());
        Assert.AreEqual(1, records[0].GetProperty("diagnosticCount").GetInt32());
        Assert.AreEqual("alta.reminder.deleted", records[1].GetProperty("type").GetString());
        var warning = records.Single(record => record.GetProperty("type").GetString() == "alta.warning");
        Assert.AreEqual("reminder.notificationFailed", warning.GetProperty("code").GetString());
        StringAssert.Contains(warning.GetProperty("message").GetString()!, "was deleted, but notification failed");
        Assert.IsFalse(warning.GetProperty("historical").GetBoolean());
        Assert.AreEqual(reminder.ReminderId, warning.GetProperty("reminderId").GetString());
        Assert.AreEqual("Target-Session", warning.GetProperty("sessionId").GetString());
        Assert.AreEqual("Deleted", warning.GetProperty("changeKind").GetString());
        Assert.AreEqual("observer [warning] text", warning.GetProperty("messages")[0].GetString());
        Assert.AreEqual(0, fixture.Service.List(null, true).Count);
    }

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("target-session", true)]
    [DataRow("other-session", false)]
    public async Task List_ReportsHistoricalFailureWithoutChangingCounts(string? sessionId, bool expectedWarning)
    {
        using var fixture = new ProtocolFixture();
        var reminder = fixture.Create();
        fixture.Service.Changed += ThrowObserver;
        fixture.Service.TryDelete(reminder.ReminderId, out _);
        var args = new List<string> { "reminder", "list", "--all" };
        if (sessionId is not null)
        {
            args.AddRange(["--session", sessionId]);
        }

        var result = await fixture.Dispatcher.InvokeAsync(args);
        var records = Records(result);

        Assert.AreEqual(AltaExitCodes.Success, result.ExitCode);
        Assert.AreEqual(0, records.Single(record => record.GetProperty("type").GetString() == "alta.reminder.summary").GetProperty("count").GetInt32());
        var warning = records.SingleOrDefault(record => record.GetProperty("type").GetString() == "alta.warning");
        Assert.AreEqual(expectedWarning, warning.ValueKind != JsonValueKind.Undefined);
        if (expectedWarning)
        {
            Assert.IsTrue(warning.GetProperty("historical").GetBoolean());
            StringAssert.Contains(warning.GetProperty("message").GetString()!, "Historical notification failure");
        }
    }

    [TestMethod]
    public async Task MissingDelete_DoesNotReportHistoricalFailureAsItsOwn()
    {
        using var fixture = new ProtocolFixture();
        fixture.Service.Changed += ThrowObserver;
        fixture.Create();

        var result = await fixture.Dispatcher.InvokeAsync(["reminder", "delete", "missing"]);

        Assert.AreEqual(AltaExitCodes.NotFound, result.ExitCode);
        Assert.IsFalse(Records(result).Any(record => record.GetProperty("type").GetString() == "alta.warning"));
    }

    [TestMethod]
    public void CreatedWarning_UsesActualOutputHelperAndOwnedScalars()
    {
        using var fixture = new ProtocolFixture();
        fixture.Service.Changed += ThrowObserver;
        fixture.Create();
        var context = new AltaCommandContext
        {
            Caller = AltaCallerIdentity.Host,
            Services = new AltaServiceCollection(),
            Stdin = TextReader.Null,
            Stdout = TextWriter.Null,
            Stderr = new StringWriter(),
            CorrelationId = "reminder-output-test",
        };

        BuiltInAltaCommandContributor.WriteReminderNotificationFailure(context, fixture.Service.GetLastNotificationFailure(), historical: false);
        using var document = JsonDocument.Parse(context.Stderr.ToString()!);

        var warning = document.RootElement;
        Assert.AreEqual("Created", warning.GetProperty("changeKind").GetString());
        Assert.AreEqual(1, warning.GetProperty("failureCount").GetInt32());
        Assert.IsFalse(warning.GetProperty("messagesTruncated").GetBoolean());
        StringAssert.Contains(warning.GetProperty("message").GetString()!, "was created, but notification failed");
    }

    [TestMethod]
    public void Source_CreateReportsFeedbackWithoutConstructingUnsafeRuntimeFixture()
    {
        // Exact source location; no ancestor discovery or default runtime/profile construction.
        var source = File.ReadAllText(ContributorSourcePath());
        var start = source.IndexOf("private static async ValueTask<int> HandleReminderCreateAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private static int HandleReminderList", start, StringComparison.Ordinal);
        var create = source[start..end];
        StringAssert.Contains(create, "}, out notificationFailure);");
        StringAssert.Contains(create, "WriteReminder(context, \"alta.reminder.created\", descriptor);");
        StringAssert.Contains(create, "WriteReminderNotificationFailure(context, notificationFailure, historical: false);");
        StringAssert.Contains(create, "return AltaExitCodes.Success;");
    }

    private static string ContributorSourcePath([CallerFilePath] string sourceFile = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "CodeAlta.LiveTool", "BuiltInAltaCommandContributor.cs"));

    private static void ThrowObserver(object? sender, EventArgs args)
        => throw new OperationCanceledException("observer [warning] text");

    private static JsonElement[] Records(AltaCommandResult result)
        => result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray();

    private sealed class ProtocolFixture : IDisposable
    {
        private readonly AltaReminderServiceTests.ManualClock _clock = new();

        public ProtocolFixture()
        {
            // Built-in list/delete do not resolve runtime, catalog, plugins, providers or cwd.
            // No default/plugin contributors and no advancing clock: delivery cannot fire.
            var services = new AltaServiceCollection();
            Dispatcher = new AltaCommandDispatcher(new AltaCommandRegistry([new BuiltInAltaCommandContributor()]), services);
            Service = new AltaReminderService(services, _clock);
            services.Add(Dispatcher).Add(Service);
        }

        public AltaCommandDispatcher Dispatcher { get; }
        public AltaReminderService Service { get; }

        public AltaReminderDescriptor Create() => Service.Create(new AltaReminderCreateRequest
        {
            TargetSessionId = "Target-Session",
            Content = "in-memory reminder",
            Duration = TimeSpan.FromMinutes(1),
            RepeatCount = 1,
        });

        public void Dispose()
        {
            Service.Changed -= ThrowObserver;
            foreach (var reminder in Service.List(null, true))
            {
                Service.TryDelete(reminder.ReminderId, out _);
            }

            _clock.Dispose();
        }
    }
}
