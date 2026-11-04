using CodeAlta.Automations;

namespace CodeAlta.Desktop.Tests;

[TestClass]
public sealed class AutomationIntegrationConfigTests
{
    private const string First = "0199f4c2-6d1e-7c3a-b5f0-2f9c8e4a1d77";
    private const string Hook = "h0123456789abcdefghij";
    private static readonly AutomationSource Global = new("config.toml", null, null);

    private const string Triggers = $$"""
        [automations.{{First}}]
        name = "Other tools"
        prompt = "Look at it."
        triggers = [
          { type = "jira_version", site = "https://acme.atlassian.net/", email = "bot@acme.test", token = "${secret:JIRA_TOKEN}", jira_project = "PLAT" },
          { type = "jira_query", event = "updated", site = "${var:JIRA_SITE}", email = "bot@acme.test", token = "${secret:JIRA_TOKEN}", jql = "project = PLAT AND status = \"To Do\"", reply = true },
          { type = "slack", event = "mention", channel = "C0123ABCDEF", match = "deploy", app_token = "${secret:SLACK_APP}", bot_token = "${secret:SLACK_BOT}", reply = true },
          { type = "incident_io", hook = "{{Hook}}", secret = "${secret:INCIDENT_SIGNING}", api_key = "${secret:INCIDENT_KEY}", reply = true },
          { type = "webhook", hook = "{{Hook}}", secret = "${secret:SENTRY}", signature = "hmac_sha256", signature_header = "Sentry-Hook-Signature", fields = ["/action", "/data/issue/title"] },
        ]
        """;

    [TestMethod]
    public void Read_TakesTheTriggersOfOtherTools()
    {
        var (definitions, faults) = AutomationConfig.Read(Triggers, Global);

        Assert.AreEqual(0, faults.Count, string.Join("; ", faults.Select(static fault => fault.Message)));
        var triggers = definitions.Single().Triggers;
        CollectionAssert.AreEqual(new[]
        {
            "jira_version:https://acme.atlassian.net\tPLAT",
            "jira_query:updated:${var:JIRA_SITE}\tproject = PLAT AND status = \"To Do\"",
            "slack:mention:C0123ABCDEF\tdeploy",
            $"incident_io:declared:{Hook}",
            $"webhook:{Hook}",
        }, triggers.Select(static trigger => trigger.Key).ToArray());
        Assert.IsTrue(triggers.All(static trigger => trigger is { IsIntegration: true, IsSchedule: false, IsEvent: false, IsCommand: false }));
        Assert.AreEqual("released", triggers[0].Event);
        Assert.AreEqual("${secret:JIRA_TOKEN}", triggers[1].Token);
        Assert.IsTrue(triggers[1].Reply);
        Assert.AreEqual("${secret:SLACK_BOT}", triggers[2].BotToken);
        Assert.AreEqual(AutomationSignatures.Standard, triggers[3].Signature);
        CollectionAssert.AreEqual(new[] { "/action", "/data/issue/title" }, triggers[4].Fields.ToArray());
    }

    [TestMethod]
    public void Write_KeepsEveryPartOfTheTriggersOfOtherTools()
    {
        var definition = AutomationConfig.Read(Triggers, Global).Definitions.Single();

        var written = AutomationConfig.Write(string.Empty, definition);
        var (again, faults) = AutomationConfig.Read(written, Global);

        Assert.AreEqual(0, faults.Count, string.Join("; ", faults.Select(static fault => fault.Message)) + "\n" + written);
        Assert.AreEqual(definition.Fingerprint(), again.Single().Fingerprint(), written);
        CollectionAssert.AreEqual(definition.Triggers.Select(static trigger => trigger.FingerprintKey).ToArray(),
            again.Single().Triggers.Select(static trigger => trigger.FingerprintKey).ToArray());
    }

