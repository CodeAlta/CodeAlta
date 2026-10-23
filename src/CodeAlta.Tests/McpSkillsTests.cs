using System.Text;
using System.Text.Json.Nodes;
using CodeAlta.Catalog.Skills;
using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Tests;

// The MCP Skills extension (io.modelcontextprotocol/skills): the checks of an entry and of a file, the listing at a
// connection and its cache.
public sealed partial class McpRuntimeServiceTests
{
    // The variables of the tiny server that give it the skills of the extension.
    private static Dictionary<string, string> Skills() => new() { ["MCP_TEST_SKILLS"] = "1" };

    // The example of the spec (`skills/get` of `pdf-processing`), with its exact digests.
    private const string PdfProcessingEntry = """
        {
          "uri": "skill://pdf-processing/SKILL.md",
          "frontmatter": { "name": "pdf-processing", "description": "Extract, fill, and assemble PDF documents" },
          "resources": [
            { "uri": "skill://pdf-processing/SKILL.md", "digest": "sha256:99b737495721155ece826d57521e2d66141ebdc1344a400487481ea2642ab19e", "size": 151 },
            { "uri": "skill://pdf-processing/templates/invoice.md", "digest": "sha256:61f4ea6d2c75fde1b4977219e7e3107d491c3c26aefb6686e84d6281c088d9ee", "size": 29 },
            { "uri": "skill://pdf-processing/templates/purchase-order.md", "digest": "sha256:f2ff774b1737ff3dec81c47946f9976f18a1a9f69dda0a81f22eabd95173c158", "size": 35 }
          ]
        }
        """;

    private const string PdfProcessingSkill = "---\nname: pdf-processing\ndescription: Extract, fill, and assemble PDF documents\n---\n\n# PDF processing\n\nChoose the matching template from `templates/`.\n";

    [TestMethod]
    public void SkillEntries_TheExamplesOfTheSpecAreValid()
    {
        var listing = JsonNode.Parse("""
            [
              { "uri": "skill://git-workflow/SKILL.md", "frontmatter": { "name": "git-workflow", "description": "Follow this team's Git conventions for branching and commits" },
                "resources": [ { "uri": "skill://git-workflow/SKILL.md", "digest": "sha256:b95a384300adeea2d902f7d19cd7c04b378ef58e09759107b9c7db4dcacbaa25", "size": 190 } ] },
              { "uri": "skill://acme/billing/refunds/SKILL.md", "frontmatter": { "name": "refunds", "description": "Process customer refund requests per company policy", "license": "Apache-2.0" },
                "resources": [
                  { "uri": "skill://acme/billing/refunds/SKILL.md", "digest": "sha256:ae7bc7e45f44a6381977ef1f772cbad6e38d8ec78903ea7b87abb373815a8f89", "size": 175 },
                  { "uri": "skill://acme/billing/refunds/examples/email.md", "digest": "sha256:a724fa5ed3b9e39c0e731fe1f0e866eb46d23969af25ecf0d5f41982e3e36bea", "size": 58 } ] },
              { "uri": "skill://reports/daily/SKILL.md", "frontmatter": { "name": "daily", "description": "Assemble today's operational report from live data" }, "resources": "dynamic" }
            ]
            """)!.AsArray().Append(JsonNode.Parse(PdfProcessingEntry)).ToArray();

        var entries = listing.Select(static node => McpSkillValidator.TryParse(node, out var entry, out var error) ? entry! : throw new AssertFailedException(error ?? "invalid")).ToArray();

        CollectionAssert.AreEqual(new[] { "git-workflow", "refunds", "daily", "pdf-processing" }, entries.Select(static entry => entry.Name).ToArray());
        Assert.AreEqual("skill://acme/billing/refunds", entries[1].RootUri);
        Assert.AreEqual(233, entries[1].TotalSize);
        Assert.IsTrue(entries[2].IsDynamic);
        Assert.IsNull(entries[2].ManifestHash);
        Assert.AreEqual("templates/invoice.md", entries[3].RelativePathOf("skill://pdf-processing/templates/invoice.md"));
    }

