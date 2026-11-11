using System.Text.Json.Nodes;
using CodeAlta.Credentials.Providers;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Tests;

[TestClass]
public sealed class KeePassXcProviderTests
{
    private const string DatabasePassword = "kdbx-password-0001";
    private static readonly string[] Values = ["kp-password-value-111", "kp-user-value", "kp-note-value-222", "https://kp.example.com", DatabasePassword];

    [TestMethod]
    public async Task The_password_of_the_database_is_checked_kept_in_memory_and_forgotten_by_a_lock()
    {
        using var vault = NewVault();
        var provider = Create(vault);
        var locked = await provider.GetStatusAsync(CancellationToken.None);
        Assert.AreEqual(CredentialProviderState.Locked, locked.State);
        Assert.AreEqual("Passwords.kdbx", locked.Account);
        Assert.IsEmpty(vault.Calls(), "the status runs nothing");
        Assert.AreEqual(CredentialFailure.Locked, (await ProviderHarness.ReadFailsAsync(provider, "Work/GitHub", interactive: false)).Failure);
        Assert.IsEmpty(vault.Calls(), "without the password nothing is run");

        var wrong = await provider.UnlockAsync(new CredentialUnlockContext { Password = "not-it" }, CancellationToken.None);
        Assert.AreEqual(CredentialProviderState.Locked, wrong.State);
        StringAssert.Contains(wrong.Message, "Invalid credentials");

        Assert.AreEqual(CredentialProviderState.Ready, (await provider.UnlockAsync(new CredentialUnlockContext { Password = DatabasePassword }, CancellationToken.None)).State);
        Assert.AreEqual(CredentialProviderState.Ready, (await provider.GetStatusAsync(CancellationToken.None)).State);
        Assert.AreEqual("kp-password-value-111", (await provider.ReadAsync("Work/GitHub", false, CancellationToken.None)).Value);
        StringAssert.StartsWith(vault.Calls()[^1], "show -q -s -a Password ");

        var calls = vault.Calls().Count;
        provider.Lock();
        Assert.AreEqual(CredentialProviderState.Locked, (await provider.GetStatusAsync(CancellationToken.None)).State);
        Assert.AreEqual(CredentialFailure.Locked, (await ProviderHarness.ReadFailsAsync(provider, "Work/GitHub")).Failure);
        Assert.AreEqual(calls, vault.Calls().Count);
        ProviderHarness.AssertNoArgumentHolds(vault, DatabasePassword, "not-it");
    }

    [TestMethod]
    public async Task Each_attribute_and_the_one_time_password_are_read_and_a_missing_one_is_not_found()
    {
        using var vault = NewVault();
        var provider = await UnlockedAsync(vault);
        Assert.AreEqual("kp-user-value", (await provider.ReadAsync("Work/GitHub#UserName", false, CancellationToken.None)).Value);
        Assert.AreEqual("https://kp.example.com", (await provider.ReadAsync("Work/GitHub#URL", false, CancellationToken.None)).Value);
        Assert.AreEqual("kp-note-value-222", (await provider.ReadAsync("Work/GitHub#Notes", false, CancellationToken.None)).Value);
        Assert.AreEqual("kp-root-value", (await provider.ReadAsync("Root entry#Password", false, CancellationToken.None)).Value);
        var totp = await provider.ReadAsync("Work/GitHub#totp", false, CancellationToken.None);
        Assert.AreEqual("123456", totp.Value);
        Assert.IsNotNull(totp.ExpiresAt);
        StringAssert.StartsWith(vault.Calls()[^1], "show -q -t ");

        Assert.AreEqual(CredentialFailure.NotFound, (await ProviderHarness.ReadFailsAsync(provider, "Work/Nothing#Password")).Failure);
        Assert.AreEqual(CredentialFailure.NotFound, (await ProviderHarness.ReadFailsAsync(provider, "Work/GitHub#Nothing")).Failure);
        Assert.AreEqual(CredentialFailure.NotFound, (await ProviderHarness.ReadFailsAsync(provider, "Root entry#totp")).Failure);

        // The password of the database changed: the one held is forgotten.
        vault.State["keepassxc"]!["password"] = "changed";
        vault.Save();
        Assert.AreEqual(CredentialFailure.Locked, (await ProviderHarness.ReadFailsAsync(provider, "Work/GitHub")).Failure);
        Assert.AreEqual(CredentialProviderState.Locked, (await provider.GetStatusAsync(CancellationToken.None)).State);
        ProviderHarness.AssertNoArgumentHolds(vault, DatabasePassword);
    }

    [TestMethod]
    public async Task A_database_opened_by_its_key_file_alone_needs_nobody()
    {
        using var vault = NewVault();
        vault.State["keepassxc"]!["noPassword"] = true;
        vault.State["keepassxc"]!["keyFile"] = "Passwords.keyx";
        vault.Save();
        var keyFile = Path.Combine(vault.Root, "Passwords.keyx");
        File.WriteAllText(keyFile, "key");
        var provider = ProviderHarness.Create(KeePassXcProvider.Contribution(new VaultCommandRunner()), new Dictionary<string, object?>
        {
            ["path"] = vault.ProgramPath, ["database"] = Database(vault), ["key_file"] = keyFile, ["no_password"] = true,
        });
        Assert.AreEqual(CredentialProviderState.Ready, (await provider.GetStatusAsync(CancellationToken.None)).State);
        Assert.AreEqual("kp-password-value-111", (await provider.ReadAsync("Work/GitHub", false, CancellationToken.None)).Value);
        StringAssert.Contains(vault.Calls()[^1], $"-k {keyFile} --no-password {Database(vault)} Work/GitHub");
    }

