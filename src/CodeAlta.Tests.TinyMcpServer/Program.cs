using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var settings = TinyMcpServerSettings.FromEnvironment();
// One file per start of the process, for the tests that count how many times a server is started: processes started at
// the same time never write the same file.
if (!string.IsNullOrWhiteSpace(settings.StartDirectory))
{
    Directory.CreateDirectory(settings.StartDirectory);
    File.WriteAllText(Path.Combine(settings.StartDirectory, $"{Environment.ProcessId}-{Guid.NewGuid():N}.start"), string.Empty);
}

if (!string.IsNullOrWhiteSpace(settings.StderrLine))
{
    Console.Error.WriteLine(settings.StderrLine);
    Console.Error.Flush();
}

var viewState = new TinyMcpView.State();
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services
    .AddMcpServer(options =>
    {
        // The skills of the extension are specified against the revision that stamps `_meta` on every request.
        options.ProtocolVersion = settings.Skills ? null : "2025-06-18";
        options.ServerInfo = new Implementation { Name = "codealta-tiny-mcp", Version = "1.0.0" };
        options.Capabilities = new ServerCapabilities
        {
            Tools = new ToolsCapability { ListChanged = false },
            Resources = settings.UiTools || settings.Skills ? new ResourcesCapability() : null,
            Extensions = settings.Skills ? new Dictionary<string, object> { [TinyMcpSkills.ExtensionId] = TinyMcpSkills.CreateCapability() } : null,
        };
#pragma warning disable MCPEXP002 // The methods of the extension are custom requests.
        if (settings.Skills) options.RequestHandlers = TinyMcpSkills.CreateHandlers(settings);
#pragma warning restore MCPEXP002
    })
    .WithStdioServerTransport()
    .WithListToolsHandler(async (request, cancellationToken) =>
    {
        await settings.DelayAsync(cancellationToken).ConfigureAwait(false);
        settings.WriteLog("LIST");
        var extensions = request.Server.ClientCapabilities?.Extensions;
        if (settings.UiTools)
        {
            // Whether the client said it shows the views of MCP Apps, for the tests of the capability.
            settings.WriteLog(extensions is not null && extensions.ContainsKey(TinyMcpView.ExtensionId) ? "CLIENT_UI yes" : "CLIENT_UI no");
        }

        if (settings.Skills)
        {
            // Whether the client said it loads skills, for the tests of the capability.
            settings.WriteLog(extensions is not null && extensions.ContainsKey(TinyMcpSkills.ExtensionId) ? "CLIENT_SKILLS yes" : "CLIENT_SKILLS no");
        }

        return new ListToolsResult { Tools = TinyMcpTools.CreateTools(settings) };
    })
    .WithCallToolHandler(async (request, cancellationToken) =>
    {
        await settings.DelayAsync(cancellationToken).ConfigureAwait(false);
        settings.WriteLog($"CALL {request.Params?.Name}");
        return TinyMcpTools.Call(request.Params, viewState);
    })
    .WithListResourcesHandler((_, _) => ValueTask.FromResult(new ListResourcesResult { Resources = settings.UiTools ? TinyMcpView.CreateResources() : [] }))
    .WithReadResourceHandler((request, _) =>
    {
        settings.WriteLog($"READ {request.Params?.Uri}");
        return ValueTask.FromResult((settings.Skills ? TinyMcpSkills.Read(request.Params?.Uri, settings.TamperSkills) : null)
            ?? TinyMcpView.Read(request.Params?.Uri, settings.HostileView));
    });

await builder.Build().RunAsync().ConfigureAwait(false);

internal sealed record TinyMcpServerSettings(string? LogPath, string? StderrLine, int DelayMs, string? ExtraTool, string? ExtraTools, bool RichTools, bool IncompatibleTool)
{
    public string? StartDirectory { get; init; }

