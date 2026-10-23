using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// The MCP Skills part of the tiny server (MCP_TEST_SKILLS=1): `skills/list` in two pages, `skills/get`, and
// `resources/directory/read`, over the example skills of the spec (`pdf-processing` with its exact digests), a nested skill,
// a `dynamic` one, two skills of the same name, one named like a built-in skill of CodeAlta and one the listing leaves out.
// MCP_TEST_SKILLS_TAMPER=1 serves `templates/invoice.md` with bytes that are not the ones of its digest.
internal static class TinyMcpSkills
{
    public const string ExtensionId = "io.modelcontextprotocol/skills";
    private const string DirectoryMimeType = "inode/directory";

    private sealed record Skill(string Root, JsonObject Frontmatter, IReadOnlyList<(string Path, string Text)> Files, bool Dynamic = false, bool Listed = true)
    {
        public string Uri => Root + "/SKILL.md";
    }

    private static readonly Skill[] Skills =
    [
        new("skill://pdf-processing",
            new JsonObject { ["name"] = "pdf-processing", ["description"] = "Extract, fill, and assemble PDF documents" },
            [
                ("SKILL.md", "---\nname: pdf-processing\ndescription: Extract, fill, and assemble PDF documents\n---\n\n# PDF processing\n\nChoose the matching template from `templates/`.\n"),
                ("templates/invoice.md", "# Invoice\n\nCustomer:\nAmount:\n"),
                ("templates/purchase-order.md", "# Purchase order\n\nSupplier:\nItems:\n"),
            ]),
        new("skill://acme/billing/refunds",
            new JsonObject
            {
                ["name"] = "refunds", ["description"] = "Process customer refund requests per company policy", ["license"] = "Apache-2.0",
                ["allowed-tools"] = "Bash(curl:*)",
            },
            [
                ("SKILL.md", "---\nname: refunds\ndescription: Process customer refund requests per company policy\nlicense: Apache-2.0\nallowed-tools: Bash(curl:*)\n---\n\n# Refunds\n\nAnswer with the template of `examples/email.md`. Escalations follow `escalation/SKILL.md`.\n"),
                ("examples/email.md", "Dear customer,\n\nYour refund has been processed.\n"),
                // The nested skill is a supporting file of the enclosing one: the manifest lists it too.
                ("escalation/SKILL.md", EscalationSkill),
            ]),
        new("skill://acme/billing/refunds/escalation",
            new JsonObject { ["name"] = "escalation", ["description"] = "Escalate a refund the policy does not cover" },
            [("SKILL.md", EscalationSkill)]),
        new("skill://acme/support/refunds",
            new JsonObject { ["name"] = "refunds", ["description"] = "Answer questions about the refund policy" },
            [("SKILL.md", "---\nname: refunds\ndescription: Answer questions about the refund policy\n---\n\n# Support refunds\n")]),
        new("skill://reports/daily",
            new JsonObject
            {
                ["name"] = "daily", ["description"] = "Assemble today's operational report from live data",
                ["metadata"] = new JsonObject { ["version"] = 2, ["beta"] = true },
            },
            [
                ("SKILL.md", "---\nname: daily\ndescription: Assemble today's operational report from live data\nmetadata:\n  version: 2\n  beta: true\n---\n\n# Daily report\n\nRead the figures of `data/today.md`.\n"),
                ("data/today.md", "# Today\n\nOrders: 42\n"),
            ],
            Dynamic: true),
        new("skill://tools/document-conversion",
            new JsonObject { ["name"] = "document-conversion", ["description"] = "Convert documents with the server's own converter" },
            [("SKILL.md", "---\nname: document-conversion\ndescription: Convert documents with the server's own converter\n---\n\n# Conversion\n")]),
        new("skill://hidden/unlisted",
            new JsonObject { ["name"] = "unlisted", ["description"] = "A skill the listing leaves out" },
            [("SKILL.md", "---\nname: unlisted\ndescription: A skill the listing leaves out\n---\n\n# Unlisted\n")],
            Listed: false),
    ];

    private const string EscalationSkill = "---\nname: escalation\ndescription: Escalate a refund the policy does not cover\n---\n\n# Escalation\n\nWrite to the billing lead.\n";

    private const int PageSize = 3;

    public static JsonObject CreateCapability() => new() { ["directoryRead"] = true };

#pragma warning disable MCPEXP002 // The methods of the extension are custom requests.
    public static IList<McpServerRequestHandler> CreateHandlers(TinyMcpServerSettings settings) =>
    [
        new() { Method = "skills/list", Handler = (request, _) => ValueTask.FromResult<JsonNode?>(List(request, settings)) },
        new() { Method = "skills/get", Handler = (request, _) => ValueTask.FromResult<JsonNode?>(Get(request, settings)) },
        new() { Method = "resources/directory/read", Handler = (request, _) => ValueTask.FromResult<JsonNode?>(ReadDirectory(request, settings)) },
    ];
#pragma warning restore MCPEXP002

