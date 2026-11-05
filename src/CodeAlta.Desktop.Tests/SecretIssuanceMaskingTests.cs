namespace CodeAlta.Desktop.Tests;

/// <summary>
/// The masking of a secret given to the work of a session (<c>doc/fleet/secrets.md</c>, masking): a value is masked from the moment
/// it is given, whatever the store keeps of it later, also when nothing was masked between the moment it was given and the moment a
/// process that took it prints it.
/// </summary>
[TestClass]
public sealed class SecretIssuanceMaskingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AValueGivenToASession_StaysMasked_OnceTheStoreKeepsNothingOfIt()
    {
        var token = TestContext.CancellationToken;
        await using var fixture = await FleetNodeFixture.StartAsync(token, null, null, null, appliesSpaceIdentities: true);
        var store = new CodeAlta.Catalog.Secrets.LocalSecretStore(fixture.Host.CatalogOptions.GlobalRoot);
        await fixture.Host.SpaceCatalog.UpdateSettingsAsync(CodeAlta.Catalog.SpaceDescriptor.DefaultId, settings => settings with
        {
            Identity = new CodeAlta.Catalog.SpaceIdentity(Cli: [new CodeAlta.Catalog.SpaceCliProfile("grafana", [new("GRAFANA_TOKEN", "${secret:GRAFANA_TOKEN}")])]),
        }, token);
        const string Given = "glsa_given-to-the-session-0123456789";
        store.SetSecret("GRAFANA_TOKEN", Given, "tests");

        // The session takes the value in the environment of its processes; nothing is masked meanwhile.
        var identity = await fixture.Host.Identities.ResolveAsync(CodeAlta.Catalog.SpaceDescriptor.DefaultId, null, "session-1", token);
        Assert.IsTrue(identity.Environment.Variables.Any(static variable => variable.Value == Given));

        // Rotated many times since, the store keeps nothing of it: not as its value, not among the values it keeps for masking.
        for (var index = 0; index < CodeAlta.Catalog.Secrets.LocalSecretStore.MaximumRetired + 44; index++) store.SetSecret("GRAFANA_TOKEN", $"glsa_rotated-{index:D4}-0123456789", "tests");
        Assert.IsFalse(store.GetSecretValues().Contains(Given));

        // A process of the session prints the value it took: it is masked.
        Assert.AreEqual("token: ***", fixture.Host.AgentHub.SecretMasker!.MaskText("token: " + Given));
    }
}
