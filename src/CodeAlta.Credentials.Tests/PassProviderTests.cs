using System.Text.Json.Nodes;
using CodeAlta.Credentials.Providers;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Tests;

[TestClass]
public sealed class PassProviderTests
{
    private const string Entry = "pass-password-value-111\nuser: alice\nurl: https://pass.example.com\nnotes follow";

    [TestMethod]
    public async Task Pass_gives_the_first_line_or_the_line_of_a_key_from_the_store_set()
    {
        using var vault = NewVault("pass");
        var store = Store(vault);
        vault.State["pass"]!["store"] = store;
        vault.Save();
        var provider = Create(vault, "pass", store);
        var status = await provider.GetStatusAsync(CancellationToken.None);
        Assert.AreEqual(CredentialProviderState.Ready, status.State);
        Assert.IsEmpty(vault.Calls(), "the status runs nothing");

        Assert.AreEqual("pass-password-value-111", (await provider.ReadAsync("web/github", false, CancellationToken.None)).Value);
        Assert.AreEqual("alice", (await provider.ReadAsync("web/github#user", false, CancellationToken.None)).Value);
        Assert.AreEqual("https://pass.example.com", (await provider.ReadAsync("web/github#URL", false, CancellationToken.None)).Value);
        Assert.AreEqual("show web/github", vault.Calls()[^1]);
        Assert.AreEqual(CredentialFailure.NotFound, (await ProviderHarness.ReadFailsAsync(provider, "web/github#nothing")).Failure);
        var missing = await ProviderHarness.ReadFailsAsync(provider, "web/none");
        Assert.AreEqual(CredentialFailure.NotFound, missing.Failure);
        StringAssert.Contains(missing.Message, "not in the password store");
        Assert.AreEqual(CredentialFailure.NotFound, (await ProviderHarness.ReadFailsAsync(provider, "--help")).Failure, "a reference is never an option");
    }

    [TestMethod]
    public async Task Gopass_shows_the_password_or_a_key_itself()
    {
        using var vault = NewVault("gopass");
        var provider = Create(vault, "gopass", null);
        Assert.AreEqual("pass-password-value-111", (await provider.ReadAsync("web/github", false, CancellationToken.None)).Value);
        Assert.AreEqual("show -o web/github", vault.Calls()[^1]);
        Assert.AreEqual("alice", (await provider.ReadAsync("web/github#user", false, CancellationToken.None)).Value);
        Assert.AreEqual("show web/github user", vault.Calls()[^1]);
        Assert.AreEqual(CredentialFailure.NotFound, (await ProviderHarness.ReadFailsAsync(provider, "web/none")).Failure);
    }

    [TestMethod]
    public async Task A_read_that_waits_for_pinentry_without_anybody_is_locked()
    {
        using var vault = NewVault("pass");
        vault.State["pass"]!["hang"] = true;
        vault.Save();
        var provider = new PassProvider(new PluginCredentialProviderContext("test", new Dictionary<string, object?> { ["path"] = vault.ProgramPath }), new VaultCommandRunner())
        {
            NonInteractiveTimeout = TimeSpan.FromSeconds(1),
        };
        var failure = await ProviderHarness.ReadFailsAsync(provider, "web/github", interactive: false);
        Assert.AreEqual(CredentialFailure.Locked, failure.Failure);
        StringAssert.Contains(failure.Message, "pinentry");
    }

    [TestMethod]
    public async Task A_read_without_a_prompt_asks_gpg_not_to_start_pinentry_and_is_locked_at_once()
    {
        using var vault = NewVault("pass");
        vault.State["pass"]!["needsPassphrase"] = true;
        vault.Save();
        var provider = new PassProvider(new PluginCredentialProviderContext("test", new Dictionary<string, object?> { ["path"] = vault.ProgramPath }), new VaultCommandRunner())
        {
            NonInteractiveTimeout = TimeSpan.FromSeconds(30),
        };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var failure = await ProviderHarness.ReadFailsAsync(provider, "web/github", interactive: false);
        Assert.AreEqual(CredentialFailure.Locked, failure.Failure);
        Assert.IsLessThan(TimeSpan.FromSeconds(15), watch.Elapsed, "gpg fails instead of waiting for pinentry");
        StringAssert.Contains(failure.Message, "passphrase");
        StringAssert.Contains(failure.Detail, "No pinentry");

        StringAssert.Contains(provider.ReadEnvironment(interactive: false)!["PASSWORD_STORE_GPG_OPTS"], "--pinentry-mode error");
        Assert.IsFalse(provider.ReadEnvironment(interactive: true)?.ContainsKey("PASSWORD_STORE_GPG_OPTS") == true, "a read with the user present lets pinentry ask");
    }