    [TestMethod]
    [DataRow("""{ "uri": "skill://a/refunds/SKILL.md", "frontmatter": { "name": "other", "description": "d" }, "resources": "dynamic" }""", "is not the last segment")]
    [DataRow("""{ "uri": "skill://a/Refunds/SKILL.md", "frontmatter": { "name": "Refunds", "description": "d" }, "resources": "dynamic" }""", "naming rules")]
    [DataRow("""{ "uri": "skill://a/two--dashes/SKILL.md", "frontmatter": { "name": "two--dashes", "description": "d" }, "resources": "dynamic" }""", "naming rules")]
    [DataRow("""{ "uri": "skill://a/x/SKILL.md", "frontmatter": { "name": "x" }, "resources": "dynamic" }""", "no `description`")]
    [DataRow("""{ "uri": "skill://a/x/SKILL.md", "frontmatter": { "name": "x", "description": "d" } }""", "neither a list")]
    [DataRow("""{ "uri": "skill://a/x/SKILL.md", "frontmatter": { "name": "x", "description": "d" }, "resources": "static" }""", "neither a list")]
    [DataRow("""{ "uri": "skill://a/x/README.md", "frontmatter": { "name": "x", "description": "d" }, "resources": "dynamic" }""", "not the URI of a `SKILL.md`")]
    [DataRow("""{ "uri": "skill://a/../x/SKILL.md", "frontmatter": { "name": "x", "description": "d" }, "resources": "dynamic" }""", "not the URI of a `SKILL.md`")]
    [DataRow("""{ "uri": "skill://x/SKILL.md", "frontmatter": { "name": "x", "description": "d" }, "resources": [] }""", "do not list its `SKILL.md`")]
    [DataRow("""{ "uri": "skill://x/SKILL.md", "frontmatter": { "name": "x", "description": "d" }, "resources": [ { "uri": "skill://x/SKILL.md", "digest": "sha256:99B737495721155ECE826D57521E2D66141EBDC1344A400487481EA2642AB19E", "size": 1 } ] }""", "lowercase hexadecimal")]
    [DataRow("""{ "uri": "skill://x/SKILL.md", "frontmatter": { "name": "x", "description": "d" }, "resources": [ { "uri": "skill://x/SKILL.md", "digest": "sha256:99b737495721155ece826d57521e2d66141ebdc1344a400487481ea2642ab19e", "size": -1 } ] }""", "not a number of bytes")]
    [DataRow("""{ "uri": "skill://x/SKILL.md", "frontmatter": { "name": "x", "description": "d" }, "resources": [ { "uri": "skill://x/SKILL.md", "digest": "sha256:99b737495721155ece826d57521e2d66141ebdc1344a400487481ea2642ab19e", "size": 1 }, { "uri": "skill://y/a.md", "digest": "sha256:99b737495721155ece826d57521e2d66141ebdc1344a400487481ea2642ab19e", "size": 1 } ] }""", "not in the directory")]
    [DataRow("""{ "uri": "skill://x/SKILL.md", "frontmatter": { "name": "x", "description": "d" }, "resources": [ { "uri": "skill://x/SKILL.md", "digest": "sha256:99b737495721155ece826d57521e2d66141ebdc1344a400487481ea2642ab19e", "size": 1 }, { "uri": "skill://x/../y.md", "digest": "sha256:99b737495721155ece826d57521e2d66141ebdc1344a400487481ea2642ab19e", "size": 1 } ] }""", "not in the directory")]
    [DataRow("""{ "uri": "skill://x/SKILL.md", "frontmatter": { "name": "x", "description": "d" }, "resources": [ { "uri": "skill://x/SKILL.md", "digest": "sha256:99b737495721155ece826d57521e2d66141ebdc1344a400487481ea2642ab19e", "size": 16777217 } ] }""", "larger than 16 MiB")]
    public void SkillEntries_AnInvalidEntryIsRefusedWithItsReason(string json, string reason)
    {
        Assert.IsFalse(McpSkillValidator.TryParse(JsonNode.Parse(json), out var entry, out var error));
        Assert.IsNull(entry);
        StringAssert.Contains(error, reason);
    }

