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
    public async Task A_1Password_app_that_authorized_CodeAlta_makes_it_ready_though_whoami_says_not_signed_in()
    {
        using var vault = new FakeVault();
        vault.State["whoamiSignedOut"] = true;
        vault.Save();
        vault.WriteConfig($"[credentials.personal]\ntype = \"1password\"\npath = \"{vault.TomlPath}\"\nunlock_at_start = false\n");
        new LocalSecretStore(vault.Profile).Bind("ANTHROPIC_API_KEY", "personal", "op://Private/Anthropic/credential", "label", "alice");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, CanPrompt = true });
        await broker.RefreshAsync();
        var locked = broker.GetProvider("personal")!.Status;
        Assert.AreEqual((CredentialProviderState.Locked, "alice@example.com"), (locked.State, locked.Account), "the account the app gives is known before it authorizes CodeAlta");
        Assert.IsFalse(vault.Calls().Any(static call => call.StartsWith("read", StringComparison.Ordinal) || call.StartsWith("vault", StringComparison.Ordinal)),
            "nothing that would show the prompt of the app runs without the user");

        var unlocked = await broker.UnlockAsync("personal", null);
        Assert.AreEqual((CredentialProviderState.Ready, "alice@example.com"), (unlocked.Status.State, unlocked.Status.Account), unlocked.Status.Message);
        Assert.AreEqual("sk-ant-fake-value-1234", broker.ReadBound("ANTHROPIC_API_KEY", "provider anthropic").Value, "the secrets of the vault are read once it is unlocked");
        Assert.AreEqual(CredentialProviderState.Ready, (await broker.TestProviderAsync("personal")).Status.State, "a check after the unlock keeps it ready");

        broker.Lock("personal");
        Assert.IsNull(broker.ReadBound("ANTHROPIC_API_KEY", "provider anthropic").Value);
    }

    [TestMethod]
    public async Task The_developer_instance_unlocks_a_vault_on_a_click_once_the_user_lets_it_and_never_at_start()
    {
        using var vault = new FakeVault();
        vault.WriteConfig($"[credentials.personal]\ntype = \"1password\"\npath = \"{vault.TomlPath}\"\n");
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, DeveloperInstance = true });
        Assert.IsTrue(broker.DeveloperInstance);
        Assert.IsFalse(broker.CanPrompt, "by default the developer instance asks no vault to unlock");

        broker.Settings.SetDeveloperInstanceUnlocks(true);
        StringAssert.Contains(File.ReadAllText(vault.ConfigPath), "developer_instance = \"unlock\"");
        Assert.IsTrue(broker.CanPrompt, "the setting is read as it is now: the window offers Unlock at once");
        await broker.RefreshAsync();
        await broker.UnlockAtStartAsync();
        Assert.IsFalse(vault.Calls().Any(static call => call.StartsWith("vault", StringComparison.Ordinal)), "a developer instance an agent starts shows no prompt by itself");
        Assert.AreEqual(CredentialProviderState.Ready, (await broker.UnlockAsync("personal", null)).Status.State, "a click unlocks it");

        broker.Settings.SetDeveloperInstanceUnlocks(false);
        Assert.IsFalse(broker.CanPrompt);
        Assert.DoesNotContain("developer_instance", File.ReadAllText(vault.ConfigPath));
        using var normal = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, CanPrompt = true });
        Assert.AreEqual((true, false), (normal.CanPrompt, normal.DeveloperInstance), "the normal instance prompts whatever the setting");
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
    public async Task A_use_that_waits_is_awaited_until_the_user_confirms_it_and_the_waits_share_the_answer()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile);
        store.SetSecret("LOCAL", "local-value-1", "alice");
        store.Bindings.SetAskBeforeUse("LOCAL", SecretAskDurations.FifteenMinutes);
        var verifier = new Verifier();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Verifier = verifier, CanPrompt = true });
        var changes = 0;
        broker.PendingUsesChanged += (_, _) => Interlocked.Increment(ref changes);
        StringAssert.Contains(broker.AuthorizeUse("LOCAL", "shell of the session s1"), "asks before use");
        Assert.IsNull(broker.ListPending().Single().AwaitedUntil, "nothing waits for the answer yet");

        var first = broker.WaitForConfirmationAsync("LOCAL", "shell of the session s1", TimeSpan.FromMinutes(2));
        var second = broker.WaitForConfirmationAsync("local", "shell of the session s1", TimeSpan.FromMinutes(2));
        Assert.IsNotNull(broker.ListPending().Single().AwaitedUntil, "the window shows that something waits");
        Assert.IsFalse(first.IsCompleted);

        Assert.AreEqual(UserPresenceResult.Verified, await broker.ConfirmAsync("LOCAL", "shell of the session s1"));
        CollectionAssert.AreEqual(new[] { SecretConfirmationOutcome.Confirmed, SecretConfirmationOutcome.Confirmed },
            await Task.WhenAll(first, second).WithinAsync(TestTime.Seconds(30)));
        Assert.HasCount(1, verifier.Messages, "one verification for the waits of one use");
        Assert.IsNull(broker.AuthorizeUse("LOCAL", "shell of the session s1"), "the use is confirmed for as long as the secret says");
        Assert.AreEqual(SecretConfirmationOutcome.Confirmed, await broker.WaitForConfirmationAsync("LOCAL", "shell of the session s1", TimeSpan.FromSeconds(1)),
            "a use confirmed already waits for nothing");
        Assert.IsGreaterThanOrEqualTo(3, Volatile.Read(ref changes), "the window is told as the waits start and end");
    }

    [TestMethod]
    public async Task A_denied_use_ends_its_waits_and_a_use_not_answered_in_time_stops_waiting()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile);
        store.SetSecret("LOCAL", "local-value-1", "alice");
        store.Bindings.SetAskBeforeUse("LOCAL", SecretAskDurations.EachUse);
        var verifier = new Verifier();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Verifier = verifier, CanPrompt = true });

        broker.AuthorizeUse("LOCAL", "shell of the session s1");
        var denied = broker.WaitForConfirmationAsync("LOCAL", "shell of the session s1", TimeSpan.FromMinutes(2));
        broker.Decline("LOCAL", "shell of the session s1");
        Assert.AreEqual(SecretConfirmationOutcome.Declined, await denied.WithinAsync(TestTime.Seconds(30)));
        Assert.AreEqual(SecretConfirmationOutcome.Declined, await broker.WaitForConfirmationAsync("LOCAL", "shell of the session s1", TimeSpan.FromSeconds(1)),
            "a use that no longer waits, without a confirmation");

        broker.AuthorizeUse("LOCAL", "shell of the session s2");
        Assert.AreEqual(SecretConfirmationOutcome.TimedOut, await broker.WaitForConfirmationAsync("LOCAL", "shell of the session s2", TimeSpan.FromMilliseconds(200))
            .WithinAsync(TestTime.Seconds(30)));
        Assert.IsEmpty(broker.ListPending(), "a use nothing waits for stops waiting");
        Assert.AreEqual(("shell of the session s2", "expired"), (broker.ListUses().First().Use, broker.ListUses().First().Outcome));
        Assert.IsEmpty(verifier.Messages, "nothing is verified without the user's Confirm");

        broker.AuthorizeUse("LOCAL", "shell of the session s3");
        using var stop = new CancellationTokenSource();
        var canceled = broker.WaitForConfirmationAsync("LOCAL", "shell of the session s3", TimeSpan.FromMinutes(2), stop.Token);
        stop.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => canceled);
        Assert.IsEmpty(broker.ListPending(), "a wait that was stopped leaves no use waiting: nothing would take its confirmation");
        Assert.AreEqual(("shell of the session s3", "expired"), (broker.ListUses().First().Use, broker.ListUses().First().Outcome));
        Assert.AreEqual(UserPresenceResult.Verified, await broker.ConfirmAsync("LOCAL", "shell of the session s3"), "the use can still be confirmed ahead, for its next start");
    }

    [TestMethod]
    public async Task One_verification_confirms_the_secrets_a_use_asks_for_at_once()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile);
        foreach (var name in (string[])["DEPLOY", "REGISTRY", "SIGNING"])
        {
            store.SetSecret(name, name.ToLowerInvariant() + "-value-1", "alice");
            store.Bindings.SetAskBeforeUse(name, SecretAskDurations.FifteenMinutes);
        }

        var verifier = new Verifier();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Verifier = verifier, CanPrompt = true });
        const string use = "shell of the session s1";
        foreach (var name in (string[])["DEPLOY", "REGISTRY", "SIGNING"]) broker.AuthorizeUse(name, use);
        var waits = ((string[])["DEPLOY", "REGISTRY", "SIGNING"]).Select(name => broker.WaitForConfirmationAsync(name, use, TimeSpan.FromMinutes(2))).ToArray();
        // One of them is denied before the user confirms the others.
        broker.Decline("SIGNING", use);

        Assert.AreEqual(UserPresenceResult.Verified, await broker.ConfirmAsync(["DEPLOY", "registry", "DEPLOY"], use));

        StringAssert.Contains(verifier.Messages.Single(), "the secrets DEPLOY and registry for the shell of the session s1", "one verification names them all");
        CollectionAssert.AreEqual(new[] { SecretConfirmationOutcome.Confirmed, SecretConfirmationOutcome.Confirmed, SecretConfirmationOutcome.Declined },
            await Task.WhenAll(waits).WithinAsync(TestTime.Seconds(30)));
        Assert.IsNull(broker.AuthorizeUse("DEPLOY", use));
        Assert.IsNull(broker.AuthorizeUse("REGISTRY", use));
        Assert.IsEmpty(broker.ListPending());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => broker.ConfirmAsync(Array.Empty<string>(), use));
    }

    [TestMethod]
    public async Task A_read_that_asks_nobody_leaves_nothing_waiting_and_takes_no_confirmation_of_one_use()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile);
        store.SetSecret("ONCE", "once-value-1", "alice");
        store.Bindings.SetAskBeforeUse("ONCE", SecretAskDurations.EachUse);
        store.SetSecret("LASTING", "lasting-value-1", "alice");
        store.Bindings.SetAskBeforeUse("LASTING", SecretAskDurations.FifteenMinutes);
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Verifier = new Verifier(), CanPrompt = true });
        const string use = "shell of the session s1";
        var shown = broker.WithoutAsking(recorded: false);

        StringAssert.Contains(shown.AuthorizeUse("ONCE", use), "is not confirmed");
        Assert.IsEmpty(broker.ListPending(), "nothing waits for the user");
        Assert.IsEmpty(broker.ListUses(), "what only shows an environment records no use");

        // Confirmed ahead: a confirmation of one use stays for the use that starts, and one that lasts is taken as it is.
        await broker.ConfirmAsync("ONCE", use);
        await broker.ConfirmAsync("LASTING", use);
        Assert.IsNotNull(shown.AuthorizeUse("ONCE", use), "a confirmation of one use is not taken by a read that asks nobody");
        Assert.IsNull(shown.AuthorizeUse("LASTING", use), "a confirmation that lasts is");
        Assert.IsNull(broker.AuthorizeUse("ONCE", use), "the confirmation of one use is still there for the use that starts");

        // A program that starts without the secret records it.
        StringAssert.Contains(broker.WithoutAsking(recorded: true).AuthorizeUse("ONCE", "shell of the session s2"), "is not confirmed");
        Assert.AreEqual(("shell of the session s2", "unavailable"), (broker.ListUses().First().Use, broker.ListUses().First().Outcome));
        Assert.IsEmpty(broker.ListPending());
    }

    [TestMethod]
    public void What_a_confirmed_use_started_takes_the_secret_once_without_asking()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile);
        store.SetSecret("LOCAL", "local-value-1", "alice");
        store.Bindings.SetAskBeforeUse("LOCAL", SecretAskDurations.FifteenMinutes);
        var verifier = new Verifier();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Verifier = verifier, CanPrompt = true });
        Assert.IsTrue(broker.AsksBeforeUse("local"));
        Assert.IsFalse(broker.AsksBeforeUse("OTHER"));

        broker.AllowOnce("LOCAL", "shell of the session child");

        Assert.IsNull(broker.AuthorizeUse("LOCAL", "shell of the session child"));
        Assert.IsEmpty(verifier.Messages, "the user is not asked again");
        StringAssert.Contains(broker.AuthorizeUse("LOCAL", "shell of the session child"), "asks before use", "once: its next start asks");
        broker.Expire("LOCAL", "shell of the session child");
        Assert.IsEmpty(broker.ListPending(), "a use nothing takes any more no longer waits");
    }

    [TestMethod]
    public async Task A_use_denied_while_the_system_verifies_the_user_is_not_confirmed_by_that_verification()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile);
        store.SetSecret("LOCAL", "local-value-1", "alice");
        store.Bindings.SetAskBeforeUse("LOCAL", SecretAskDurations.FifteenMinutes);
        var verifier = new HeldVerifier();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Verifier = verifier, CanPrompt = true });
        broker.AuthorizeUse("LOCAL", "shell of the session s1");
        var waiting = broker.WaitForConfirmationAsync("LOCAL", "shell of the session s1", TimeSpan.FromMinutes(2));

        // Confirm… in one window, Deny in another while Touch ID is shown.
        var confirming = broker.ConfirmAsync("LOCAL", "shell of the session s1");
        await verifier.Asked.Task.WithinAsync(TestTime.Seconds(30));
        broker.Decline("LOCAL", "shell of the session s1");
        Assert.AreEqual(SecretConfirmationOutcome.Declined, await waiting.WithinAsync(TestTime.Seconds(30)));
        verifier.Answer.SetResult(UserPresenceResult.Verified);

        Assert.AreEqual(UserPresenceResult.Canceled, await confirming.WithinAsync(TestTime.Seconds(30)));
        StringAssert.Contains(broker.AuthorizeUse("LOCAL", "shell of the session s1"), "asks before use", "the denial stands");
        Assert.IsFalse(broker.ListUses().Any(static use => use.Outcome == "confirmed"));

        // Denying every use of the secret stands the same way; a verification that starts after a denial confirms.
        verifier.Reset();
        confirming = broker.ConfirmAsync("LOCAL", "shell of the session s1");
        await verifier.Asked.Task.WithinAsync(TestTime.Seconds(30));
        broker.Decline("LOCAL", null);
        verifier.Answer.SetResult(UserPresenceResult.Verified);
        Assert.AreEqual(UserPresenceResult.Canceled, await confirming.WithinAsync(TestTime.Seconds(30)));
        verifier.Reset();
        verifier.Answer.SetResult(UserPresenceResult.Verified);
        Assert.AreEqual(UserPresenceResult.Verified, await broker.ConfirmAsync("LOCAL", "shell of the session s1").WithinAsync(TestTime.Seconds(30)));
        Assert.IsNull(broker.AuthorizeUse("LOCAL", "shell of the session s1"));
    }

    [TestMethod]
    public async Task A_use_that_stopped_waiting_while_the_system_verifies_the_user_is_not_confirmed_by_that_verification()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile);
        store.SetSecret("LOCAL", "local-value-1", "alice");
        store.Bindings.SetAskBeforeUse("LOCAL", SecretAskDurations.FifteenMinutes);
        var verifier = new HeldVerifier();
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Verifier = verifier, CanPrompt = true });
        broker.AuthorizeUse("LOCAL", "shell of the session s1");
        var waiting = broker.WaitForConfirmationAsync("LOCAL", "shell of the session s1", TimeSpan.FromMilliseconds(300));

        var confirming = broker.ConfirmAsync("LOCAL", "shell of the session s1");
        await verifier.Asked.Task.WithinAsync(TestTime.Seconds(30));
        Assert.AreEqual(SecretConfirmationOutcome.TimedOut, await waiting.WithinAsync(TestTime.Seconds(30)));
        verifier.Answer.SetResult(UserPresenceResult.Verified);

        Assert.AreEqual(UserPresenceResult.Canceled, await confirming.WithinAsync(TestTime.Seconds(30)));
        StringAssert.Contains(broker.AuthorizeUse("LOCAL", "shell of the session s1"), "asks before use", "the shell started without it, and the next start asks again");
    }

    [TestMethod]
    public async Task Nothing_waits_for_a_confirmation_where_nobody_can_answer()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile);
        store.SetSecret("LOCAL", "local-value-1", "alice");
        store.Bindings.SetAskBeforeUse("LOCAL", SecretAskDurations.EachUse);
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Verifier = new Verifier() });
        broker.AuthorizeUse("LOCAL", "shell of the session s1");

        Assert.AreEqual(SecretConfirmationOutcome.Unavailable, await broker.WaitForConfirmationAsync("LOCAL", "shell of the session s1", TimeSpan.FromMinutes(2)));
    }

    [TestMethod]
    public async Task Nothing_waits_for_a_confirmation_while_the_system_cannot_verify_the_user()
    {
        using var vault = new FakeVault();
        var store = new LocalSecretStore(vault.Profile);
        store.SetSecret("LOCAL", "local-value-1", "alice");
        store.Bindings.SetAskBeforeUse("LOCAL", SecretAskDurations.EachUse);
        // Windows Hello was set up when the secret was told to ask before use, and is turned off now.
        var verifier = new Verifier { Available = false };
        using var broker = new CredentialBroker(new CredentialBrokerOptions { GlobalRoot = vault.Profile, Verifier = verifier, CanPrompt = true });
        broker.AuthorizeUse("LOCAL", "shell of the session s1");

        Assert.AreEqual(SecretConfirmationOutcome.Unavailable, await broker.WaitForConfirmationAsync("LOCAL", "shell of the session s1", TimeSpan.FromMinutes(2))
            .WithinAsync(TestTime.Seconds(30)));
        Assert.IsNull(broker.ListPending().Single().AwaitedUntil, "nothing waited for the answer");
        Assert.IsEmpty(verifier.Messages);
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

    // A verification that waits for the test: the system prompt is shown until the test answers it.
    private sealed class HeldVerifier : IUserPresenceVerifier
    {
        public TaskCompletionSource Asked { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<UserPresenceResult> Answer { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Reset()
        {
            Asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public ValueTask<UserPresenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new UserPresenceAvailability(true, null));

        public async ValueTask<UserPresenceResult> VerifyAsync(string message, CancellationToken cancellationToken)
        {
            var answer = Answer;
            Asked.TrySetResult();
            return await answer.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Verifier : IUserPresenceVerifier
    {
        public UserPresenceResult Answer { get; set; } = UserPresenceResult.Verified;

        public bool Available { get; set; } = true;

        public List<string> Messages { get; } = [];

        public ValueTask<UserPresenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new UserPresenceAvailability(Available, Available ? null : "Windows Hello is not set up."));

        public ValueTask<UserPresenceResult> VerifyAsync(string message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return ValueTask.FromResult(Answer);
        }
    }
}