    [TestMethod]
    public async Task A_missing_database_or_program_says_so()
    {
        using var vault = NewVault();
        var gone = ProviderHarness.Create(KeePassXcProvider.Contribution(new VaultCommandRunner()), new Dictionary<string, object?> { ["path"] = vault.ProgramPath, ["database"] = Path.Combine(vault.Root, "none.kdbx") });
        var status = await gone.GetStatusAsync(CancellationToken.None);
        Assert.AreEqual(CredentialProviderState.Unavailable, status.State);
        StringAssert.Contains(status.Message, "none.kdbx");

        var noProgram = ProviderHarness.Create(KeePassXcProvider.Contribution(new VaultCommandRunner()), new Dictionary<string, object?> { ["path"] = Path.Combine(vault.Bin, "none.exe"), ["database"] = Database(vault) });
        Assert.AreEqual(CredentialProviderState.NotInstalled, (await noProgram.GetStatusAsync(CancellationToken.None)).State);
    }

    [TestMethod]
    public async Task The_picker_lists_groups_entries_and_their_fields_never_their_values()
    {
        using var vault = NewVault();
        var provider = await UnlockedAsync(vault);
        var top = await provider.BrowseAsync(new CredentialBrowseRequest(null), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "Old", "Work", "Root entry" }, top.Entries.Select(static entry => entry.Label).ToArray());
        Assert.IsTrue(top.Entries.All(static entry => entry.Kind == CredentialBrowseEntryKind.Folder));
        StringAssert.Contains(vault.Calls()[^1], "ls -R -f -q ");

        var work = await provider.BrowseAsync(new CredentialBrowseRequest(top.Entries[1].Id), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "Deep", "GitHub" }, work.Entries.Select(static entry => entry.Label).ToArray());
        Assert.AreEqual("Work", work.Path);
        var deep = await provider.BrowseAsync(new CredentialBrowseRequest(work.Entries[0].Id), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "Mail" }, deep.Entries.Select(static entry => entry.Label).ToArray());
        Assert.IsEmpty((await provider.BrowseAsync(new CredentialBrowseRequest(top.Entries[0].Id), CancellationToken.None)).Entries, "an empty group is empty");

        var fields = await provider.BrowseAsync(new CredentialBrowseRequest(work.Entries[1].Id), CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "Work/GitHub#Password", "Work/GitHub#UserName", "Work/GitHub#URL", "Work/GitHub#Notes", "Work/GitHub#totp" },
            fields.Entries.Select(static entry => entry.Reference).ToArray());
        Assert.AreEqual("Work › GitHub", fields.Path);
        foreach (var page in new[] { top, work, deep, fields }) ProviderHarness.AssertHoldsNone(page, Values);
        Assert.AreEqual(CredentialFailure.NotFound, (await ProviderHarness.BrowseFailsAsync(provider, "elsewhere")).Failure);
        ProviderHarness.AssertNoArgumentHolds(vault, DatabasePassword);
    }

    private static FakeVault NewVault()
    {
        var vault = new FakeVault("keepassxc-cli");
        File.WriteAllText(Database(vault), "not a real database");
        static JsonObject Entry(string path, string password, bool totp = false) => new()
        {
            ["path"] = path,
            ["totp"] = totp,
            ["attributes"] = new JsonObject { ["Title"] = path.Split('/')[^1], ["Password"] = password, ["UserName"] = "kp-user-value", ["URL"] = "https://kp.example.com", ["Notes"] = "kp-note-value-222" },
        };
        vault.State["keepassxc"] = new JsonObject
        {
            ["password"] = DatabasePassword,
            ["groups"] = new JsonArray("Work/", "Work/Deep/", "Old/[empty]"),
            ["entries"] = new JsonArray(Entry("Work/GitHub", "kp-password-value-111", totp: true), Entry("Root entry", "kp-root-value"), Entry("Work/Deep/Mail", "kp-password-value-111")),
        };
        vault.Save();
        return vault;
    }

    private static string Database(FakeVault vault) => Path.Combine(vault.Root, "Passwords.kdbx");

    private static ICredentialProvider Create(FakeVault vault)
        => ProviderHarness.Create(KeePassXcProvider.Contribution(new VaultCommandRunner()), new Dictionary<string, object?> { ["path"] = vault.ProgramPath, ["database"] = Database(vault) });

    private static async Task<ICredentialProvider> UnlockedAsync(FakeVault vault)
    {
        var provider = Create(vault);
        Assert.AreEqual(CredentialProviderState.Ready, (await provider.UnlockAsync(new CredentialUnlockContext { Password = DatabasePassword }, CancellationToken.None)).State);
        return provider;
    }
}