    [TestMethod]
    public void SkillEntries_MoreThan512FilesAreRefused()
    {
        var files = new JsonArray([.. Enumerable.Range(0, 513).Select(index => (JsonNode)new JsonObject
        {
            ["uri"] = index == 0 ? "skill://x/SKILL.md" : $"skill://x/f{index}.md",
            ["digest"] = "sha256:" + new string('0', 64),
            ["size"] = 1,
        })]);
        var entry = new JsonObject { ["uri"] = "skill://x/SKILL.md", ["frontmatter"] = new JsonObject { ["name"] = "x", ["description"] = "d" }, ["resources"] = files };

        Assert.IsFalse(McpSkillValidator.TryParse(entry, out _, out var error));
        StringAssert.Contains(error, "more than 512 files");
    }

    [TestMethod]
    public void SkillFiles_AreCheckedAgainstTheDigestsAndSizesOfTheSpec()
    {
        Assert.IsTrue(McpSkillValidator.TryParse(JsonNode.Parse(PdfProcessingEntry), out var entry, out _));
        var invoice = entry!.FindFile("skill://pdf-processing/templates/invoice.md")!;

        Assert.IsNull(McpSkillVerifier.VerifyFile(entry.FindFile(entry.Uri)!, Encoding.UTF8.GetBytes(PdfProcessingSkill)));
        Assert.IsNull(McpSkillVerifier.VerifyFile(invoice, "# Invoice\n\nCustomer:\nAmount:\n"u8));
        StringAssert.Contains(McpSkillVerifier.VerifyFile(invoice, "# Invoice\n\nCustomer:\nAmount:\n\n"u8), "has 30 bytes, not the 29");
        StringAssert.Contains(McpSkillVerifier.VerifyFile(invoice, "# Invoice\n\nCustomer:\nTotal: \n"u8), "SHA-256");
        // A different file changes the hash an approval is bound to.
        var changed = entry with { Files = [.. entry.Files!.Select(file => file == invoice ? file with { Digest = "sha256:" + new string('1', 64) } : file)] };
        Assert.AreNotEqual(entry.ManifestHash, changed.ManifestHash);
    }

    [TestMethod]
    public void SkillFiles_TheFrontmatterOfASkillMdIsComparedFieldByField()
    {
        Assert.IsTrue(McpSkillValidator.TryParse(JsonNode.Parse(PdfProcessingEntry), out var entry, out _));
        var typed = entry! with
        {
            Frontmatter = JsonNode.Parse("""{ "name": "pdf-processing", "description": "Extract, fill, and assemble PDF documents", "metadata": { "version": 2, "beta": true, "tags": ["a", "b"] } }""")!.AsObject(),
        };
        const string typedSkill = "---\nname: pdf-processing\ndescription: Extract, fill, and assemble PDF documents\nmetadata:\n  version: 2\n  beta: true\n  tags: [a, b]\n---\nBody\n";

        Assert.IsNull(McpSkillVerifier.VerifyFrontmatter(entry!, PdfProcessingSkill));
        Assert.IsNull(McpSkillVerifier.VerifyFrontmatter(typed, typedSkill));
        StringAssert.Contains(McpSkillVerifier.VerifyFrontmatter(entry!, PdfProcessingSkill.Replace("assemble", "merge", StringComparison.Ordinal)), "`description`");
        StringAssert.Contains(McpSkillVerifier.VerifyFrontmatter(entry!, PdfProcessingSkill.Replace("---\n\n", "license: MIT\n---\n\n", StringComparison.Ordinal)), "`license`");
        StringAssert.Contains(McpSkillVerifier.VerifyFrontmatter(typed, typedSkill.Replace("version: 2", "version: \"2\"", StringComparison.Ordinal)), "`metadata`");
        StringAssert.Contains(McpSkillVerifier.VerifyFrontmatter(entry!, "# No frontmatter\n"), "no frontmatter");
    }

    [TestMethod]
    public void SkillFrontmatter_IsReadAsJsonTypedAsYaml()
    {
        Assert.IsTrue(SkillFrontmatterReader.TrySplit("---\nname: x\ncount: 3\nratio: 0.5\nflag: false\nnothing: null\nlist:\n  - one\n---\nbody", out var yaml, out var body));
        var json = SkillFrontmatterReader.ReadAsJson(yaml);

        Assert.AreEqual("body", body);
        Assert.IsTrue(McpSkillVerifier.JsonEquals(JsonNode.Parse("""{ "name": "x", "count": 3, "ratio": 0.5, "flag": false, "nothing": null, "list": ["one"] }"""), json), json.ToJsonString());
        Assert.ThrowsExactly<InvalidDataException>(() => SkillFrontmatterReader.ReadAsJson("- a\n- b"));
    }