    [TestMethod]
    public void Read_RefusesATokenWrittenInClear()
    {
        var (definitions, faults) = AutomationConfig.Read($$"""
            [automations.{{First}}]
            name = "Leaky"
            prompt = "Look."
            triggers = [{ type = "jira_query", site = "https://acme.atlassian.net", email = "bot@acme.test", token = "ATATT3xFfGF0secretvalue", jql = "project = PLAT" }]
            """, Global);

        Assert.AreEqual(0, definitions.Count);
        StringAssert.Contains(faults.Single().Message, "${secret:NAME}");
    }

    [TestMethod]
    [DataRow("""{ type = "jira_query", site = "https://acme.atlassian.net", email = "a@b.c", token = "${secret:T}" }""", "JQL")]
    [DataRow("""{ type = "jira_version", site = "http://acme.atlassian.net", email = "a@b.c", token = "${secret:T}", jira_project = "PLAT" }""", "https")]
    [DataRow("""{ type = "jira_version", site = "https://acme.atlassian.net", email = "a@b.c", token = "${secret:T}", jira_project = "plat" }""", "key of its Jira project")]
    [DataRow("""{ type = "slack", app_token = "${secret:A}", bot_token = "${secret:B}", channel = "#general" }""", "id of its channel")]
    [DataRow("""{ type = "slack", app_token = "${secret:A}", channel = "C0123ABCDEF" }""", "bot token")]
    [DataRow("""{ type = "incident_io", secret = "${secret:S}", api_key = "${secret:K}" }""", "hook of the server")]
    [DataRow("""{ type = "webhook", hook = "h0123456789abcdefghij", secret = "${secret:S}", signature = "hmac_sha256" }""", "header of its signature")]
    [DataRow("""{ type = "webhook", hook = "h0123456789abcdefghij", secret = "${secret:S}", fields = ["action"] }""", "JSON pointers")]
    [DataRow("""{ type = "webhook", hook = "h0123456789abcdefghij", secret = "${secret:S}", reply = true }""", "nowhere to reply")]
    [DataRow("""{ type = "slack", event = "reaction", app_token = "${secret:A}", bot_token = "${secret:B}", channel = "C0123ABCDEF" }""", "message or mention")]
    [DataRow("""{ type = "slack", app_token = "${secret:A}", bot_token = "${secret:B}", channel = "C0123ABCDEF", token = "${secret:T}" }""", "has no site, email or token")]
    public void Read_SaysWhatATriggerOfAnotherToolLacks(string trigger, string expected)
    {
        var (definitions, faults) = AutomationConfig.Read($$"""
            [automations.{{First}}]
            name = "Wrong"
            prompt = "Look."
            triggers = [{{trigger}}]
            """, Global);

        Assert.AreEqual(0, definitions.Count);
        StringAssert.Contains(faults.Single().Message, expected);
    }

    [TestMethod]
    public void Fingerprint_ChangesWithTheSecretsAndTheReplyOfATrigger()
    {
        var definition = AutomationConfig.Read(Triggers, Global).Definitions.Single();
        var slack = definition.Triggers[2];

        var otherBot = definition with { Triggers = [.. definition.Triggers.Select(trigger => trigger == slack ? slack with { BotToken = "${secret:OTHER}" } : trigger)] };
        var noReply = definition with { Triggers = [.. definition.Triggers.Select(trigger => trigger == slack ? slack with { Reply = false } : trigger)] };

        Assert.AreNotEqual(definition.Fingerprint(), otherBot.Fingerprint());
        Assert.AreNotEqual(definition.Fingerprint(), noReply.Fingerprint());
        // What the trigger watches is the same: what it has seen stays.
        Assert.AreEqual(slack.Key, otherBot.Triggers[2].Key);
    }

    [TestMethod]
    public void Fingerprint_OfAnAutomationWithoutThemDoesNotChange()
    {
        var definition = new AutomationDefinition(First, "Nightly") { Prompt = "Review.", Triggers = [new AutomationTrigger(AutomationTriggerKind.Daily) { At = [new(9, 0)] }] };

        // The value an automation was allowed with before these triggers existed.
        Assert.AreEqual(definition.Triggers[0].Key, definition.Triggers[0].FingerprintKey);
    }
}
