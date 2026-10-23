using System.Globalization;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Plugin.Mcp;
using XenoAtom.CommandLine;

namespace CodeAlta.Tests;

// `alta mcp skill list|show|activate|read`: the approval of the user, the entry held, the checks of every file read.
public sealed partial class McpRuntimeServiceTests
{
    [TestMethod]
    public async Task SkillCommands_AnActivationAsksTheUserOnce_AndTheApprovalIsBoundToTheFilesOfTheSkill()
    {
        using var project = TempDirectory.Create();
        var log = Path.Combine(project.Path, "server.log");
        WriteTinyServerConfig(project.Path, "tiny", log, extraEnv: Skills());
        await using var plugin = new McpPlugin(createPresentation: null, _home.Path, new McpPluginOptions { LoadsSkills = true });
        var asked = new List<AgentSkillPermissionRequest>();
        var answer = AgentPermissionDecisionKind.AllowAlways;
        AgentPermissionDecision Ask(AgentPermissionRequest request) { asked.Add((AgentSkillPermissionRequest)request); return new(answer); }

        var list = await RunSkillAsync(plugin, project.Path, Ask, "skill", "list", "--refresh");
        Assert.AreEqual(0, list.Exit);
        CollectionAssert.Contains(list.Records.Where(static record => record.GetProperty("type").GetString() == "alta.mcp.skill").Select(static record => record.GetProperty("name").GetString()).ToArray(), "pdf-processing");
        Assert.IsFalse(File.ReadAllLines(log).Any(static line => line.StartsWith("READ", StringComparison.Ordinal)), "Listing reads no file.");

        var activated = await RunSkillAsync(plugin, project.Path, Ask, "skill", "activate", "--server", "tiny", "pdf-processing");

        Assert.AreEqual(0, activated.Exit, activated.Output);
        var record = activated.Records.Single();
        Assert.AreEqual("alta.skill.activated", record.GetProperty("type").GetString());
        Assert.AreEqual("always", record.GetProperty("approval").GetString());
        var payload = record.GetProperty("payload").GetString()!;
        StringAssert.StartsWith(payload, "<skill_content name=\"pdf-processing\" source_kind=\"mcp\" server=\"tiny\" uri=\"skill://pdf-processing/SKILL.md\" base_uri=\"skill://pdf-processing/\">");
        StringAssert.Contains(payload, "Files: SKILL.md (151 B), templates/invoice.md (29 B), templates/purchase-order.md (35 B)");
        StringAssert.Contains(payload, "# PDF processing");
        Assert.IsFalse(payload.Contains("description: Extract", StringComparison.Ordinal), "The frontmatter is not part of the payload.");
        Assert.AreEqual(new AgentSkillPermissionRequest(asked[0].ProviderId, "one", asked[0].Timestamp, null, asked[0].InteractionId, "tiny", "pdf-processing",
            "skill://pdf-processing/SKILL.md", 3, 215, false), asked.Single());

        // Allowed from now on: a new activation of the unchanged skill does not ask again.
        var again = await RunSkillAsync(plugin, project.Path, Ask, "skill", "activate", "--server", "tiny", "skill://pdf-processing/SKILL.md");
        Assert.AreEqual("kept", again.Records.Single().GetProperty("approval").GetString());
        Assert.AreEqual(1, asked.Count);

        // An approval of other files of the skill is no approval of these: the user is asked again.
        var approvals = McpSkillApprovalStore.PathOf(_home.Path);
        using (var stored = JsonDocument.Parse(File.ReadAllText(approvals)))
        {
            File.WriteAllText(approvals, File.ReadAllText(approvals).Replace(stored.RootElement.GetProperty("approvals")[0].GetProperty("manifestHash").GetString()!, new string('0', 64), StringComparison.Ordinal));
        }

        answer = AgentPermissionDecisionKind.AllowOnce;
        Assert.AreEqual("once", (await RunSkillAsync(plugin, project.Path, Ask, "skill", "activate", "--server", "tiny", "pdf-processing")).Records.Single().GetProperty("approval").GetString());
        Assert.AreEqual(2, asked.Count);

        // A refusal loads nothing.
        answer = AgentPermissionDecisionKind.Deny;
        var denied = await RunSkillAsync(plugin, project.Path, Ask, "skill", "activate", "--server", "tiny", "skill://acme/support/refunds/SKILL.md");
        Assert.AreEqual(1, denied.Exit);
        Assert.AreEqual("skill_not_approved", denied.Records.Single().GetProperty("code").GetString());
        Assert.IsFalse(File.ReadAllLines(log).Contains("READ skill://acme/support/refunds/SKILL.md"));

        // A caller that cannot ask the user loads no skill that was not allowed from now on.
        var unasked = await RunSkillAsync(plugin, project.Path, ask: null, "skill", "activate", "--server", "tiny", "skill://acme/support/refunds/SKILL.md");
        Assert.AreEqual("approval_unavailable", unasked.Records.Single().GetProperty("code").GetString());

        // What `show` says, without reading a file.
        var reads = File.ReadAllLines(log).Count(static line => line.StartsWith("READ", StringComparison.Ordinal));
        var shown = (await RunSkillAsync(plugin, project.Path, Ask, "skill", "show", "--server", "tiny", "skill://acme/billing/refunds/SKILL.md")).Records.Single();
        Assert.AreEqual("not_approved", shown.GetProperty("approval").GetString());
        Assert.IsTrue(shown.GetProperty("allowedToolsIgnored").GetBoolean());
        Assert.AreEqual(3, shown.GetProperty("files").GetArrayLength());
        Assert.AreEqual(reads, File.ReadAllLines(log).Count(static line => line.StartsWith("READ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SkillCommands_FilesAreReadAgainstTheEntryHeld()
    {
        using var project = TempDirectory.Create();
        var log = Path.Combine(project.Path, "server.log");
        WriteTinyServerConfig(project.Path, "tiny", log, extraEnv: Skills());
        await using var plugin = new McpPlugin(createPresentation: null, _home.Path, new McpPluginOptions { LoadsSkills = true });
        static AgentPermissionDecision Allow(AgentPermissionRequest request) => new(AgentPermissionDecisionKind.AllowOnce);

        var notLoaded = await RunSkillAsync(plugin, project.Path, Allow, "skill", "read", "--server", "tiny", "templates/invoice.md");
        Assert.AreEqual("skill_not_loaded", notLoaded.Records.Single().GetProperty("code").GetString());
        Assert.AreEqual(0, (await RunSkillAsync(plugin, project.Path, Allow, "skill", "activate", "--server", "tiny", "skill://pdf-processing/SKILL.md")).Exit);

        var invoice = (await RunSkillAsync(plugin, project.Path, Allow, "skill", "read", "--server", "tiny", "templates/invoice.md")).Records.Single();
        var byUri = (await RunSkillAsync(plugin, project.Path, Allow, "skill", "read", "--server", "tiny", "skill://pdf-processing/templates/purchase-order.md")).Records.Single();
        var folder = (await RunSkillAsync(plugin, project.Path, Allow, "skill", "read", "--server", "tiny", "templates/")).Records.Single();
        var root = (await RunSkillAsync(plugin, project.Path, Allow, "skill", "read", "--server", "tiny", ".")).Records.Single();
        var unlisted = (await RunSkillAsync(plugin, project.Path, Allow, "skill", "read", "--server", "tiny", "templates/credit-note.md")).Records.Single();
        var outside = (await RunSkillAsync(plugin, project.Path, Allow, "skill", "read", "--server", "tiny", "../acme/billing/refunds/SKILL.md")).Records.Single();
        var again = (await RunSkillAsync(plugin, project.Path, Allow, "skill", "read", "--server", "tiny", "templates/invoice.md")).Records.Single();

        Assert.AreEqual("# Invoice\n\nCustomer:\nAmount:\n", invoice.GetProperty("content").GetString());
        Assert.IsTrue(invoice.GetProperty("verified").GetBoolean());
        Assert.AreEqual("templates/purchase-order.md", byUri.GetProperty("path").GetString());
        Assert.AreEqual("alta.mcp.skill.folder", folder.GetProperty("type").GetString());
        CollectionAssert.AreEqual(new[] { "invoice.md", "purchase-order.md" }, folder.GetProperty("entries").EnumerateArray().Select(static item => item.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual("manifest", folder.GetProperty("source").GetString());
        CollectionAssert.AreEqual(new[] { "SKILL.md", "templates/" }, root.GetProperty("entries").EnumerateArray().Select(static item => item.GetProperty("name").GetString()).ToArray());
        // A file the entry does not list is refused, and the server is not asked for it.
        Assert.AreEqual("file_not_in_skill", unlisted.GetProperty("code").GetString());
        Assert.AreEqual("file_not_in_skill", outside.GetProperty("code").GetString());
        Assert.AreEqual("# Invoice\n\nCustomer:\nAmount:\n", again.GetProperty("content").GetString());
        var lines = File.ReadAllLines(log);
        Assert.IsFalse(lines.Contains("READ skill://pdf-processing/templates/credit-note.md"));
        // A file checked once is not read again from its server.
        Assert.AreEqual(1, lines.Count(static line => line == "READ skill://pdf-processing/templates/invoice.md"));
        Assert.IsFalse(lines.Any(static line => line.StartsWith("DIR", StringComparison.Ordinal)), "A folder of a listed skill is answered from its entry.");
    }

    [TestMethod]
    public async Task SkillCommands_NestedSharedNamedUnlistedAndDynamicSkills()
    {
        using var project = TempDirectory.Create();
        var log = Path.Combine(project.Path, "server.log");
        WriteTinyServerConfig(project.Path, "tiny", log, extraEnv: Skills());
        await using var plugin = new McpPlugin(createPresentation: null, _home.Path, new McpPluginOptions { LoadsSkills = true });
        var asked = new List<string>();
        AgentPermissionDecision Ask(AgentPermissionRequest request) { asked.Add(((AgentSkillPermissionRequest)request).Uri); return new(AgentPermissionDecisionKind.AllowAlways); }
        await RunSkillAsync(plugin, project.Path, Ask, "skill", "list", "--refresh");

        // Two skills of the server are named `refunds`: a name says neither.
        var shared = await RunSkillAsync(plugin, project.Path, Ask, "skill", "activate", "--server", "tiny", "refunds");
        Assert.AreEqual("skill_ambiguous", shared.Records.Single().GetProperty("code").GetString());
        StringAssert.Contains(shared.Records.Single().GetProperty("message").GetString(), "skill://acme/billing/refunds/SKILL.md, skill://acme/support/refunds/SKILL.md");

        // The approval of a skill is not one of a skill nested in it, whose `SKILL.md` it reads as one of its files.
        Assert.AreEqual(0, (await RunSkillAsync(plugin, project.Path, Ask, "skill", "activate", "--server", "tiny", "skill://acme/billing/refunds/SKILL.md")).Exit);
        var nestedFile = (await RunSkillAsync(plugin, project.Path, Ask, "skill", "read", "--server", "tiny", "escalation/SKILL.md")).Records.Single();
        StringAssert.Contains(nestedFile.GetProperty("content").GetString(), "# Escalation");
        Assert.AreEqual(0, (await RunSkillAsync(plugin, project.Path, Ask, "skill", "activate", "--server", "tiny", "skill://acme/billing/refunds/escalation/SKILL.md")).Exit);
        CollectionAssert.AreEqual(new[] { "skill://acme/billing/refunds/SKILL.md", "skill://acme/billing/refunds/escalation/SKILL.md" }, asked);
        // Two skills loaded: a relative path names its skill.
        Assert.AreEqual("skill_ambiguous", (await RunSkillAsync(plugin, project.Path, Ask, "skill", "read", "--server", "tiny", "SKILL.md")).Records.Single().GetProperty("code").GetString());
        StringAssert.Contains((await RunSkillAsync(plugin, project.Path, Ask, "skill", "read", "--server", "tiny", "--skill", "escalation", "SKILL.md")).Records.Single().GetProperty("content").GetString(), "billing lead");

        // A skill the listing leaves out is confirmed with `skills/get`.
        Assert.AreEqual(0, (await RunSkillAsync(plugin, project.Path, Ask, "skill", "activate", "--server", "tiny", "skill://hidden/unlisted/SKILL.md")).Exit);
        CollectionAssert.Contains(File.ReadAllLines(log), "SKILLS_GET skill://hidden/unlisted/SKILL.md");
        var unknown = await RunSkillAsync(plugin, project.Path, Ask, "skill", "activate", "--server", "tiny", "skill://hidden/none/SKILL.md");
        Assert.AreEqual("skill_not_found", unknown.Records.Last().GetProperty("code").GetString());

        // A skill its server generates is allowed once only, and its folders are listed by its server.
        var dynamic = (await RunSkillAsync(plugin, project.Path, Ask, "skill", "activate", "--server", "tiny", "skill://reports/daily/SKILL.md")).Records.Single();
        Assert.AreEqual("once", dynamic.GetProperty("approval").GetString());
        StringAssert.Contains(dynamic.GetProperty("payload").GetString(), "Files: generated by the server, not checked; list a folder with a path ending in `/`.");
        Assert.AreEqual(0, (await RunSkillAsync(plugin, project.Path, Ask, "skill", "activate", "--server", "tiny", "skill://reports/daily/SKILL.md")).Exit);
        Assert.AreEqual(2, asked.Count(static uri => uri == "skill://reports/daily/SKILL.md"));
        var data = (await RunSkillAsync(plugin, project.Path, Ask, "skill", "read", "--server", "tiny", "--skill", "daily", "data/")).Records.Single();
        CollectionAssert.AreEqual(new[] { "today.md" }, data.GetProperty("entries").EnumerateArray().Select(static item => item.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual("server", data.GetProperty("source").GetString());
        var today = (await RunSkillAsync(plugin, project.Path, Ask, "skill", "read", "--server", "tiny", "--skill", "daily", "data/today.md")).Records.Single();
        Assert.IsFalse(today.GetProperty("verified").GetBoolean());
        CollectionAssert.Contains(File.ReadAllLines(log), "DIR skill://reports/daily/data");

        // A skill the user turned off is neither listed nor loaded.
        WriteProjectPolicy(project.Path, "[plugins.mcp.servers.tiny]\ndisabled_skills = [\"pdf-processing\"]\n");
        Assert.AreEqual("skill_disabled", (await RunSkillAsync(plugin, project.Path, Ask, "skill", "activate", "--server", "tiny", "skill://pdf-processing/SKILL.md")).Records.Single().GetProperty("code").GetString());
        Assert.IsFalse((await RunSkillAsync(plugin, project.Path, Ask, "skill", "list")).Records.Any(static record => record.TryGetProperty("name", out var name) && name.GetString() == "pdf-processing"));
    }

    [TestMethod]
    public async Task SkillCommands_ATamperedFileIsRefusedAndTheSkillChanged_AndAServerNeverReadsTheSkillOfAnother()
    {
        using var project = TempDirectory.Create();
        var log = Path.Combine(project.Path, "server.log");
        var otherLog = Path.Combine(project.Path, "other.log");
        var path = McpConfigDiscovery.GetProjectConfigPath(project.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["tiny"] = CreateTinyServerDefinition(new Dictionary<string, string> { ["MCP_TEST_LOG"] = log, ["MCP_TEST_SKILLS"] = "1", ["MCP_TEST_SKILLS_TAMPER"] = "1" }),
                ["other"] = CreateTinyServerDefinition(new Dictionary<string, string> { ["MCP_TEST_LOG"] = otherLog, ["MCP_TEST_SKILLS"] = "1" }),
            },
        }));
        await using var plugin = new McpPlugin(createPresentation: null, _home.Path, new McpPluginOptions { LoadsSkills = true });
        static AgentPermissionDecision Allow(AgentPermissionRequest request) => new(AgentPermissionDecisionKind.AllowOnce);
        Assert.AreEqual(0, (await RunSkillAsync(plugin, project.Path, Allow, "skill", "activate", "--server", "tiny", "skill://pdf-processing/SKILL.md")).Exit);

        // The other server has the same skill at the same URI: it is another skill, which this session did not load.
        var foreign = (await RunSkillAsync(plugin, project.Path, Allow, "skill", "read", "--server", "other", "skill://pdf-processing/templates/invoice.md")).Records.Single();
        Assert.AreEqual("skill_not_loaded", foreign.GetProperty("code").GetString());
        Assert.IsFalse(File.Exists(otherLog) && File.ReadAllLines(otherLog).Any(static line => line.StartsWith("READ", StringComparison.Ordinal)));

        var tampered = (await RunSkillAsync(plugin, project.Path, Allow, "skill", "read", "--server", "tiny", "templates/invoice.md")).Records.Single();
        Assert.AreEqual("skill_changed", tampered.GetProperty("code").GetString());
        StringAssert.Contains(tampered.GetProperty("message").GetString(), "alta mcp skill activate --server tiny skill://pdf-processing/SKILL.md");
        Assert.IsFalse(tampered.GetProperty("message").GetString()!.Contains("Total", StringComparison.Ordinal), "The content that failed is not given.");
        // The skill is let go: it is to be loaded again.
        Assert.AreEqual("skill_not_loaded", (await RunSkillAsync(plugin, project.Path, Allow, "skill", "read", "--server", "tiny", "templates/invoice.md")).Records.Single().GetProperty("code").GetString());
    }

    private async Task<(int Exit, IReadOnlyList<JsonElement> Records, string Output)> RunSkillAsync(McpPlugin plugin, string projectPath, Func<AgentPermissionRequest, AgentPermissionDecision>? ask, params string[] args)
    {
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var context = CreateAltaContext(stdout, stderr, projectPath, "one") with
        {
            RequestPermissionAsync = ask is null ? null : (request, _) => ValueTask.FromResult(ask(request)),
        };
        var app = new CommandApp("alta", "test") { plugin.GetAltaCommands().Single().CreateCommandNode(context) };
        var exit = await app.RunAsync(["mcp", .. args], new CommandRunConfig { Out = TextWriter.Null, Error = stderr });
        var output = stdout.ToString();
        var records = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        return (exit, records, output + stderr);
    }
}
