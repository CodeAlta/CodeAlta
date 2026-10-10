using System.Text.Json;
using System.Threading.Channels;
using CodeAlta.Desktop;
using CodeAlta.Desktop.Rpc;
using CodeAlta.LiveTool;
using CodeAlta.Plugins;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The calls of a canvas script to its plugin, end to end with a real source plugin: the <c>canvas-board</c> sample of the plugin runtime skill is
/// copied, built by the plugin runtime, opened as a canvas, and called through the service of the page the way its script calls it.
/// </summary>
[TestClass]
public sealed class CanvasRpcSourcePluginTests
{
    private const string Epoch = "epoch-1";

    [TestMethod]
    [TestCategory("RequiresDotNet10FileBuild")]
    public async Task TheBoardSample_ServesItsScriptThroughTheService_AndFollowsAReloadOfItsPlugin()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeAlta-canvas-rpc-" + Guid.NewGuid().ToString("N"));
        var global = Directory.CreateDirectory(Path.Combine(root, "home", ".alta")).FullName;
        var folder = Path.Combine(global, "plugins", "canvas-board");
        CopySample(Path.Combine(AppContext.BaseDirectory, "BuiltinSkills", "codealta-plugin-runtime", "samples", "canvas-board"), folder);
        var ui = new DesktopPluginUi();
        var broker = new DesktopCanvases(ui);
        ui.Modules = broker.Modules;
        var runtime = new PluginRuntimeManager();
        try
        {
            await runtime.StartAsync(new PluginRuntimeManagerOptions
            {
                GlobalRoot = global, Frontend = PluginFrontends.Desktop, IsHeadless = true,
                Services = new DesktopPluginServices(new PluginAltaServiceBridge(), ui, broker),
            });
            var package = runtime.GetPackages().Single(static candidate => candidate.Package.PackageId == "canvas-board");
            if (package.Build is { Succeeded: false } build && (build.StandardOutput + build.StandardError).Contains("The project file could not be loaded", StringComparison.OrdinalIgnoreCase))
                Assert.Inconclusive("The installed .NET SDK did not accept `dotnet build plugin.cs` file-based builds in this environment.");
            Assert.IsTrue(package.Build is not { Succeeded: false }, package.Build?.StandardOutput + package.Build?.StandardError);
            broker.Attach(runtime, () => "work");
            var service = new CanvasesService(broker, Epoch);
            var events = Channel.CreateUnbounded<CanvasEvent>();
            using var watching = new CancellationTokenSource();
            var pump = Task.Run(async () =>
            {
                try { await foreach (var value in service.WatchAsync(new(Epoch), watching.Token)) events.Writer.TryWrite(value); }
                catch (OperationCanceledException) { /* The test is over. */ }
            });
            var kept = new List<CanvasEvent>();
            async Task<CanvasEvent> NextAsync(Func<CanvasEvent, bool> wanted)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                foreach (var value in kept.ToArray())
                {
                    if (!wanted(value)) continue;
                    kept.Remove(value);
                    return value;
                }

                while (true)
                {
                    var value = await events.Reader.ReadAsync(timeout.Token);
                    if (wanted(value)) return value;
                    kept.Add(value);
                }
            }

            // The frames of an event that were not asked for yet wait here, in order.
            var frames = new List<(string Connection, JsonElement Frame)>();
            async Task<JsonElement> FrameAsync(string connection, Func<JsonElement, bool> wanted)
            {
                while (true)
                {
                    var index = frames.FindIndex(candidate => candidate.Connection == connection && wanted(candidate.Frame));
                    if (index >= 0)
                    {
                        var found = frames[index].Frame;
                        frames.RemoveAt(index);
                        return found;
                    }

                    var carried = await NextAsync(value => value.Kind == "rpc");
                    foreach (var text in carried.Frames!) frames.Add((carried.Connection!, JsonDocument.Parse(text).RootElement.Clone()));
                }
            }

            string Invoke(string id, string command, string args) => $$"""{"neoastra":1,"kind":"invoke","id":"{{id}}","command":"{{command}}","args":{{args}}}""";

            var key = runtime.ActivePlugins.Single().Descriptor.RuntimeKey;
            var opened = await service.OpenAsync(new(Epoch, key, "board", "work", null, null, null, true), default);
            Assert.AreEqual("ok", opened.Status);
            StringAssert.EndsWith(opened.Script, "/ui/board.js");
            var first = service.RpcOpen(new(Epoch, opened.InstanceId));
            Assert.AreEqual("ok", first.Status);

            // A call: the plugin's records, in camelCase.
            service.RpcSend(new(Epoch, opened.InstanceId, first.Connection, [Invoke("r1", "board.get", "null")]));
            var board = (await FrameAsync(first.Connection!, frame => frame.GetProperty("id").GetString() == "r1")).GetProperty("value");
            Assert.AreEqual(("Release board", 1, 3), (board.GetProperty("title").GetString(), board.GetProperty("revision").GetInt32(), board.GetProperty("columns").GetArrayLength()));
            Assert.AreEqual("Write the notes", board.GetProperty("columns")[0].GetProperty("cards")[0].GetProperty("title").GetString());