    public static TinyMcpServerSettings FromEnvironment()
    {
        var delayMs = 0;
        _ = int.TryParse(Environment.GetEnvironmentVariable("MCP_TEST_DELAY_MS"), out delayMs);
        return new TinyMcpServerSettings(
            Environment.GetEnvironmentVariable("MCP_TEST_LOG"),
            Environment.GetEnvironmentVariable("MCP_TEST_STDERR"),
            Math.Max(0, delayMs),
            Environment.GetEnvironmentVariable("MCP_TEST_EXTRA_TOOL"),
            Environment.GetEnvironmentVariable("MCP_TEST_EXTRA_TOOLS"),
            string.Equals(Environment.GetEnvironmentVariable("MCP_TEST_RICH_TOOLS"), "1", StringComparison.Ordinal),
            string.Equals(Environment.GetEnvironmentVariable("MCP_TEST_INCOMPATIBLE_TOOL"), "1", StringComparison.Ordinal))
        {
            StartDirectory = Environment.GetEnvironmentVariable("MCP_TEST_START_DIR"),
            UiTools = string.Equals(Environment.GetEnvironmentVariable("MCP_TEST_UI_TOOLS"), "1", StringComparison.Ordinal),
            HostileView = string.Equals(Environment.GetEnvironmentVariable("MCP_TEST_UI_HOSTILE"), "1", StringComparison.Ordinal),
            Skills = string.Equals(Environment.GetEnvironmentVariable("MCP_TEST_SKILLS"), "1", StringComparison.Ordinal),
            TamperSkills = string.Equals(Environment.GetEnvironmentVariable("MCP_TEST_SKILLS_TAMPER"), "1", StringComparison.Ordinal),
        };
    }

    // The skills of the MCP Skills extension (MCP_TEST_SKILLS=1).
    public bool Skills { get; init; }

    // A file of a skill served with other bytes than its digest's (MCP_TEST_SKILLS_TAMPER=1).
    public bool TamperSkills { get; init; }

    // The tools and the view of MCP Apps (MCP_TEST_UI_TOOLS=1): a tool with a view, a tool of the view only, a tool of the model only.
    public bool UiTools { get; init; }

    // The view tries what a view must not do (MCP_TEST_UI_HOSTILE=1).
    public bool HostileView { get; init; }

    public Task DelayAsync(CancellationToken cancellationToken)
        => DelayMs > 0 ? Task.Delay(DelayMs, cancellationToken) : Task.CompletedTask;

    public void WriteLog(string message)
    {
        if (string.IsNullOrWhiteSpace(LogPath))
        {
            return;
        }

        File.AppendAllText(LogPath, message + Environment.NewLine);
    }