    [TestMethod]
    public async Task A_missing_or_unknown_program_says_so()
    {
        using var vault = NewVault("pass");
        var none = ProviderHarness.Create(PassProvider.Contribution(new VaultCommandRunner()), new Dictionary<string, object?> { ["path"] = Path.Combine(vault.Bin, "none.exe") });
        var status = await none.GetStatusAsync(CancellationToken.None);
        Assert.AreEqual(CredentialProviderState.NotInstalled, status.State);
        Assert.IsNotNull(status.Install?.Page);
        var other = ProviderHarness.Create(PassProvider.Contribution(new VaultCommandRunner()), new Dictionary<string, object?> { ["program"] = "sh", ["path"] = vault.ProgramPath });
        Assert.AreEqual(CredentialProviderState.Unavailable, (await other.GetStatusAsync(CancellationToken.None)).State);
    }

    [TestMethod]
    public async Task The_picker_lists_the_files_of_the_store_without_running_anything()
    {
        using var vault = NewVault("pass");
        var store = Store(vault);
        Directory.CreateDirectory(Path.Combine(store, "web", "old"));
        Directory.CreateDirectory(Path.Combine(store, ".git"));
        File.WriteAllText(Path.Combine(store, "web", "github.gpg"), "pass-password-value-111");
        File.WriteAllText(Path.Combine(store, "web", "readme.txt"), "not an entry");
        File.WriteAllText(Path.Combine(store, "email.gpg"), "encrypted");
        File.WriteAllText(Path.Combine(store, ".gpg-id"), "alice@example.com");
        var provider = Create(vault, "pass", store);

        var top = await provider.BrowseAsync(new CredentialBrowseRequest(null), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { ("web", CredentialBrowseEntryKind.Folder), ("email", CredentialBrowseEntryKind.Field) }, top.Entries.Select(static entry => (entry.Label, entry.Kind)).ToArray());
        Assert.AreEqual("email", top.Entries[1].Reference);
        var web = await provider.BrowseAsync(new CredentialBrowseRequest(top.Entries[0].Id), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "old", "github" }, web.Entries.Select(static entry => entry.Label).ToArray());
        Assert.AreEqual("web/github", web.Entries[1].Reference);
        Assert.AreEqual("web", web.Path);
        ProviderHarness.AssertHoldsNone(web, "pass-password-value-111");
        Assert.IsEmpty(vault.Calls(), "listing decrypts nothing");

        Assert.AreEqual(CredentialFailure.NotFound, (await ProviderHarness.BrowseFailsAsync(provider, "folder:../..")).Failure, "the picker stays in the store");
        Assert.AreEqual(CredentialFailure.NotFound, (await ProviderHarness.BrowseFailsAsync(provider, "folder:none")).Failure);
        var noStore = Create(vault, "pass", Path.Combine(vault.Root, "none"));
        StringAssert.Contains((await ProviderHarness.BrowseFailsAsync(noStore, null)).Message, "not found");
    }

    private static FakeVault NewVault(string program)
    {
        var vault = new FakeVault(program);
        vault.State["pass"] = new JsonObject { ["entries"] = new JsonObject { ["web/github"] = Entry } };
        vault.Save();
        return vault;
    }

    private static string Store(FakeVault vault)
    {
        var store = Path.Combine(vault.Root, "store");
        Directory.CreateDirectory(store);
        return store;
    }

    private static ICredentialProvider Create(FakeVault vault, string program, string? store)
    {
        var settings = new Dictionary<string, object?> { ["path"] = vault.ProgramPath, ["program"] = program };
        if (store is not null) settings["store"] = store;
        return ProviderHarness.Create(PassProvider.Contribution(new VaultCommandRunner()), settings);
    }
}