            // A stream, which pushes a new board when a call changes it, and an event that tells what changed.
            service.RpcSend(new(Epoch, opened.InstanceId, first.Connection, [Invoke("w1", "board.watch", "{}"), $$"""{"neoastra":1,"kind":"subscribe","id":"sub1","event":"{{CanvasRpcEndpoint.EventsName}}"}"""]));
            var channel = (await FrameAsync(first.Connection!, frame => frame.TryGetProperty("id", out var id) && id.GetString() == "w1")).GetProperty("value").GetProperty("channel").GetString()!;
            var snapshot = await FrameAsync(first.Connection!, frame => frame.GetProperty("kind").GetString() == "channel_item");
            Assert.AreEqual(1, snapshot.GetProperty("value").GetProperty("revision").GetInt32());
            await FrameAsync(first.Connection!, frame => frame.GetProperty("kind").GetString() == "subscribed");
            service.RpcSend(new(Epoch, opened.InstanceId, first.Connection, [$$"""{"neoastra":1,"kind":"channel_ack","channel":"{{channel}}","sequence":1}""",
                Invoke("a1", "board.add", """{"column":"To do","title":"Pushed"}""")]));
            var pushed = await FrameAsync(first.Connection!, frame => frame.GetProperty("kind").GetString() == "channel_item" && frame.GetProperty("sequence").GetInt32() == 2);
            Assert.AreEqual(2, pushed.GetProperty("value").GetProperty("revision").GetInt32());
            var changed = await FrameAsync(first.Connection!, frame => frame.GetProperty("kind").GetString() == "event");
            Assert.AreEqual(("board.changed", "Added \"Pushed\" to To do"), (changed.GetProperty("value").GetProperty("name").GetString(), changed.GetProperty("value").GetProperty("value").GetProperty("message").GetString()));

            // Errors: the plugin's own codes, a request that does not fit, and a failure whose text stays in the log.
            service.RpcSend(new(Epoch, opened.InstanceId, first.Connection, [Invoke("e1", "board.add", """{"column":"To do","title":""}"""), Invoke("e2", "board.move", """{"id":999,"column":"Done"}"""),
                Invoke("e3", "board.add", """{"column":3}"""), Invoke("e4", "board.explode", "{}"), Invoke("e5", "board.move", """{"id":1,"column":"Nowhere"}""")]));
            var codes = new Dictionary<string, string>();
            while (codes.Count < 5)
            {
                var answer = await FrameAsync(first.Connection!, frame => frame.GetProperty("kind").GetString() == "result" && frame.GetProperty("id").GetString()!.StartsWith('e'));
                codes[answer.GetProperty("id").GetString()!] = answer.GetProperty("error").GetProperty("code").GetString()!;
                Assert.IsFalse(answer.ToString().Contains("Not shown to the script", StringComparison.Ordinal));
            }

            Assert.AreEqual("invalid_card", codes["e1"]);
            Assert.AreEqual("not_found", codes["e2"]);
            Assert.AreEqual("invalid_request", codes["e3"]);
            Assert.AreEqual("internal_error", codes["e4"]);
            Assert.AreEqual("unknown_column", codes["e5"]);

            // A new version of the plugin: the connection of the old one ends, the instance is opened again, and the script connects to the new plugin.
            File.WriteAllText(Path.Combine(folder, "plugin.cs"), File.ReadAllText(Path.Combine(folder, "plugin.cs")).Replace("\"Release board\"", "\"Second board\"", StringComparison.Ordinal));
            Assert.AreEqual(PluginPackageChange.Reloaded, (await runtime.ReloadPackageAsync(package.Package)).Change);
            var ended = await NextAsync(value => value.Kind == "rpcClosed" && value.Connection == first.Connection);
            Assert.AreEqual(opened.InstanceId, ended.InstanceId);
            Assert.AreEqual("closed", service.RpcSend(new(Epoch, opened.InstanceId, first.Connection, [Invoke("old", "board.get", "{}")])).Status);
            await NextAsync(value => value.Kind == "update" && value.InstanceId == opened.InstanceId);
            var second = service.RpcOpen(new(Epoch, opened.InstanceId));
            Assert.AreEqual("ok", second.Status);
            service.RpcSend(new(Epoch, opened.InstanceId, second.Connection, [Invoke("r2", "board.get", "{}")]));
            var next = (await FrameAsync(second.Connection!, frame => frame.GetProperty("id").GetString() == "r2")).GetProperty("value");
            Assert.AreEqual(("Second board", 1), (next.GetProperty("title").GetString(), next.GetProperty("revision").GetInt32()), "a new plugin, a new board");

            watching.Cancel();
            await pump;
        }
        finally
        {
            broker.Dispose();
            await runtime.DisposeAsync();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* Best-effort cleanup of a temporary directory. */ }
        }
    }

    private static void CopySample(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
