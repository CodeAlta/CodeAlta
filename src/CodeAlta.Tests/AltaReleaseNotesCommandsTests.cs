using System.Text.Json;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

/// <summary>Tests of <c>alta release-notes</c>: the running version and the latest one, the notes of a version, the list, and the dialog.</summary>
[TestClass]
public sealed class AltaReleaseNotesCommandsTests
{
    [TestMethod]
    public async Task Command_ExistsOnlyWithTheService_ChangesNothing_AndRunsInASandbox()
    {
        using var none = Fixture.Create(service: false);
        Assert.AreEqual(AltaExitCodes.Usage, (await none.RunAsync("release-notes")).ExitCode);

        using var fixture = Fixture.Create();
        var policy = Records(await fixture.RunAsync("tool", "list", "--detailed")).Single(static line => line.TryGetProperty("path", out var path) && path.GetString() == "release-notes");
        Assert.IsFalse(policy.GetProperty("isMutating").GetBoolean());
        Assert.IsTrue(AltaSandboxGate.Allows("alta release-notes"));
    }

    [TestMethod]
    public async Task WithoutArguments_PrintsTheStatus_AndTheNotesOfTheRunningVersion()
    {
        using var fixture = Fixture.Create();
        var records = Records(await fixture.OkAsync("release-notes")).ToArray();
        var status = records.Single(static line => Type(line) == "alta.release-notes.status");
        Assert.AreEqual(("4.0.0", "4.1.0", false), (status.GetProperty("current").GetString(), status.GetProperty("latest").GetString(), status.GetProperty("isLatest").GetBoolean()));
        var release = records.Single(static line => Type(line) == "alta.release-notes.release");
        Assert.AreEqual(("4.0.0", "## The 4.0.0 tour"), (release.GetProperty("release").GetString(), release.GetProperty("notes").GetString()));

        var asked = Records(await fixture.OkAsync("release-notes", "v4.1.0")).Single(static line => Type(line) == "alta.release-notes.release");
        Assert.AreEqual("4.1.0", asked.GetProperty("release").GetString(), "a version is named with or without its v");
        var missing = await fixture.RunAsync("release-notes", "9.9.9");
        Assert.AreEqual(AltaExitCodes.NotFound, missing.ExitCode);
        StringAssert.Contains(missing.Stdout + missing.Stderr, "--list");
    }

    [TestMethod]
    public async Task List_PrintsEveryVersionWithoutNotes_AndShow_OpensTheDialog()
    {
        using var fixture = Fixture.Create();
        var listed = Records(await fixture.OkAsync("release-notes", "--list")).Where(static line => Type(line) == "alta.release-notes.release").ToArray();
        CollectionAssert.AreEqual(new[] { "4.1.0", "4.0.0" }, listed.Select(static line => line.GetProperty("release").GetString()).ToArray());
        Assert.IsTrue(listed.All(static line => !line.TryGetProperty("notes", out _)));

        Records(await fixture.OkAsync("release-notes", "4.1.0", "--show")).Single(static line => Type(line) == "alta.release-notes.shown");
        CollectionAssert.AreEqual(new[] { "4.1.0" }, fixture.Notes.Shown);
        fixture.Notes.Window = false;
        Assert.AreEqual(AltaExitCodes.ServiceUnavailable, (await fixture.RunAsync("release-notes", "--show")).ExitCode);
        Assert.AreEqual(AltaExitCodes.Usage, (await fixture.RunAsync("release-notes", "--show", "--list")).ExitCode);
    }

    [TestMethod]
    public async Task GitHubUnreachable_SaysWhy()
    {
        using var fixture = Fixture.Create();
        fixture.Notes.Failure = new HttpRequestException("GitHub limits the requests of this computer for now: try again in a while.");
        var result = await fixture.RunAsync("release-notes");
        Assert.AreEqual(AltaExitCodes.ServiceUnavailable, result.ExitCode);
        StringAssert.Contains(result.Stdout + result.Stderr, "limits the requests");
    }

    private static IEnumerable<JsonElement> Records(AltaCommandResult result)
        => result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(static line => JsonDocument.Parse(line).RootElement.Clone());

    private static string? Type(JsonElement line) => line.TryGetProperty("type", out var type) ? type.GetString() : null;

    private sealed class FakeNotes : IAltaReleaseNotes
    {
        public List<string?> Shown { get; } = [];
        public bool Window { get; set; } = true;
        public Exception? Failure { get; set; }

        public Task<AltaReleaseNotesList> ListAsync(bool refresh, CancellationToken cancellationToken)
            => Failure is not null ? Task.FromException<AltaReleaseNotesList>(Failure) : Task.FromResult(new AltaReleaseNotesList("4.0.0", "4.1.0", false,
            [
                new AltaRelease("4.1.0", "4.1.0", new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero), false, "https://github.com/CodeAlta/CodeAlta/releases/tag/4.1.0", "## The 4.1.0 tour"),
                new AltaRelease("4.0.0", "4.0.0", new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), false, "https://github.com/CodeAlta/CodeAlta/releases/tag/4.0.0", "## The 4.0.0 tour"),
            ]));

        public bool Show(string? version, string? callerSessionId)
        {
            if (!Window) return false;
            Shown.Add(version);
            return true;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly AltaCallerIdentity Mcp = new() { Kind = "mcp" };
        private readonly string _root;

        private Fixture(string root) => _root = root;

        public FakeNotes Notes { get; } = new();

        public AltaCommandDispatcher Dispatcher { get; private set; } = null!;

        public static Fixture Create(bool service = true)
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta.AltaReleaseNotesCommandsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new Fixture(root);
            var options = new CatalogOptions { GlobalRoot = root };
            var services = new AltaServiceCollection().Add(options).Add(new ProjectCatalog(options));
            if (service) services.Add<IAltaReleaseNotes>(fixture.Notes);
            var registry = new AltaCommandRegistry();
            services.Add(registry);
            fixture.Dispatcher = new AltaCommandDispatcher(registry, services);
            return fixture;
        }

        public Task<AltaCommandResult> RunAsync(params string[] args) => Dispatcher.InvokeAsync(args, caller: Mcp).AsTask();

        public async Task<AltaCommandResult> OkAsync(params string[] args)
        {
            var result = await Dispatcher.InvokeAsync(args, caller: Mcp);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, $"alta {string.Join(' ', args)}: {result.Stdout}{result.Stderr}");
            return result;
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
