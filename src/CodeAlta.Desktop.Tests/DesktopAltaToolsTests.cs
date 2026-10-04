using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Desktop;
using CodeAlta.LiveTool;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class DesktopAltaToolsTests
{
    [TestMethod]
    public async Task SessionTool_RunsAltaCommandsAsItsSession()
    {
        var notes = new Notes();
        var tools = DesktopAltaTools.CreateSessionTools(Dispatcher(notes));

        // A session that exists is named by the request; a session being created is learned from its first call.
        var known = tools(new("known-session", "project", "C:/work", "provider")).Single();
        var created = tools(new(null, null, "C:/work", "provider")).Single();
        var first = await known.Handler(Invocation("ignored", ["notes", "set", "--stdin"], "# Known"), default);
        var second = await created.Handler(Invocation("created-session", ["notes", "set", "--stdin"], "# Created"), default);

        Assert.IsTrue(first.Success, first.Error);
        Assert.IsTrue(second.Success, second.Error);
        CollectionAssert.AreEqual(new[] { ("known-session", "# Known"), ("created-session", "# Created") }, notes.Writes);
    }

    [TestMethod]
    public void SessionTool_HasTheSameSignatureForEverySession()
    {
        var tools = DesktopAltaTools.CreateSessionTools(Dispatcher(new Notes()));
        var left = tools(new("a", null, "C:/one", "p")).Single().Spec;
        var right = tools(new(null, "project", "C:/two", "q")).Single().Spec;

        // The runtime replaces a session's attachment when its tools differ by name, description or schema.
        Assert.AreEqual("alta", left.Name);
        Assert.AreEqual(left.Name, right.Name);
        Assert.AreEqual(left.Description, right.Description);
        Assert.AreEqual(left.InputSchema.GetRawText(), right.InputSchema.GetRawText());
    }

    [TestMethod]
    public async Task SessionTool_ReportsACommandThatHasNoService()
    {
        // Without an ask service the command fails as a tool result; it never throws into the run.
        var tool = DesktopAltaTools.CreateSessionTools(Dispatcher(new Notes()))(new("s", null, "C:/work", "p")).Single();

        var result = await tool.Handler(Invocation("s", ["ask", "--stdin"], """{"questions":[{"title":"T","question":"Q","freeform":{}}]}"""), default);

        Assert.IsFalse(result.Success);
    }

    private static AltaCommandDispatcher Dispatcher(Notes notes)
    {
        var services = new AltaServiceCollection().Add<IAltaNotesService>(notes);
        var registry = new AltaCommandRegistry();
        var dispatcher = new AltaCommandDispatcher(registry, services);
        services.Add(registry).Add(dispatcher);
        return dispatcher;
    }

    private static AgentToolInvocation Invocation(string session, string[] args, string stdin)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object> { ["args"] = args, ["stdin"] = stdin }));
        return new(new("provider"), session, "call", "alta", document.RootElement.Clone());
    }

    private sealed class Notes : IAltaNotesService
    {
        public List<(string Session, string Markdown)> Writes { get; } = [];

        public event EventHandler<AltaNotesChangedEventArgs>? Changed { add { } remove { } }

        public AltaCallerIdentity CaptureCaller(AltaCallerIdentity caller) => caller;

        public ValueTask<string> GetMarkdownAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Writes.LastOrDefault(write => write.Session == caller.SourceSessionId).Markdown ?? string.Empty);

        public ValueTask SetMarkdownAsync(string markdown, AltaCallerIdentity caller, CancellationToken cancellationToken = default)
        {
            Writes.Add((caller.SourceSessionId ?? string.Empty, markdown));
            return ValueTask.CompletedTask;
        }

        public ValueTask ClearAsync(AltaCallerIdentity caller, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
