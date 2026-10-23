using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

// The MCP Apps part of the tiny server (MCP_TEST_UI_TOOLS=1): `show_view` has a view, `view_refresh` is for the view only,
// `model_only` is for the model only. The view is a small page that speaks the protocol of MCP Apps without any library.
internal static class TinyMcpView
{
    public const string ExtensionId = "io.modelcontextprotocol/ui";
    public const string ViewUri = "ui://tiny/view";
    public const string PlainUri = "ui://tiny/plain";
    private const string HtmlMimeType = "text/html;profile=mcp-app";

    public static IEnumerable<Tool> CreateTools()
    {
        yield return new Tool
        {
            Name = "show_view",
            Title = "Show view",
            Description = "Shows a small interactive view.",
            InputSchema = Parse("""{ "type": "object", "properties": { "title": { "type": "string" } } }"""),
            Meta = new JsonObject { ["ui"] = new JsonObject { ["resourceUri"] = ViewUri } },
        };
        yield return new Tool
        {
            Name = "view_refresh",
            Description = "Refreshes the view; only the view calls it.",
            InputSchema = Parse("""{ "type": "object" }"""),
            Meta = new JsonObject { ["ui"] = new JsonObject { ["resourceUri"] = ViewUri, ["visibility"] = new JsonArray("app") } },
        };
        yield return new Tool
        {
            Name = "model_only",
            Description = "Only the model calls it.",
            InputSchema = Parse("""{ "type": "object" }"""),
            Meta = new JsonObject { ["ui"] = new JsonObject { ["visibility"] = new JsonArray("model") } },
        };
    }

    // The state of a view: how many times it refreshed, which a refresh shows.
    public sealed class State
    {
        private int _refreshCount;

        public int Refresh() => Interlocked.Increment(ref _refreshCount);
    }

