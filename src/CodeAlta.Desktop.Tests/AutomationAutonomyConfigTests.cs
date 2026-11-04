using CodeAlta.Automations;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class AutomationAutonomyConfigTests
{
    private const string First = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77";
    private static readonly AutomationSource Global = new("config.toml", null, null);

    private static (IReadOnlyList<AutomationDefinition> Definitions, IReadOnlyList<AutomationFault> Faults) Read(string autonomy) => AutomationConfig.Read($$"""
        [automations.{{First}}]
        name = "Incident diagnosis"
        prompt = "Diagnose it."
        autonomy = {{autonomy}}
        """, Global);

    [TestMethod]
    public void Autonomy_IsReadWrittenAndPartOfTheFingerprint()
    {
        var (definitions, faults) = Read("""{ allowed_tools = ["k8s_restart_pod", "k8s_rollback"], max_calls_per_hour = 3, approval_timeout = 30, on_timeout = "wait" }""");

        Assert.AreEqual(0, faults.Count, string.Join("; ", faults.Select(static fault => fault.Message)));
        var autonomy = definitions.Single().Autonomy!;
        CollectionAssert.AreEqual(new[] { "k8s_restart_pod", "k8s_rollback" }, autonomy.AllowedTools.ToArray());
        Assert.AreEqual((3, 30, "wait", "owner"), (autonomy.MaxCallsPerHour, autonomy.ApprovalTimeout, autonomy.OnTimeout, autonomy.Approvers));
        Assert.IsTrue(autonomy.Allows("k8s_rollback") && !autonomy.Allows("k8s_delete"));

        var written = AutomationConfig.Write(string.Empty, definitions.Single());
        StringAssert.Contains(written, """autonomy = { allowed_tools = ["k8s_restart_pod", "k8s_rollback"], max_calls_per_hour = 3, approval_timeout = 30, on_timeout = "wait" }""");
        Assert.AreEqual(definitions.Single().Fingerprint(), AutomationConfig.Read(written, Global).Definitions.Single().Fingerprint());

        // Allowing one more tool, or none, is another automation to allow.
        var plain = definitions.Single() with { Autonomy = null };
        var more = definitions.Single() with { Autonomy = autonomy with { AllowedTools = [.. autonomy.AllowedTools, "k8s_scale"] } };
        Assert.AreEqual(3, new[] { definitions.Single().Fingerprint(), plain.Fingerprint(), more.Fingerprint() }.Distinct().Count());
        // An automation without it keeps the fingerprint it was allowed with before autonomy existed.
        Assert.IsFalse(AutomationConfig.Write(string.Empty, plain).Contains("autonomy", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("""{ allowed_tools = ["shell_command"] }""", "tool of CodeAlta")]
    [DataRow("""{ allowed_tools = ["k8s_*"] }""", "without wildcards")]
    [DataRow("""{ allowed_tools = ["a", "a"] }""", "allowed twice")]
    [DataRow("""{ allowed_tools = ["a"], approval_timeout = 0 }""", "approval_timeout is 1")]
    [DataRow("""{ allowed_tools = ["a"], on_timeout = "retry" }""", "stop or wait")]
    [DataRow("""{ allowed_tools = ["a"], approvers = "team:sre" }""", "approvers is owner (its person, or the team that owns it) or oncall")]
    [DataRow("""{ allowed_tools = ["a"], everything = true }""", "autonomy has no everything")]
    [DataRow("\"yes\"", "autonomy is a table")]
    public void Autonomy_SaysWhatIsWrong(string autonomy, string expected)
    {
        var (definitions, faults) = Read(autonomy);

        Assert.AreEqual(0, definitions.Count);
        StringAssert.Contains(faults.Single().Message, expected);
    }

    [TestMethod]
    public void AnAllowedTool_IsPinnedBoundedAndChecked_ReadAndWrittenAsATable()
    {
        const string Hash = "sha256:00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
        var (definitions, faults) = Read($$"""{ allowed_tools = ["k8s_get_deployment", { name = "k8s_restart_pod", hash = "{{Hash}}", version = "1.4.2", bounds = { namespace = ["shop"], replicas = { min = 1, max = 5 } }, preconditions = [{ tool = "k8s_get_deployment", arguments = ["namespace", "name"], expect = "available" }] }] }""");

        Assert.AreEqual(0, faults.Count, string.Join("; ", faults.Select(static fault => fault.Message)));
        var autonomy = definitions.Single().Autonomy!;
        CollectionAssert.AreEqual(new[] { "k8s_get_deployment", "k8s_restart_pod" }, autonomy.AllowedTools.ToArray(), "a table allows its tool too");
        var tool = autonomy.Tool("k8s_restart_pod")!;
        Assert.AreEqual((Hash, "1.4.2"), (tool.Hash, tool.Version));
        Assert.AreEqual(("namespace", "shop"), (tool.Bounds[0].Argument, tool.Bounds[0].Values!.Single()));
        Assert.AreEqual(("replicas", 1d, 5d), (tool.Bounds[1].Argument, tool.Bounds[1].Min, tool.Bounds[1].Max));
        Assert.AreEqual(("k8s_get_deployment", "available"), (tool.Preconditions.Single().Tool, tool.Preconditions.Single().Expect));
        Assert.IsNull(autonomy.Tool("k8s_get_deployment"), "allowed by its name alone");

        var written = AutomationConfig.Write(string.Empty, definitions.Single());
        StringAssert.Contains(written, $$"""{ name = "k8s_restart_pod", hash = "{{Hash}}", version = "1.4.2", bounds = { namespace = ["shop"], replicas = { min = 1, max = 5 } }""");
        Assert.AreEqual(definitions.Single().Fingerprint(), AutomationConfig.Read(written, Global).Definitions.Single().Fingerprint());
        var looser = definitions.Single() with { Autonomy = autonomy with { Tools = [tool with { Bounds = [] }] } };
        Assert.AreNotEqual(definitions.Single().Fingerprint(), looser.Fingerprint(), "loosening a bound is another automation to allow");

        foreach (var (text, problem) in new[]
        {
            ("""{ allowed_tools = [{ name = "k8s_restart_pod", hash = "md5:abc" }] }""", "sha256: and 64 hexadecimal digits"),
            ("""{ allowed_tools = [{ name = "k8s_restart_pod", preconditions = [{ tool = "k8s_get", expect = "ok" }] }] }""", "a tool the automation also allows"),
            ("""{ allowed_tools = [{ name = "k8s_restart_pod", bounds = { replicas = { min = 5, max = 1 } } }] }""", "a min above its max"),
            ("""{ allowed_tools = [{ name = "k8s_restart_pod", bounds = { replicas = { least = 1 } } }] }""", "a list of values, or { min"),
            ("""{ allowed_tools = [{ name = "k8s_restart_pod", colour = "red" }] }""", "has no colour"),
            ("""{ allowed_tools = [{ hash = "x" }] }""", "has a name"),
            ("""{ allowed_tools = [{ name = "a", preconditions = [{ tool = "b", expect = "ok" }] }, { name = "b", preconditions = [{ tool = "a", expect = "ok" }] }] }""", "preconditions of its own"),
        })
        {
            var refused = Read(text).Faults.Single().Message;
            StringAssert.Contains(refused, problem, text);
        }
    }

    [TestMethod]
    public void Autonomy_WithoutAllowedTools_IsReadOnly()
    {
        var autonomy = Read("{ }").Definitions.Single().Autonomy!;

        Assert.AreEqual(0, autonomy.AllowedTools.Count);
        Assert.AreEqual((null, null, "stop"), (autonomy.MaxCallsPerHour, autonomy.ApprovalTimeout, autonomy.OnTimeout));
    }
}