    public IEnumerable<string> SplitExtraTools()
        => string.IsNullOrWhiteSpace(ExtraTools)
            ? []
            : ExtraTools.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal static class TinyMcpTools
{
    public static List<Tool> CreateTools(TinyMcpServerSettings settings)
    {
        var tools = new List<Tool>
        {
            new()
            {
                Name = "echo",
                Title = "Echo",
                Description = "Echo input text.",
                InputSchema = ParseSchema("""
                    { "type": "object", "properties": { "text": { "type": "string" } } }
                    """),
            },
            new()
            {
                Name = "disabled",
                Description = "Tool disabled by policy in tests.",
                InputSchema = ParseSchema("""{ "type": "object" }"""),
            },
            new()
            {
                Name = "secret",
                Description = "Returns a secret-shaped value for redaction tests.",
                InputSchema = ParseSchema("""{ "type": "object" }"""),
            },
        };

        if (!string.IsNullOrWhiteSpace(settings.ExtraTool))
        {
            tools.Add(new Tool
            {
                Name = settings.ExtraTool,
                Description = "Extra env-controlled tool for cache-key tests.",
                InputSchema = ParseSchema("""{ "type": "object" }"""),
            });
        }

        foreach (var extraTool in settings.SplitExtraTools())
        {
            tools.Add(new Tool
            {
                Name = extraTool,
                Description = "Extra env-controlled tool for collision tests.",
                InputSchema = ParseSchema("""{ "type": "object" }"""),
            });
        }

        if (settings.RichTools)
        {
            tools.AddRange([
                new Tool
                {
                    Name = "rich",
                    Description = "Returns mixed content and structured data.",
                    InputSchema = ParseSchema("""{ "type": "object" }"""),
                },
                new Tool
                {
                    Name = "error",
                    Description = "Returns an MCP isError result.",
                    InputSchema = ParseSchema("""{ "type": "object" }"""),
                },
                new Tool
                {
                    Name = "long",
                    Description = "Returns content that should be truncated.",
                    InputSchema = ParseSchema("""{ "type": "object" }"""),
                },
            ]);
        }

        if (settings.UiTools)
        {
            tools.AddRange(TinyMcpView.CreateTools());
        }

        if (settings.IncompatibleTool)
        {
            tools.Add(new Tool
            {
                Name = "editJiraIssue",
                Description = "Updates fields on a Jira issue using a dynamic fields map.",
                InputSchema = ParseSchema("""
                    {
                      "type": "object",
                      "required": ["fields"],
                      "additionalProperties": {
                        "type": "object",
                        "additionalProperties": true
                      }
                    }
                    """),
            });
        }

        return tools;
    }

    public static CallToolResult Call(CallToolRequestParams? request, TinyMcpView.State viewState)
    {
        if (request is null)
        {
            throw new McpProtocolException("Missing tool call parameters.", McpErrorCode.InvalidParams);
        }

        return request.Name switch
        {
            "echo" => CreateEchoResult(GetStringArgument(request, "text")),
            "secret" => TextResult("Bearer secret-token-value-1234567890"),
            "disabled" => TextResult("should not be called", isError: true),
            "rich" => CreateRichResult(),
            "error" => TextResult("failure text", isError: true),
            "long" => CreateLongResult(),
            "editJiraIssue" => TextResult("jira:" + JsonSerializer.Serialize(
                request.Arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal))),
            _ when IsExtraTool(request.Name) => TextResult("extra:" + request.Name),
            _ when TinyMcpView.Call(request, viewState) is { } viewResult => viewResult,
            _ => throw new McpProtocolException("Unknown tool " + request.Name, McpErrorCode.InvalidParams),
        };
    }

    private static bool IsExtraTool(string toolName)
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MCP_TEST_EXTRA_TOOLS")) &&
           Environment.GetEnvironmentVariable("MCP_TEST_EXTRA_TOOLS")!
               .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Contains(toolName, StringComparer.Ordinal);

    private static CallToolResult CreateEchoResult(string text)
        => new()
        {
            Content = [new TextContentBlock { Text = "echo:" + text }],
            StructuredContent = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["echoed"] = text }),
        };

    private static CallToolResult CreateRichResult()
        => new()
        {
            Content =
            [
                new TextContentBlock { Text = "alpha" },
                // A real image, one pixel wide: a client that looks at it can decode it.
                ImageContentBlock.FromBytes(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="), "image/png"),
                AudioContentBlock.FromBytes(new byte[] { 5, 6, 7 }, "audio/wav"),
                new ResourceLinkBlock { Uri = "file:///tmp/example.txt", Name = "example", MimeType = "text/plain" },
                new EmbeddedResourceBlock
                {
                    Resource = new TextResourceContents
                    {
                        Uri = "file:///tmp/embedded.txt",
                        MimeType = "text/plain",
                        Text = "embedded text",
                    },
                },
            ],
            StructuredContent = JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["visible"] = "ok",
                ["apiToken"] = "secret-structured-value",
            }),
        };

    private static CallToolResult CreateLongResult()
        => new()
        {
            Content = [new TextContentBlock { Text = "abcdefghijklmnopqrstuvwxyz" }],
            StructuredContent = JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["apiToken"] = "secret-structured-value",
                ["visible"] = "too-large",
            }),
        };

    private static CallToolResult TextResult(string text, bool? isError = null)
        => new() { IsError = isError, Content = [new TextContentBlock { Text = text }] };

    private static string GetStringArgument(CallToolRequestParams request, string name)
    {
        if (request.Arguments is not null &&
            request.Arguments.TryGetValue(name, out var argument) &&
            argument.ValueKind == JsonValueKind.String)
        {
            return argument.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static JsonElement ParseSchema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
