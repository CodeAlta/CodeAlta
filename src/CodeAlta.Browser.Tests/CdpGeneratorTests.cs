using CodeAlta.Browser.Cdp.Generator;

namespace CodeAlta.Browser.Tests;

[TestClass]
public sealed class CdpGeneratorTests
{
    private const string Protocol = """
        {
          "domains": [
            {
              "domain": "Page",
              "types": [
                { "id": "FrameId", "type": "string" },
                { "id": "Frame", "type": "object", "description": "A frame <of> a page.", "properties": [
                  { "name": "id", "$ref": "FrameId" },
                  { "name": "parentId", "$ref": "FrameId", "optional": true },
                  { "name": "url", "type": "string" },
                  { "name": "extra", "type": "object" },
                  { "name": "kind", "$ref": "Runtime.Kind" }
                ] },
                { "id": "Unused", "type": "object", "properties": [ { "name": "x", "type": "integer" } ] }
              ],
              "commands": [
                { "name": "navigate", "parameters": [ { "name": "url", "type": "string" }, { "name": "referrer", "type": "string", "optional": true } ],
                  "returns": [ { "name": "frameId", "$ref": "FrameId" }, { "name": "frames", "type": "array", "items": { "$ref": "Frame" } } ] },
                { "name": "enable" },
                { "name": "reload", "parameters": [ { "name": "ignoreCache", "type": "boolean", "optional": true } ] }
              ],
              "events": [ { "name": "frameNavigated", "parameters": [ { "name": "frame", "$ref": "Frame" } ] } ]
            },
            { "domain": "Runtime", "types": [ { "id": "Kind", "type": "string", "enum": ["a", "b"] } ] }
          ]
        }
        """;

    [TestMethod]
    public void EmitsOnlyWhatTheMapReaches()
    {
        var protocol = CdpProtocol.Parse([Protocol]);
        var emitter = new CdpEmitter(protocol, new CdpMethodMap(["Page.navigate", "Page.enable", "Page.reload", "Page.missing"], ["Page.frameNavigated"]), "CodeAlta.Browser.Cdp");
        var files = emitter.EmitAll("abc123").ToDictionary(static file => file.FileName, static file => file.Content);

        CollectionAssert.AreEqual(new[] { "Page.missing is not in the protocol." }, emitter.Warnings);
        var page = files["Cdp.Page.gen.cs"];
        StringAssert.Contains(page, "internal sealed record PageFrame");
        Assert.IsFalse(page.Contains("PageUnused", StringComparison.Ordinal), "A type no command or event reaches is left out.");
        // A string alias is a string, an enum stays a string, an object without properties is JSON of any shape.
        StringAssert.Contains(page, "public string Id { get; init; } = \"\";");
        StringAssert.Contains(page, "public string? ParentId { get; init; }");
        StringAssert.Contains(page, "public string Kind { get; init; } = \"\";");
        StringAssert.Contains(page, "public JsonElement? Extra { get; init; }");
        StringAssert.Contains(page, "public PageFrame[] Frames { get; init; } = [];");
        StringAssert.Contains(page, "/// <summary>A frame &lt;of&gt; a page.</summary>");
        StringAssert.Contains(page, "public Task<PageNavigateResult> NavigateAsync(PageNavigateParams parameters, CancellationToken cancellationToken = default)");
        StringAssert.Contains(page, "public Task EnableAsync(PageEnableParams? parameters = null, CancellationToken cancellationToken = default)");
        StringAssert.Contains(page, "public Task ReloadAsync(PageReloadParams? parameters = null");
        StringAssert.Contains(page, "public IDisposable OnFrameNavigated(Action<PageFrameNavigatedEvent> handler)");
        Assert.IsFalse(page.Contains("required", StringComparison.Ordinal), "No member is required: a browser that leaves one out is still read.");

        StringAssert.Contains(files["CdpJsonContext.gen.cs"], "[JsonSerializable(typeof(PageNavigateParams))]");
        StringAssert.Contains(files["CdpJsonContext.gen.cs"], "[JsonSerializable(typeof(PageFrameNavigatedEvent))]");
        StringAssert.Contains(files["CdpSession.Domains.gen.cs"], "public CdpPageDomain Page => new(this);");
        StringAssert.Contains(files["Cdp.Metadata.gen.cs"], "public const string Revision = \"abc123\";");
    }

    [TestMethod]
    public void TheCheckedInBindingsComeFromThePinnedRevision()
    {
        Assert.AreEqual(40, CodeAlta.Browser.Cdp.CdpProtocolInfo.Revision.Length, "A commit of ChromeDevTools/devtools-protocol.");
        var map = CdpMethodMap.Load(Path.Combine(AppContext.BaseDirectory, "cdp-methods.json"));
        Assert.AreEqual(CodeAlta.Browser.Cdp.CdpProtocolInfo.CommandCount, map.Commands.Count, "The bindings are generated from the method map: run the generator after a change of it.");
        Assert.AreEqual(CodeAlta.Browser.Cdp.CdpProtocolInfo.EventCount, map.Events.Count);
    }
}
