using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;
using Microsoft.Data.Sqlite;

namespace CodeAlta.Tests;

/// <summary>Tests of <c>alta canvas</c>: the tabs that plugins provide, listed, opened, closed and invoked by agents.</summary>
[TestClass]
public sealed class AltaCanvasCommandsTests
{
    private static readonly string[] Leaves = ["list", "show", "open", "focus", "close", "invoke"];

    [TestMethod]
    public async Task CanvasGroup_ExistsWithAView_AndNotWithout()
    {
        using var none = await Fixture.CreateAsync(canvases: false);
        Assert.AreEqual(AltaExitCodes.Usage, (await none.RunAsync("canvas", "list")).ExitCode);
        Assert.IsFalse((await none.RunAsync("tool", "list")).Stdout.Contains("canvas list", StringComparison.Ordinal));

        using var window = await Fixture.CreateAsync();
        foreach (var leaf in Leaves)
        {
            var help = await window.RunAsync("canvas", leaf, "--help");
            Assert.AreEqual(AltaExitCodes.Success, help.ExitCode, leaf);
            Assert.IsTrue(help.IsHelp, leaf);
        }

        var tools = (await window.RunAsync("tool", "list")).Stdout;
        foreach (var leaf in Leaves) StringAssert.Contains(tools, $"canvas {leaf}");
        var groupHelp = await window.RunAsync("canvas", "--help");
        StringAssert.Contains(groupHelp.Stdout, "A canvas is declared by a plugin");
    }

    [TestMethod]
    public async Task List_ShowsTheDeclaredCanvases_WithTheirRefs_AndFiltersByPlugin()
    {
        using var fixture = await Fixture.CreateAsync();
        var items = Records(await fixture.OkAsync("canvas", "list")).Where(line => Type(line) == "alta.canvas.item").ToArray();
        CollectionAssert.AreEqual(new[] { "builtin:statistics/dashboard", "global:tools/checklist", "global:tools/board", "project:x/checklist" },
            items.Select(static item => item.GetProperty("ref").GetString()).ToArray());
        var checklist = items[1];
        Assert.AreEqual("project", checklist.GetProperty("scope").GetString());
        Assert.AreEqual(2, checklist.GetProperty("actions").GetInt32());
        Assert.AreEqual("Tools", checklist.GetProperty("plugin").GetString());
        Assert.AreEqual(4, fixture.Single(await fixture.OkAsync("canvas", "list"), "alta.canvas.summary").GetProperty("count").GetInt32());
        Assert.IsFalse(items[2].TryGetProperty("description", out _) || items[2].TryGetProperty("icon", out _), "What a canvas does not have is left out of its record.");

        var tools = Records(await fixture.OkAsync("canvas", "list", "--plugin", "global:tools")).Where(line => Type(line) == "alta.canvas.item").ToArray();
        Assert.AreEqual(2, tools.Length);
        Assert.AreEqual(2, Records(await fixture.OkAsync("canvas", "list", "--plugin", "tools")).Count(line => Type(line) == "alta.canvas.item"), "The name of the plugin works as its key.");

        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.scopeConflict", "canvas", "list", "--all");
    }

    [TestMethod]
    public async Task List_Open_ReadsTheTabsOfTheShownSpace_OfOneSpace_OrOfAll()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Spaces.CreateAsync("Work");
        fixture.Canvases.Instances.Add(new("i1", "builtin:statistics", "dashboard", "default", null, null, null, "Statistics", true));
        fixture.Canvases.Instances.Add(new("i2", "global:tools", "checklist", "work", "p1", null, null, "Checklist", false));

        var current = Records(await fixture.OkAsync("canvas", "list", "--open")).Where(line => Type(line) == "alta.canvas.instance").ToArray();
        Assert.AreEqual("i1", current.Single().GetProperty("instanceId").GetString(), "Without a window that says it, the default space is the current one.");

