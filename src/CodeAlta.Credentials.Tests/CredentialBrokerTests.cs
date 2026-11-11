using CodeAlta.Catalog.Secrets;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Tests;

[TestClass]
public sealed class CredentialBrokerTests
{
    [TestMethod]
    public async Task A_locked_1Password_is_never_read_without_a_prompt_and_is_read_once_unlocked()
    {
        using var vault = new FakeVault();
        vault.WriteConfig($"[credentials.personal]\ntype = \"1password\"\npath = \"{vault.TomlPath}\"\n");
        new LocalSecretStore(vault.Profile).Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "Private › Anthropic › credential", "alice");
        var masked = new List<string>();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Mask = masked.Add, CanPrompt = true });

        await broker.RefreshAsync();
        Assert.AreEqual(CredentialProviderState.Locked, broker.GetProvider("personal")!.Status.State);
        Assert.IsFalse(vault.Calls().Any(static call => call.StartsWith("read", StringComparison.Ordinal) || call.StartsWith("vault", StringComparison.Ordinal)),
            "a locked vault is asked only whoami, which shows no prompt: " + string.Join(" | ", vault.Calls()));
        var locked = broker.ReadBound("ANTHROPIC_API_KEY", "provider anthropic");
        Assert.IsNull(locked.Value);
        StringAssert.Contains(locked.Problem, "1Password");
        StringAssert.Contains(locked.Problem, "locked");

        var view = await broker.UnlockAsync("personal", null);
        Assert.AreEqual(CredentialProviderState.Ready, view.Status.State);
        Assert.AreEqual(1, view.Held);
        Assert.AreEqual("alice@example.com", view.Status.Account);
        Assert.Contains("sk-ant-fake-value-1234", masked, "the value is masked before anything gets it");
        Assert.AreEqual("sk-ant-fake-value-1234", broker.ReadBound("ANTHROPIC_API_KEY", "provider anthropic").Value);
        Assert.AreEqual("sk-ant-fake-value-1234", SecretReferenceResolver.ForMachine(vault.Profile, null, broker, "provider anthropic").Resolve("${secret:ANTHROPIC_API_KEY}"));
        Assert.AreEqual("given", broker.ListUses()[0].Outcome);

        broker.Lock("personal");
        Assert.IsNull(broker.ReadBound("ANTHROPIC_API_KEY", "provider anthropic").Value, "a locked provider forgets its values");
    }

    [TestMethod]
    public async Task A_vault_signed_in_is_read_at_start_and_a_missing_field_says_so()
    {
        using var vault = new FakeVault();
        vault.State["signedIn"] = true;
        vault.Save();
        vault.WriteConfig($"[credentials.personal]\ntype = \"1password\"\npath = \"{vault.TomlPath}\"\nunlock_at_start = false\n");
        var store = new LocalSecretStore(vault.Profile);
        store.Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "label", "alice");
        store.Bind("GONE", "personal", "op://Private/Anthropic/nothing", "label", "alice");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile });
        await broker.RefreshAsync();

        Assert.AreEqual(SecretAvailabilityState.Ready, broker.GetAvailability("ANTHROPIC_API_KEY")!.State);
        var gone = broker.GetAvailability("GONE")!;
        Assert.AreEqual(SecretAvailabilityState.NotFound, gone.State);
        Assert.IsFalse(broker.GetProvider("personal")!.UnlockAtStart);
        Assert.DoesNotContain("sk-ant", gone.Message!);
    }

    [TestMethod]
    public async Task The_picker_lists_names_never_values()
    {
        using var vault = new FakeVault();
        vault.State["signedIn"] = true;
        vault.Save();
        vault.WriteConfig($"[credentials.personal]\ntype = \"1password\"\npath = \"{vault.TomlPath}\"\n");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile });
        var vaults = await broker.BrowseAsync("personal", null);
        var items = await broker.BrowseAsync("personal", vaults.Entries.Single().Id);
        Assert.AreEqual("Anthropic", items.Entries.Single().Label);
        var fields = await broker.BrowseAsync("personal", items.Entries.Single().Id);
        Assert.AreEqual(2, fields.Entries.Count);
        Assert.AreEqual("op://Private/Anthropic/credential", fields.Entries[0].Reference);
        Assert.AreEqual("concealed", fields.Entries[0].Detail);
        Assert.AreEqual("Private › Anthropic", fields.Path);
        Assert.IsFalse(fields.ToString().Contains("sk-ant", StringComparison.Ordinal) || fields.Entries.Any(static entry => entry.ToString().Contains("sk-ant", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task A_1Password_app_that_gives_op_no_account_is_unlocked_from_CodeAlta_never_signed_in_in_a_terminal()
    {
        // The app authorizes the process that runs op: a sign-in typed in a terminal authorizes that terminal, never CodeAlta.
        using var vault = new FakeVault();
        vault.State["accounts"] = false;
        vault.Save();
        vault.WriteConfig($"[credentials.personal]\ntype = \"1password\"\naccount = \"my.1password.com\"\npath = \"{vault.TomlPath}\"\nunlock_at_start = false\n");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, CanPrompt = true });
        await broker.RefreshAsync();

        var status = broker.GetProvider("personal")!.Status;
        Assert.AreEqual(CredentialProviderState.Locked, status.State, "Unlock is what the user does: it asks the app from CodeAlta");
        Assert.IsNull(status.SignInCommand, "no command of a terminal can authorize CodeAlta");
        StringAssert.Contains(status.Message, "Integrate with 1Password CLI");
        StringAssert.Contains(status.Message, "my.1password.com", "the account set is named, as it may not be one of the app");

        // Still no account: the unlock says the same, without a terminal.
        var refused = await broker.UnlockAsync("personal", null);
        Assert.AreEqual((CredentialProviderState.Locked, (IReadOnlyList<string>?)null), (refused.Status.State, refused.Status.SignInCommand));

        // The integration turned on: the unlock asks the app, which authorizes CodeAlta.
        vault.Reload();
        vault.State["accounts"] = true;
        vault.Save();
        Assert.AreEqual(CredentialProviderState.Ready, (await broker.UnlockAsync("personal", null)).Status.State);
    }

    [TestMethod]
    public async Task A_service_account_token_is_a_secret_of_this_computer_given_in_the_environment_of_op()
    {
        using var vault = new FakeVault();
        vault.State["accounts"] = false;
        vault.Save();
        var store = new LocalSecretStore(vault.Profile);
        store.SetSecret("OP_CI_TOKEN", "ops_fake_token_value", "alice");
        vault.WriteConfig($"[credentials.ci]\ntype = \"1password\"\nservice_account_token = \"${{secret:OP_CI_TOKEN}}\"\npath = \"{vault.TomlPath}\"\n");
        store.Bind("DEPLOY", "ci", "op://Private/Anthropic/username", "label", "alice");
        var masked = new List<string>();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Mask = masked.Add });
        await broker.RefreshAsync();
        Assert.AreEqual(CredentialProviderState.Ready, broker.GetProvider("ci")!.Status.State);
        Assert.AreEqual("alice", broker.ReadBound("DEPLOY", null).Value);
        Assert.Contains("ops_fake_token_value", masked, "the token is masked as a value the host gave");
        Assert.IsFalse(vault.Calls().Any(static call => call.Contains("ops_fake", StringComparison.Ordinal)), "the token is in no argument");

        // A token kept in a vault would open a vault with a key it keeps.
        store.Bind("OP_CI_TOKEN", "ci", "op://Private/Anthropic/credential", "label", "alice");
        await broker.RefreshAsync();
        vault.WriteConfig($"[credentials.ci]\ntype = \"1password\"\nservice_account_token = \"${{secret:OP_CI_TOKEN}}\"\npath = \"{vault.TomlPath}\"\ndisplay_name = \"CI\"\n");
        await broker.RefreshAsync();
        StringAssert.Contains(broker.GetProvider("ci")!.Status.Message, "kept in a vault");
    }

    [TestMethod]
    public async Task A_command_provider_reads_with_the_parts_of_the_reference_and_never_through_a_shell()
    {
        using var vault = new FakeVault("vaulttool");
        vault.WriteConfig($"[credentials.team]\ntype = \"command\"\nread = [\"{vault.TomlPath}\", \"print\", \"value-of-{{item}}-{{field}}\"]\n");
        new LocalSecretStore(vault.Profile).Bind("TEAM_TOKEN", "team", "deploy/prod#token", "deploy/prod#token", "alice");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile });
        await broker.RefreshAsync();
        Assert.AreEqual("value-of-deploy/prod-token", broker.ReadBound("TEAM_TOKEN", null).Value);
    }

    [TestMethod]
    public async Task A_secret_that_asks_before_use_waits_for_a_confirmation_of_each_use()
    {
        using var vault = new FakeVault();
        vault.State["signedIn"] = true;
        vault.Save();
        vault.WriteConfig($"[credentials.personal]\ntype = \"1password\"\npath = \"{vault.TomlPath}\"\n");
        var store = new LocalSecretStore(vault.Profile);
        store.Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "label", "alice");
        store.Bindings.SetAskBeforeUse("ANTHROPIC_API_KEY", SecretAskDurations.FifteenMinutes);
        var verifier = new Verifier();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Verifier = verifier, CanPrompt = true });
        await broker.RefreshAsync();

        var waiting = broker.ReadBound("ANTHROPIC_API_KEY", "provider anthropic");
        Assert.IsNull(waiting.Value);
        StringAssert.Contains(waiting.Problem, "asks before use");
        Assert.AreEqual("provider anthropic", broker.ListPending().Single().Use);
        Assert.AreEqual(SecretAvailabilityState.WaitsForYou, broker.GetAvailability("ANTHROPIC_API_KEY")!.State);

        verifier.Answer = UserPresenceResult.Canceled;
        Assert.AreEqual(UserPresenceResult.Canceled, await broker.ConfirmAsync("ANTHROPIC_API_KEY", "provider anthropic"));
        Assert.IsNull(broker.ReadBound("ANTHROPIC_API_KEY", "provider anthropic").Value);

        verifier.Answer = UserPresenceResult.Verified;
        Assert.AreEqual(UserPresenceResult.Verified, await broker.ConfirmAsync("ANTHROPIC_API_KEY", "provider anthropic"));
        StringAssert.Contains(verifier.Messages[^1], "provider anthropic");
        Assert.AreEqual("sk-ant-fake-value-1234", broker.ReadBound("ANTHROPIC_API_KEY", "provider anthropic").Value);
        Assert.IsNull(broker.ReadBound("ANTHROPIC_API_KEY", "MCP server grafana").Value, "another use asks again");
        Assert.HasCount(1, broker.ListPending());
    }

    [TestMethod]
    public async Task Nobody_confirms_where_nobody_can_be_asked()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile);
        store.SetSecret("LOCAL", "local-value-1", "alice");
        store.Bindings.SetAskBeforeUse("LOCAL", SecretAskDurations.EachUse);
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Verifier = new Verifier() });
        StringAssert.Contains(broker.AuthorizeUse("LOCAL", "the shell of a session"), "nobody can confirm");
        Assert.AreEqual(UserPresenceResult.Unavailable, await broker.ConfirmAsync("LOCAL", "the shell of a session"));
    }

    [TestMethod]
    public void The_providers_of_the_configuration_are_written_and_read_back_whole()
    {
        using var vault = new FakeVault();
        vault.WriteConfig("# mine\n[chat]\ndefault_provider = \"x\"\n");
        var settings = new CredentialProviderSettings(vault.ConfigPath);
        settings.Save(new CredentialProviderDefinition("team", "command", new Dictionary<string, object?> { ["read"] = new object?[] { "tool", "get", "{ref}" }, ["json"] = "data.value" })
        {
            Cache = CredentialCacheDurations.OneHour,
            UnlockAtStart = false,
        });
        var read = settings.Load(out var problems).Single();
        Assert.IsEmpty(problems);
        Assert.AreEqual(("team", "command", "1h", false), (read.Id, read.Type, read.Cache, read.UnlockAtStart));
        CollectionAssert.AreEqual(new object?[] { "tool", "get", "{ref}" }, (object?[])read.Values["read"]!);
        StringAssert.Contains(File.ReadAllText(vault.ConfigPath), "# mine");
        Assert.IsTrue(settings.Remove("team"));
        Assert.IsEmpty(settings.Load(out _));
    }

    private sealed class Verifier : IUserPresenceVerifier
    {
        public UserPresenceResult Answer { get; set; } = UserPresenceResult.Verified;

        public List<string> Messages { get; } = [];

        public ValueTask<UserPresenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new UserPresenceAvailability(true, null));

        public ValueTask<UserPresenceResult> VerifyAsync(string message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return ValueTask.FromResult(Answer);
        }
    }
}