    // The content of a file of a skill, or null when the URI is not one.
    public static ReadResourceResult? Read(string? uri, bool tamper)
    {
        foreach (var skill in Skills)
        {
            foreach (var (path, text) in skill.Files)
            {
                if (!string.Equals(skill.Root + "/" + path, uri, StringComparison.Ordinal)) continue;
                var served = tamper && path == "templates/invoice.md" ? text.Replace("Amount", "Total", StringComparison.Ordinal) : text;
                return new ReadResourceResult
                {
                    Contents = [new TextResourceContents { Uri = uri!, MimeType = path.EndsWith(".md", StringComparison.Ordinal) ? "text/markdown" : "text/plain", Text = served }],
                };
            }
        }

        return null;
    }

    private static JsonObject List(JsonRpcRequest request, TinyMcpServerSettings settings)
    {
        var cursor = (request.Params as JsonObject)?["cursor"]?.GetValue<string>();
        settings.WriteLog("SKILLS_LIST " + (cursor ?? "-"));
        // The last entry is invalid (its name is not the last segment of its path): a client leaves it out.
        JsonNode[] listed =
        [
            .. Skills.Where(static skill => skill.Listed).Select(static skill => (JsonNode)Entry(skill)),
            new JsonObject
            {
                ["uri"] = "skill://broken/mismatch/SKILL.md",
                ["frontmatter"] = new JsonObject { ["name"] = "other", ["description"] = "Named unlike its path" },
                ["resources"] = "dynamic",
            },
        ];
        var start = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
        var page = listed.Skip(start).Take(PageSize).ToArray();
        var result = new JsonObject
        {
            ["resultType"] = "complete",
            ["skills"] = new JsonArray([.. page.Select(static entry => entry.DeepClone())]),
            ["ttlMs"] = 300000,
            ["cacheScope"] = "public",
        };
        if (start + PageSize < listed.Length) result["nextCursor"] = (start + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return result;
    }

    private static JsonObject Get(JsonRpcRequest request, TinyMcpServerSettings settings)
    {
        var uri = (request.Params as JsonObject)?["uri"]?.GetValue<string>();
        settings.WriteLog("SKILLS_GET " + uri);
        var skill = Skills.FirstOrDefault(skill => string.Equals(skill.Uri, uri, StringComparison.Ordinal))
            ?? throw new McpProtocolException("No skill is served at " + uri, McpErrorCode.InvalidParams);
        return new JsonObject { ["resultType"] = "complete", ["skill"] = Entry(skill), ["ttlMs"] = 300000, ["cacheScope"] = "public" };
    }

    private static JsonObject ReadDirectory(JsonRpcRequest request, TinyMcpServerSettings settings)
    {
        var uri = (request.Params as JsonObject)?["uri"]?.GetValue<string>() ?? string.Empty;
        settings.WriteLog("DIR " + uri);
        var prefix = uri + "/";
        var children = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var skill in Skills)
        {
            foreach (var (path, _) in skill.Files)
            {
                var file = skill.Root + "/" + path;
                if (!file.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var rest = file[prefix.Length..];
                var slash = rest.IndexOf('/', StringComparison.Ordinal);
                if (slash < 0) children[rest] = false;
                else children.TryAdd(rest[..slash], true);
            }
        }

        if (children.Count == 0) throw new McpProtocolException(uri + " is not a directory resource", McpErrorCode.InvalidParams);
        return new JsonObject
        {
            ["resultType"] = "complete",
            ["resources"] = new JsonArray([.. children.Select(child => (JsonNode)new JsonObject
            {
                ["uri"] = prefix + child.Key,
                ["name"] = child.Key,
                ["mimeType"] = child.Value ? DirectoryMimeType : "text/markdown",
            })]),
            ["ttlMs"] = 300000,
            ["cacheScope"] = "public",
        };
    }

    private static JsonObject Entry(Skill skill) => new()
    {
        ["uri"] = skill.Uri,
        ["frontmatter"] = skill.Frontmatter.DeepClone(),
        ["resources"] = skill.Dynamic
            ? "dynamic"
            : new JsonArray([.. skill.Files.Select(file => (JsonNode)new JsonObject
            {
                ["uri"] = skill.Root + "/" + file.Path,
                ["digest"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file.Text))),
                ["size"] = Encoding.UTF8.GetByteCount(file.Text),
            })]),
    };
}