    [TestMethod]
    public async Task Skills_AreListedAtTheConnectionWithoutReadingAFile()
    {
        using var project = TempDirectory.Create();
        var log = Path.Combine(project.Path, "server.log");
        WriteTinyServerConfig(project.Path, "tiny", log, extraEnv: Skills());
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };

        await using (var service = new McpRuntimeService(_home.Path, configCache: null, advertiseApps: false, loadsSkills: true))
        {
            await service.ListToolsForServersAsync(request, ["tiny"], CancellationToken.None);
        }

        await using var reader = new McpRuntimeService(_home.Path, configCache: null, advertiseApps: false, loadsSkills: true);
        var list = reader.ListCachedSkills(request).Single();
        var lines = File.ReadAllLines(log);

        CollectionAssert.Contains(lines, "CLIENT_SKILLS yes");
        // Three pages, and not a single file read.
        CollectionAssert.AreEqual(new[] { "SKILLS_LIST -", "SKILLS_LIST 3", "SKILLS_LIST 6" }, lines.Where(static line => line.StartsWith("SKILLS_LIST", StringComparison.Ordinal)).ToArray());
        Assert.IsFalse(lines.Any(static line => line.StartsWith("READ", StringComparison.Ordinal)));
        Assert.AreEqual("tiny", list.Server);
        Assert.IsTrue(list.DirectoryRead);
        CollectionAssert.AreEqual(
            new[] { "skill://pdf-processing/SKILL.md", "skill://acme/billing/refunds/SKILL.md", "skill://acme/billing/refunds/escalation/SKILL.md", "skill://acme/support/refunds/SKILL.md", "skill://reports/daily/SKILL.md", "skill://tools/document-conversion/SKILL.md" },
            list.Skills.Select(static skill => skill.Uri).ToArray());
        // The invalid entry is left out, with its reason; the skill the listing omits is not there.
        StringAssert.Contains(list.Diagnostics.Single(), "skill://broken/mismatch/SKILL.md");
        Assert.AreEqual("sha256:99b737495721155ece826d57521e2d66141ebdc1344a400487481ea2642ab19e", list.Skills[0].Files![0].Digest);
        Assert.IsTrue(list.Skills.Single(static skill => skill.Name == "daily").IsDynamic);
        Assert.AreEqual("Bash(curl:*)", list.Skills[1].Frontmatter["allowed-tools"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Skills_AHostThatDoesNotLoadThemNeitherTellsTheServerNorListsThem()
    {
        using var project = TempDirectory.Create();
        var log = Path.Combine(project.Path, "server.log");
        WriteTinyServerConfig(project.Path, "tiny", log, extraEnv: Skills());
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };

        await using (var service = new McpRuntimeService(_home.Path, configCache: null, advertiseApps: true))
        {
            await service.ListToolsForServersAsync(request, ["tiny"], CancellationToken.None);
        }

        var lines = File.ReadAllLines(log);
        CollectionAssert.Contains(lines, "CLIENT_SKILLS no");
        Assert.IsFalse(lines.Any(static line => line.StartsWith("SKILLS_", StringComparison.Ordinal)));
        Assert.IsFalse(Directory.Exists(McpSkillListCache.FolderOf(_home.Path)));
    }

    [TestMethod]
    public async Task Skills_TheCachedListFollowsThePolicyAndIsForgottenWithItsServer()
    {
        using var project = TempDirectory.Create();
        WriteTinyServerConfig(project.Path, "tiny", logPath: null, extraEnv: Skills());
        var request = new McpRuntimeRequest { ProjectDirectory = project.Path };
        await using (var service = new McpRuntimeService(_home.Path, configCache: null, advertiseApps: false, loadsSkills: true))
        {
            await service.ListToolsForServersAsync(request, ["tiny"], CancellationToken.None);
        }

        await using var reader = new McpRuntimeService(_home.Path, configCache: null, advertiseApps: false, loadsSkills: true);
        Assert.AreEqual(1, reader.ListCachedSkills(request).Count);

        WriteProjectPolicy(project.Path, "[plugins.mcp.servers.tiny]\nenabled = false\n");
        Assert.AreEqual(0, reader.ListCachedSkills(request).Count);

        await new McpManagementService(_home.Path).RemoveServerAsync("tiny", McpManagementScope.Project, new McpManagementRequest { ProjectDirectory = project.Path, UserHomeDirectory = _home.Path });
        Assert.AreEqual(0, Directory.EnumerateFiles(McpSkillListCache.FolderOf(_home.Path)).Count());
    }

    [TestMethod]
    public async Task SkillPolicy_DisabledSkillsAreWrittenReadAndRemoved()
    {
        using var project = TempDirectory.Create();
        var policyPath = McpPolicyWriter.GetProjectPolicyPath(project.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(policyPath)!);
        File.WriteAllText(policyPath, "# kept\n[plugins.mcp.servers.tiny]\nenabled = true\n");
        var service = new McpManagementService(_home.Path);
        var request = new McpManagementRequest { ProjectDirectory = project.Path, UserHomeDirectory = _home.Path };

        var off = await service.SetSkillEnabledAsync("tiny", "skill://pdf-processing/SKILL.md", enabled: false, McpManagementScope.Project, request);
        var again = await service.SetSkillEnabledAsync("tiny", "skill://pdf-processing/SKILL.md", enabled: false, McpManagementScope.Project, request);
        var policy = new McpPolicyLoader().Load(null, policyPath);

        Assert.IsTrue(off.Changed);
        Assert.IsFalse(again.Changed);
        StringAssert.Contains(File.ReadAllText(policyPath), "# kept");
        StringAssert.Contains(File.ReadAllText(policyPath), "disabled_skills = [\"skill://pdf-processing/SKILL.md\"]");
        Assert.IsTrue(policy.IsSkillDisabled("tiny", "skill://pdf-processing/SKILL.md", "pdf-processing"));
        Assert.IsFalse(policy.IsSkillDisabled("tiny", "skill://acme/billing/refunds/SKILL.md", "refunds"));
        Assert.IsFalse(policy.IsSkillDisabled("other", "skill://pdf-processing/SKILL.md", "pdf-processing"));
        // A name a person writes turns off every skill of that name.
        Assert.IsTrue(new McpServerPolicyOptions { DisabledSkills = ["refunds"] }.IsSkillDisabled("skill://acme/support/refunds/SKILL.md", "refunds"));

        var on = await service.SetSkillEnabledAsync("tiny", "skill://pdf-processing/SKILL.md", enabled: true, McpManagementScope.Project, request);
        Assert.IsTrue(on.Changed);
        Assert.IsFalse(File.ReadAllText(policyPath).Contains("disabled_skills =", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task SkillPrompt_ListsTheCachedSkillsInOrderAndTheSameFromOneTurnToTheNext()
    {
        using var project = TempDirectory.Create();
        var log = Path.Combine(project.Path, "server.log");
        WriteTinyServerConfig(project.Path, "tiny", log, extraEnv: Skills());
        await using var plugin = new McpPlugin(createPresentation: null, _home.Path, new McpPluginOptions { LoadsSkills = true });
        var contribution = plugin.GetSystemPromptContributions().Single(static part => part.Title == "MCP skills");
        Assert.IsNull(await contribution.Content(CreateSkillsPromptContext(project.Path), CancellationToken.None));

        await using (var runtime = new McpRuntimeService(_home.Path, configCache: null, advertiseApps: false, loadsSkills: true))
        {
            await runtime.ListToolsForServersAsync(new McpRuntimeRequest { ProjectDirectory = project.Path }, ["tiny"], CancellationToken.None);
        }

        var first = await contribution.Content(CreateSkillsPromptContext(project.Path), CancellationToken.None);
        var second = await contribution.Content(CreateSkillsPromptContext(project.Path), CancellationToken.None);

        Assert.AreEqual(first, second);
        Assert.AreEqual(PluginPromptPartKind.SkillAdvertisement, contribution.Kind);
        var lines = first!.Split('\n');
        StringAssert.StartsWith(lines[0], "MCP skills: published by MCP servers, not by this computer.");
        CollectionAssert.AreEqual(
            new[]
            {
                "- `tiny`",
                "  - refunds: Process customer refund requests per company policy (`skill://acme/billing/refunds/SKILL.md`)",
                "  - escalation: Escalate a refund the policy does not cover (`skill://acme/billing/refunds/escalation/SKILL.md`)",
                "  - refunds: Answer questions about the refund policy (`skill://acme/support/refunds/SKILL.md`)",
                "  - pdf-processing: Extract, fill, and assemble PDF documents (`skill://pdf-processing/SKILL.md`)",
                "  - daily: Assemble today's operational report from live data (`skill://reports/daily/SKILL.md`, dynamic)",
                "  - document-conversion: Convert documents with the server's own converter (`skill://tools/document-conversion/SKILL.md`)",
            },
            lines.Skip(2).ToArray());
        // The prompt reads the cache: nothing is started or read for it.
        Assert.AreEqual(1, File.ReadAllLines(log).Count(static line => line == "LIST"));

        // A skill the user turned off is not offered, nor a server turned off, nor a server of another space.
        WriteProjectPolicy(project.Path, "[plugins.mcp.servers.tiny]\ndisabled_skills = [\"skill://pdf-processing/SKILL.md\", \"refunds\"]\n");
        var filtered = await contribution.Content(CreateSkillsPromptContext(project.Path), CancellationToken.None);
        Assert.IsFalse(filtered!.Contains("pdf-processing", StringComparison.Ordinal));
        Assert.IsFalse(filtered.Contains("refunds:", StringComparison.Ordinal));
        StringAssert.Contains(filtered, "escalation:");
        WriteProjectPolicy(project.Path, "[plugins.mcp.servers.tiny]\nenabled = false\n");
        Assert.IsNull(await contribution.Content(CreateSkillsPromptContext(project.Path), CancellationToken.None));
        WriteProjectPolicy(project.Path, string.Empty);
        File.WriteAllText(McpPolicyWriter.GetGlobalPolicyPath(_home.Path), "[plugins.mcp.servers.tiny]\nspaces = [\"business\"]\n");
        Assert.IsNull(await contribution.Content(CreateSkillsPromptContext(project.Path, "personal"), CancellationToken.None));
        StringAssert.Contains(await contribution.Content(CreateSkillsPromptContext(project.Path, "business"), CancellationToken.None), "- `tiny`");
        // A host that does not load skills has no such part.
        Assert.IsFalse(new McpPlugin(createPresentation: null, _home.Path).GetSystemPromptContributions().Any(static part => part.Title == "MCP skills"));
    }

    [TestMethod]
    public void SkillPrompt_AServerWithManySkillsIsGivenByItsCountAndAnUntrustedDescriptionStaysOnOneLine()
    {
        McpSkillEntry Skill(string name, string description = "Does things")
            => new($"skill://many/{name}/SKILL.md", new JsonObject { ["name"] = name, ["description"] = description }, null);
        var many = new McpCachedSkillList("many", "f", DateTimeOffset.UnixEpoch, false, [.. Enumerable.Range(0, 21).Select(index => Skill($"s{index}"))], []);
        var few = new McpCachedSkillList("alpha", "f", DateTimeOffset.UnixEpoch, false, [Skill("tricky", "Line one\n\n- `alta` injected\tline " + new string('x', 300))], []);

        var prompt = McpPlugin.FormatSkillsPrompt([many, few], new McpPolicyOptions())!;
        var lines = prompt.Split('\n');

        // The servers in the order of their keys.
        StringAssert.StartsWith(lines[2], "- `alpha`");
        StringAssert.StartsWith(lines[3], "  - tricky: Line one - 'alta' injected line xxx");
        Assert.IsTrue(lines[3].Length < 260, lines[3]);
        Assert.AreEqual("- `many`: 21 skills; find them with `alta mcp skill list --server many --query <words>`", lines[4]);
        Assert.AreEqual(5, lines.Length);
    }

    private static PluginSystemPromptContext CreateSkillsPromptContext(string projectPath, string? spaceId = null)
        => new()
        {
            Plugin = CreatePluginDescriptor(),
            Services = NoopPluginServices.Create(),
            ProjectPath = projectPath,
            SessionId = "session-a",
            SpaceId = spaceId,
        };
}