    public static CallToolResult? Call(CallToolRequestParams request, State state)
        => request.Name switch
        {
            "show_view" => new CallToolResult
            {
                Content = [new TextContentBlock { Text = "Showed the view." }],
                StructuredContent = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    ["title"] = request.Arguments is { } arguments && arguments.TryGetValue("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString()! : "Tiny view",
                    ["items"] = new[] { "alpha", "beta", "gamma" },
                    // A name the redaction of the model's copy replaces: the view gets it as the server sent it.
                    ["apiKey"] = "kept-for-the-view",
                }),
            },
            "view_refresh" => new CallToolResult
            {
                Content = [new TextContentBlock { Text = "Refreshed." }],
                StructuredContent = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["count"] = state.Refresh() }),
            },
            "model_only" => new CallToolResult { Content = [new TextContentBlock { Text = "model only" }] },
            _ => null,
        };

    public static List<Resource> CreateResources() =>
    [
        new Resource
        {
            Uri = ViewUri,
            Name = "tiny_view",
            MimeType = HtmlMimeType,
            // The listing declares what the view needs; its content does not, so a host reads it here.
            Meta = new JsonObject { ["ui"] = new JsonObject { ["csp"] = new JsonObject { ["connectDomains"] = new JsonArray("https://api.example.com") }, ["prefersBorder"] = true } },
        },
        new Resource { Uri = PlainUri, Name = "tiny_plain", MimeType = "text/plain" },
    ];

    public static ReadResourceResult Read(string? uri, bool hostile) => uri switch
    {
        ViewUri => new ReadResourceResult { Contents = [new TextResourceContents { Uri = ViewUri, MimeType = HtmlMimeType, Text = hostile ? HostileHtml : Html }] },
        PlainUri => new ReadResourceResult { Contents = [new TextResourceContents { Uri = PlainUri, MimeType = "text/plain", Text = "not a view" }] },
        _ => throw new McpProtocolException("Unknown resource " + uri, McpErrorCode.InvalidParams),
    };

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // The protocol of a view, shared by both pages: requests and notifications to the host, and the messages of the host.
    private const string Protocol = """
        let nextId = 1;
        const pending = new Map();
        function request(method, params) {
          const id = nextId++;
          parent.postMessage({ jsonrpc: "2.0", id, method, params }, "*");
          return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
        }
        function notify(method, params) { parent.postMessage({ jsonrpc: "2.0", method, params }, "*"); }
        function show(id, text) { document.getElementById(id).textContent = text; }
        let context = {};
        function applyContext(update) {
          context = { ...context, ...update };
          for (const [name, value] of Object.entries(context.styles?.variables ?? {})) if (value) document.documentElement.style.setProperty(name, value);
          if (context.theme) document.documentElement.style.colorScheme = context.theme;
          document.body.dataset.theme = context.theme ?? "";
          document.body.dataset.mode = context.displayMode ?? "";
        }
        const handlers = {};
        window.addEventListener("message", (event) => {
          if (event.source !== parent) return;
          const message = event.data;
          if (!message || message.jsonrpc !== "2.0") return;
          if (message.method === undefined && message.id !== undefined) {
            const entry = pending.get(message.id);
            if (!entry) return;
            pending.delete(message.id);
            if (message.error) entry.reject(message.error); else entry.resolve(message.result);
            return;
          }
          if (message.method === "ui/notifications/host-context-changed") applyContext(message.params ?? {});
          if (message.id !== undefined && (message.method === "ui/resource-teardown" || message.method === "ping")) {
            parent.postMessage({ jsonrpc: "2.0", id: message.id, result: {} }, "*");
          }
          handlers[message.method]?.(message.params ?? {});
        });
        async function connect() {
          const result = await request("ui/initialize", {
            protocolVersion: "2026-01-26",
            appInfo: { name: "tiny-view", version: "1.0.0" },
            appCapabilities: { availableDisplayModes: ["inline", "fullscreen"] },
          });
          applyContext(result.hostContext ?? {});
          notify("ui/notifications/initialized", {});
          new ResizeObserver(() => notify("ui/notifications/size-changed", { width: document.documentElement.scrollWidth, height: document.documentElement.scrollHeight })).observe(document.documentElement);
          return result;
        }
        """;

    private const string Style = """
        <style>
          :root { color-scheme: light dark; --color-background-primary: light-dark(#ffffff, #1e1e1e); --color-text-primary: light-dark(#1b1b1b, #ececec);
                  --color-text-secondary: light-dark(#555, #aaa); --color-border-primary: light-dark(#d0d0d0, #444); --font-sans: system-ui, sans-serif; }
          body { margin: 0; padding: 12px 14px; background: var(--color-background-primary); color: var(--color-text-primary); font: 13px/1.5 var(--font-sans); }
          h3 { margin: 0 0 6px; font-size: 15px; }
          p { margin: 4px 0; color: var(--color-text-secondary); }
          button { font: inherit; color: inherit; background: transparent; border: 1px solid var(--color-border-primary); border-radius: 4px; padding: 2px 10px; margin: 6px 6px 0 0; cursor: pointer; }
          li { margin: 2px 0; }
        </style>
        """;

    private const string Html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Tiny view</title>" + Style + """
        </head><body>
          <h3 id="title">Tiny view</h3>
          <p id="input">Waiting for the input…</p>
          <p id="result">Waiting for the result…</p>
          <p id="refresh"></p>
          <ul id="items"></ul>
          <div>
            <button id="refreshButton">Refresh</button><button id="send">Send</button><button id="grow">Grow</button><button id="mode">Fullscreen</button>
          </div>
          <script>
        """ + Protocol + """
          handlers["ui/notifications/tool-input"] = (params) => show("input", "Input: " + JSON.stringify(params.arguments ?? {}));
          handlers["ui/notifications/tool-result"] = (params) => {
            const data = params.structuredContent ?? {};
            show("title", data.title ?? "Tiny view");
            show("result", "Result: " + (params.content?.[0]?.text ?? "") + " (apiKey: " + (data.apiKey ?? "none") + ")");
            const list = document.getElementById("items");
            list.replaceChildren(...(data.items ?? []).map((item) => Object.assign(document.createElement("li"), { textContent: item })));
          };
          document.getElementById("refreshButton").onclick = async () => {
            try {
              const result = await request("tools/call", { name: "view_refresh", arguments: {} });
              show("refresh", "Refreshed " + result.structuredContent?.count + " time(s).");
            } catch (error) { show("refresh", "Refresh refused: " + (error?.message ?? error)); }
          };
          document.getElementById("send").onclick = async () => {
            try { await request("ui/message", { role: "user", content: [{ type: "text", text: "Hello from the tiny view." }] }); show("refresh", "Message sent."); }
            catch (error) { show("refresh", "Message refused: " + (error?.message ?? error)); }
          };
          document.getElementById("grow").onclick = () => {
            document.getElementById("items").append(Object.assign(document.createElement("li"), { textContent: "item " + (document.getElementById("items").children.length + 1) }));
          };
          document.getElementById("mode").onclick = async () => {
            const result = await request("ui/request-display-mode", { mode: context.displayMode === "fullscreen" ? "inline" : "fullscreen" });
            applyContext({ displayMode: result.mode });
            document.getElementById("mode").textContent = result.mode === "fullscreen" ? "Inline" : "Fullscreen";
          };
          connect();
          </script>
        </body></html>
        """;

    // Tries what a view must not reach, and shows what happened to each attempt.
    private const string HostileHtml = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Hostile view</title>" + Style + """
        </head><body>
          <h3>Hostile view</h3>
          <ul id="report"></ul>
          <script>
        """ + Protocol + """
          function report(name, outcome) { document.getElementById("report").append(Object.assign(document.createElement("li"), { textContent: name + ": " + outcome, id: name })); }
          (async () => {
            await connect();
            try { report("top", "reached " + String(window.top.location.href)); } catch { report("top", "blocked"); }
            try { report("bridge", typeof window.chrome?.webview?.postMessage === "function" ? "present" : "absent"); } catch { report("bridge", "blocked"); }
            try { await fetch("https://undeclared.example.org/"); report("fetch", "reached"); } catch { report("fetch", "blocked"); }
            try { await request("tools/call", { name: "model_only", arguments: {} }); report("modelOnly", "called"); } catch (error) { report("modelOnly", "refused " + (error?.code ?? "")); }
            try { await request("tools/call", { name: "show_view", arguments: {} }); report("appVisible", "called"); } catch (error) { report("appVisible", "refused " + (error?.code ?? "")); }
          })();
          </script>
        </body></html>
        """;
}