        fixture.View.ShownSpaceId = "work";
        Assert.AreEqual("i2", Records(await fixture.OkAsync("canvas", "list", "--open")).Single(line => Type(line) == "alta.canvas.instance").GetProperty("instanceId").GetString());
        Assert.AreEqual("i1", Records(await fixture.OkAsync("canvas", "list", "--open", "--space", "default")).Single(line => Type(line) == "alta.canvas.instance").GetProperty("instanceId").GetString());
        Assert.AreEqual(2, Records(await fixture.OkAsync("canvas", "list", "--open", "--all")).Count(line => Type(line) == "alta.canvas.instance"));
        Assert.AreEqual(1, Records(await fixture.OkAsync("canvas", "list", "--open", "--all", "--plugin", "statistics")).Count(line => Type(line) == "alta.canvas.instance"));
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.scopeConflict", "canvas", "list", "--open", "--all", "--space", "work");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "space.notFound", "canvas", "list", "--open", "--space", "nowhere");
    }

    [TestMethod]
    public async Task Ids_AreRefsOrUniqueCanvasIds()
    {
        using var fixture = await Fixture.CreateAsync();
        // `dashboard` is declared once; `checklist` by two plugins.
        Assert.AreEqual("builtin:statistics/dashboard", fixture.Single(await fixture.OkAsync("canvas", "show", "dashboard"), "alta.canvas.detail").GetProperty("ref").GetString());
        var ambiguous = await fixture.FailsAsync(AltaExitCodes.Usage, "usage.ambiguousCanvas", "canvas", "show", "checklist");
        StringAssert.Contains(ambiguous.GetProperty("message").GetString(), "global:tools/checklist");
        StringAssert.Contains(ambiguous.GetProperty("message").GetString(), "project:x/checklist");
        Assert.AreEqual("project:x/checklist", fixture.Single(await fixture.OkAsync("canvas", "show", "project:x/checklist"), "alta.canvas.detail").GetProperty("ref").GetString());
        await fixture.FailsAsync(AltaExitCodes.NotFound, "canvas.notFound", "canvas", "show", "nothing");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "canvas.notFound", "canvas", "show", "global:tools/nothing");
    }

    [TestMethod]
    public async Task Show_PrintsTheActionsWithTheirSchemas_AndWhatTheCanvasShows()
    {
        using var fixture = await Fixture.CreateAsync();
        var project = await fixture.AddProjectAsync("alpha");
        var detail = fixture.Single(await fixture.OkAsync("canvas", "show", "global:tools/checklist", "--project", project.Slug), "alta.canvas.detail");
        var actions = detail.GetProperty("actions").EnumerateArray().ToArray();
        Assert.AreEqual("tick", actions[0].GetProperty("name").GetString());
        Assert.AreEqual("object", actions[0].GetProperty("inputSchema").GetProperty("type").GetString(), "A schema is JSON, not a string.");
        Assert.AreEqual("ok", detail.GetProperty("describeStatus").GetString());
        Assert.AreEqual($"Checklist of {project.Id}", detail.GetProperty("markdown").GetString(), "The project is the id the window uses, not the slug.");
        Assert.AreEqual(project.Id, fixture.Canvases.Described.Single().ProjectId);

        // Without a project the canvas is shown but cannot say what it shows.
        var bare = fixture.Single(await fixture.OkAsync("canvas", "show", "global:tools/checklist"), "alta.canvas.detail");
        Assert.IsFalse(bare.TryGetProperty("markdown", out _));
        Assert.AreEqual("# Statistics", fixture.Single(await fixture.OkAsync("canvas", "show", "dashboard"), "alta.canvas.detail").GetProperty("markdown").GetString());
    }

    [TestMethod]
    public async Task Open_AnApplicationCanvas_ShowsItInTheShownSpace_WithItsInput()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.View.ShownSpaceId = "default";
        var opened = fixture.Single(await fixture.OkAsync(["canvas", "open", "dashboard", "--stdin"], stdin: "{\"range\":\"week\"}"), "alta.canvas.opened");
        Assert.IsTrue(opened.GetProperty("shown").GetBoolean());
        Assert.AreEqual("default", opened.GetProperty("spaceId").GetString());
        var request = fixture.Canvases.Opened.Single();
        Assert.AreEqual("week", request.Input!.Value.GetProperty("range").GetString());
        Assert.IsNull(request.Target.ProjectId, "An application canvas is about no project.");
        Assert.IsTrue(request.Focus);
        Assert.AreEqual(0, fixture.View.Shown.Count, "Opening a tab never moves the window.");
    }

    [TestMethod]
    public async Task Open_AProjectCanvas_ResolvesTheSlugToTheId_AndTheSpaceRuleApplies()
    {
        using var fixture = await Fixture.CreateAsync();
        var alpha = await fixture.AddProjectAsync("alpha");
        var beta = await fixture.AddProjectAsync("beta");
        await fixture.Spaces.CreateAsync("Work");
        await fixture.Spaces.AssignAsync(alpha.Id, ["work"], null);
        fixture.View.ShownSpaceId = "work";

        // The shown space has the project: the tab goes there.
        var here = fixture.Single(await fixture.OkAsync("canvas", "open", "global:tools/checklist", "--project", alpha.Slug), "alta.canvas.opened");
        Assert.AreEqual(alpha.Id, fixture.Canvases.Opened[0].Target.ProjectId);
        Assert.AreEqual("work", here.GetProperty("spaceId").GetString());
        Assert.IsTrue(here.GetProperty("shown").GetBoolean());

        // The shown space does not have it: the first space of the project is the default one, the window stays where it is.
        var elsewhere = fixture.Single(await fixture.OkAsync("canvas", "open", "global:tools/checklist", "--project", beta.ProjectPath), "alta.canvas.opened");
        Assert.AreEqual(beta.Id, fixture.Canvases.Opened[1].Target.ProjectId);
        Assert.AreEqual("default", elsewhere.GetProperty("spaceId").GetString());
        Assert.IsFalse(elsewhere.GetProperty("shown").GetBoolean());
        Assert.AreEqual(0, fixture.View.Shown.Count);
        Assert.AreEqual("work", fixture.View.ShownSpaceId);

        // --space names one; the default space has every project.
        var named = fixture.Single(await fixture.OkAsync("canvas", "open", "global:tools/checklist", "--project", beta.Slug, "--space", "Default"), "alta.canvas.opened");
        Assert.AreEqual("default", named.GetProperty("spaceId").GetString());
        var refused = await fixture.FailsAsync(AltaExitCodes.Unsupported, "project.notInSpace", "canvas", "open", "global:tools/checklist", "--project", beta.Slug, "--space", "work");
        StringAssert.Contains(refused.GetProperty("message").GetString(), "alta space add work beta");
        Assert.AreEqual(3, fixture.Canvases.Opened.Count, "A refused request asks the window for nothing.");
    }

    [TestMethod]
    public async Task Open_TakesTheProjectOfTheCallingSession_AndNeedsOneOtherwise()
    {
        using var fixture = await Fixture.CreateAsync();
        var alpha = await fixture.AddProjectAsync("alpha");
        var caller = new AltaCallerIdentity { Kind = "session", SourceSessionId = "s1", SourceProjectId = alpha.Id };
        fixture.Single(await fixture.OkAsync(["canvas", "open", "global:tools/checklist"], caller: caller), "alta.canvas.opened");
        Assert.AreEqual(alpha.Id, fixture.Canvases.Opened.Single().Target.ProjectId);

        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingProject", "canvas", "open", "global:tools/checklist");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "project.notFound", "canvas", "open", "global:tools/checklist", "--project", "no-such-project");
        Assert.AreEqual(1, fixture.Canvases.Opened.Count);
    }

    [TestMethod]
    public async Task Open_ASessionCanvas_ResolvesTheSessionAndItsProject()
    {
        using var fixture = await Fixture.CreateAsync(sessions: true);
        var alpha = await fixture.AddProjectAsync("alpha");
        await fixture.SessionCatalog!.SaveInternalAsync(SessionOf(alpha, "session-1"));
        var opened = fixture.Single(await fixture.OkAsync("canvas", "open", "global:tools/board", "--session", "session-1", "--key", "main"), "alta.canvas.opened");
        var target = fixture.Canvases.Opened.Single().Target;
        Assert.AreEqual("session-1", target.SessionId);
        Assert.AreEqual(alpha.Id, target.ProjectId, "The window needs the project of the session to place the tab.");
        Assert.AreEqual("main", target.Key);
        Assert.AreEqual("session-1", opened.GetProperty("sessionId").GetString());

        var caller = new AltaCallerIdentity { Kind = "session", SourceSessionId = "session-1" };
        fixture.Single(await fixture.OkAsync(["canvas", "open", "global:tools/board"], caller: caller), "alta.canvas.opened");
        Assert.AreEqual("session-1", fixture.Canvases.Opened[1].Target.SessionId, "The calling session is the default.");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingSession", "canvas", "open", "global:tools/board");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "session.notFound", "canvas", "open", "global:tools/board", "--session", "nope");
    }

    [TestMethod]
    public async Task Open_NeedsAWindow_AValidInput_AndAnActivePlugin()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Canvases.Window = false;
        await fixture.FailsAsync(AltaExitCodes.ServiceUnavailable, "view.unavailable", "canvas", "open", "dashboard");
        Assert.AreEqual(0, fixture.Canvases.Opened.Count);
        // Reading and invoking need no window.
        fixture.Single(await fixture.OkAsync("canvas", "list"), "alta.canvas.summary");
        fixture.Single(await fixture.OkAsync("canvas", "show", "dashboard"), "alta.canvas.detail");
        fixture.Single(await fixture.OkAsync(["canvas", "invoke", "dashboard", "refresh"], stdin: null), "alta.canvas.result");

        fixture.Canvases.Window = true;
        var bad = await fixture.Dispatch(["canvas", "open", "dashboard", "--stdin"], stdin: "{not json");
        Assert.AreEqual(AltaExitCodes.Usage, bad.ExitCode);
        StringAssert.Contains(bad.Stdout, "usage.invalidInput");

        fixture.Canvases.OpenStatus = "plugin_stopped";
        await fixture.FailsAsync(AltaExitCodes.Unsupported, "canvas.pluginStopped", "canvas", "open", "dashboard");
        fixture.Canvases.OpenStatus = "unknown_canvas";
        await fixture.FailsAsync(AltaExitCodes.NotFound, "canvas.notFound", "canvas", "open", "dashboard");
    }

    [TestMethod]
    public async Task Open_WithAHostWithoutSpaces_LeavesTheSpaceToTheWindow()
    {
        using var fixture = await Fixture.CreateAsync(spaces: false);
        var opened = fixture.Single(await fixture.OkAsync("canvas", "open", "dashboard"), "alta.canvas.opened");
        Assert.IsNull(fixture.Canvases.Opened.Single().Target.SpaceId);
        Assert.IsTrue(opened.GetProperty("shown").GetBoolean());
        await fixture.FailsAsync(AltaExitCodes.Unsupported, "space.unavailable", "canvas", "open", "dashboard", "--space", "work");
    }

    [TestMethod]
    public async Task Focus_And_Close_NeedAnOpenTab()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.View.ShownSpaceId = "default";
        await fixture.FailsAsync(AltaExitCodes.NotFound, "canvas.notOpen", "canvas", "focus", "dashboard");
        await fixture.FailsAsync(AltaExitCodes.NotFound, "canvas.notOpen", "canvas", "close", "dashboard");

        fixture.Canvases.Instances.Add(new("i1", "builtin:statistics", "dashboard", "default", null, null, null, "Statistics", true));
        var focused = fixture.Single(await fixture.OkAsync("canvas", "focus", "dashboard"), "alta.canvas.focused");
        Assert.AreEqual("default", focused.GetProperty("spaceId").GetString());
        Assert.IsTrue(fixture.Canvases.Opened.Single().Focus);

        var closed = fixture.Single(await fixture.OkAsync("canvas", "close", "dashboard"), "alta.canvas.closed");
        Assert.AreEqual("dashboard", fixture.Canvases.Closed.Single().CanvasId);
        Assert.AreEqual("builtin:statistics/dashboard", closed.GetProperty("ref").GetString());
        Assert.AreEqual(0, fixture.Canvases.Instances.Count);
        await fixture.FailsAsync(AltaExitCodes.NotFound, "canvas.notOpen", "canvas", "close", "dashboard");

        fixture.Canvases.Window = false;
        await fixture.FailsAsync(AltaExitCodes.ServiceUnavailable, "view.unavailable", "canvas", "close", "dashboard");
    }

    [TestMethod]
    public async Task Invoke_RunsADeclaredAction_AndPrintsItsResult()
    {
        using var fixture = await Fixture.CreateAsync();
        var alpha = await fixture.AddProjectAsync("alpha");
        var result = fixture.Single(await fixture.OkAsync(["canvas", "invoke", "global:tools/checklist", "tick", "--project", alpha.Slug, "--stdin"], stdin: "{\"step\":\"tag\"}"), "alta.canvas.result");
        Assert.AreEqual("tick", result.GetProperty("action").GetString());
        Assert.AreEqual("tag", result.GetProperty("result").GetProperty("echo").GetProperty("step").GetString());
        var invoked = fixture.Canvases.Invoked.Single();
        Assert.AreEqual(alpha.Id, invoked.Target.ProjectId);
        Assert.AreEqual("tick", invoked.Action);

        var unknown = await fixture.FailsAsync(AltaExitCodes.NotFound, "canvas.actionNotFound", "canvas", "invoke", "global:tools/checklist", "untick", "--project", alpha.Slug);
        StringAssert.Contains(unknown.GetProperty("message").GetString(), "tick, reset");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.invalid", "canvas", "invoke", "global:tools/checklist");
        await fixture.FailsAsync(AltaExitCodes.Usage, "usage.missingProject", "canvas", "invoke", "global:tools/checklist", "tick");

        fixture.Canvases.InvokeStatus = "failed";
        await fixture.FailsAsync(AltaExitCodes.Failure, "canvas.failed", "canvas", "invoke", "global:tools/checklist", "tick", "--project", alpha.Slug);
        fixture.Canvases.InvokeStatus = "plugin_stopped";
        await fixture.FailsAsync(AltaExitCodes.Unsupported, "canvas.pluginStopped", "canvas", "invoke", "global:tools/checklist", "tick", "--project", alpha.Slug);
    }

    private static SessionViewDescriptor SessionOf(ProjectDescriptor project, string sessionId)
        => new()
        {
            SessionId = sessionId,
            Kind = SessionViewKind.InternalSession,
            ProviderId = ModelProviderIds.Codex.Value,
            ProviderKey = ModelProviderIds.Codex.Value,
            ProjectRef = project.Id,
            WorkingDirectory = project.ProjectPath,
            Title = "A session",
            Status = SessionViewStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastActiveAt = DateTimeOffset.UtcNow,
        };

    private static string? Type(JsonElement line) => line.GetProperty("type").GetString();

    private static List<JsonElement> Records(AltaCommandResult result)
    {
        var values = new List<JsonElement>();
        foreach (var line in result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var document = JsonDocument.Parse(line);
            values.Add(document.RootElement.Clone());
        }

        return values;
    }

    private sealed class FakeSpaceView : IAltaSpaceView
    {
        public string? ShownSpaceId { get; set; }

        public List<string> Shown { get; } = [];

        public bool Show(string spaceId)
        {
            Shown.Add(spaceId);
            ShownSpaceId = spaceId;
            return true;
        }

        public void NotifyChanged()
        {
        }
    }

    private sealed class FakeCanvasView(FakeSpaceView spaces) : IAltaCanvasView
    {
        public bool Window { get; set; } = true;

        public string OpenStatus { get; set; } = "requested";

        public string InvokeStatus { get; set; } = "ok";

        public List<AltaCanvasInstance> Instances { get; } = [];

        public List<(AltaCanvasTarget Target, JsonElement? Input, bool Focus)> Opened { get; } = [];

        public List<AltaCanvasTarget> Closed { get; } = [];

        public List<AltaCanvasTarget> Described { get; } = [];

        public List<(AltaCanvasTarget Target, string Action, JsonElement? Input)> Invoked { get; } = [];

        public bool HasWindow => Window;

        public IReadOnlyList<AltaCanvasDeclaration> List()
            =>
            [
                new("builtin:statistics", "Statistics", "dashboard", "Statistics", "What the sessions cost.", "chart", AltaCanvasScopes.Application, "{\"type\":\"object\"}", true,
                    [new("refresh", "Reads the numbers again.", null)]),
                new("global:tools", "Tools", "checklist", "Checklist", "Steps of a release.", "tick", AltaCanvasScopes.Project, null, true,
                    [new("tick", "Ticks a step.", "{\"type\":\"object\",\"properties\":{\"step\":{\"type\":\"string\"}}}"), new("reset", null, null)]),
                new("global:tools", "Tools", "board", "Board", null, null, AltaCanvasScopes.Session, null, false, []),
                new("project:x", "X", "checklist", "Checklist of X", null, null, AltaCanvasScopes.Project, null, false, []),
            ];

        public IReadOnlyList<AltaCanvasInstance> ListOpen() => [.. Instances];

        public ValueTask<AltaCanvasOpened> OpenAsync(AltaCanvasTarget target, JsonElement? input, bool focus, CancellationToken cancellationToken)
        {
            if (OpenStatus != "requested") return new(new AltaCanvasOpened(OpenStatus, null, null, false));
            Opened.Add((target, input, focus));
            var space = target.SpaceId ?? spaces.ShownSpaceId;
            return new(new AltaCanvasOpened("requested", "instance-" + Opened.Count, space, space is null || space == spaces.ShownSpaceId));
        }

        public ValueTask<bool> CloseAsync(AltaCanvasTarget target, CancellationToken cancellationToken)
        {
            Closed.Add(target);
            return new(Instances.RemoveAll(instance => instance.CanvasId == target.CanvasId) > 0);
        }

        public ValueTask<AltaCanvasDescription> DescribeAsync(AltaCanvasTarget target, CancellationToken cancellationToken)
        {
            Described.Add(target);
            return new(new AltaCanvasDescription("ok", target.CanvasId == "dashboard" ? "# Statistics" : $"Checklist of {target.ProjectId}"));
        }

        public ValueTask<AltaCanvasInvocation> InvokeAsync(AltaCanvasTarget target, string action, JsonElement? input, CancellationToken cancellationToken)
        {
            Invoked.Add((target, action, input));
            using var document = JsonDocument.Parse($"{{\"echo\":{(input is { } value ? value.GetRawText() : "null")}}}");
            return new(new AltaCanvasInvocation(InvokeStatus, InvokeStatus == "ok" ? document.RootElement.Clone() : null));
        }
    }

    private sealed class Fixture : IDisposable
    {
        // The commands are run by a caller that belongs to no session, as the MCP server of the window does.
        private static readonly AltaCallerIdentity Mcp = new() { Kind = "mcp" };

        private Fixture(string root)
        {
            Root = root;
            Options = new CatalogOptions { GlobalRoot = Path.Combine(root, "home") };
            Projects = new ProjectCatalog(Options);
            Spaces = new SpaceCatalog(Projects);
            Canvases = new FakeCanvasView(View);
        }

        public string Root { get; }

        public CatalogOptions Options { get; }

        public ProjectCatalog Projects { get; }

        public SpaceCatalog Spaces { get; }

        public FakeSpaceView View { get; } = new();

        public FakeCanvasView Canvases { get; }

        public SessionViewCatalog? SessionCatalog { get; private set; }

        private AltaCommandDispatcher Dispatcher { get; set; } = null!;

        public static Task<Fixture> CreateAsync(bool canvases = true, bool spaces = true, bool sessions = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "CodeAlta.AltaCanvasCommandsTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "home"));
            var fixture = new Fixture(root);
            var services = new AltaServiceCollection().Add(fixture.Options).Add(fixture.Projects);
            if (spaces) services.Add(fixture.Spaces).Add<IAltaSpaceView>(fixture.View);
            if (canvases) services.Add<IAltaCanvasView>(fixture.Canvases);
            if (sessions)
            {
                fixture.SessionCatalog = new SessionViewCatalog(fixture.Options);
                services.Add(fixture.SessionCatalog);
            }

            var registry = new AltaCommandRegistry();
            services.Add(registry);
            fixture.Dispatcher = new AltaCommandDispatcher(registry, services);
            return Task.FromResult(fixture);
        }

        public async Task<ProjectDescriptor> AddProjectAsync(string name)
        {
            var folder = Path.Combine(Root, "code", name);
            Directory.CreateDirectory(folder);
            return await Projects.UpsertFromPathAsync(folder);
        }

        public Task<AltaCommandResult> RunAsync(params string[] args)
            => Dispatcher.InvokeAsync(args, caller: Mcp).AsTask();

        public Task<AltaCommandResult> Dispatch(string[] args, string? stdin = null)
            => Dispatcher.InvokeAsync(args, stdin, Mcp, null).AsTask();

        public Task<AltaCommandResult> OkAsync(params string[] args) => OkAsync(args, stdin: null);

        public async Task<AltaCommandResult> OkAsync(string[] args, string? stdin = null, AltaCallerIdentity? caller = null, string? cwd = null)
        {
            var result = await Dispatcher.InvokeAsync(args, stdin, caller ?? Mcp, cwd);
            Assert.AreEqual(AltaExitCodes.Success, result.ExitCode, $"alta {string.Join(' ', args)}: {result.Stdout}");
            return result;
        }

        public async Task<JsonElement> FailsAsync(int exitCode, string code, params string[] args)
        {
            var result = await Dispatcher.InvokeAsync(args, caller: Mcp);
            Assert.AreEqual(exitCode, result.ExitCode, $"alta {string.Join(' ', args)}: {result.Stdout}");
            var error = Records(result).Single(static line => Type(line) == "alta.error");
            Assert.AreEqual(code, error.GetProperty("code").GetString(), string.Join(' ', args));
            return error;
        }

        public JsonElement Single(AltaCommandResult result, string type)
            => Records(result).Single(line => Type(line) == type);

        public void Dispose()
        {
            for (var attempt = 0; Directory.Exists(Root); attempt++)
            {
                try
                {
                    SqliteConnection.ClearAllPools();
                    Directory.Delete(Root, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }
}
