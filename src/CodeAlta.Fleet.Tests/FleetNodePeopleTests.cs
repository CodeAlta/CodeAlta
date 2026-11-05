using CodeAlta.Fleet.Identity;
using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// The people a machine names (<c>doc/fleet/access-control.md</c>, what the node decides): a person's id is unique within one
/// organization only, so a person named before their organization was kept belongs to the one the machine was linked to then,
/// never to another organization the machine joins afterwards.
/// </summary>
[TestClass]
public sealed class FleetNodePeopleTests
{
    [TestMethod]
    public void APersonNamedBeforeOrganizationsWereKept_StaysOfTheOrganizationOfThen_AndIsNotTheSameIdOfAnotherOne()
    {
        using var folder = new TestFolder();
        File.WriteAllText(Path.Combine(folder.Path, "people.json"),
            """{ "people": [ { "personId": "bea", "permissions": ["view", "drive", "start"], "addedAt": "2026-10-01T09:00:00+00:00" } ], "asks": [], "refused": [] }""");
        var sole = "acme";
        var people = new FleetNodePeople(folder.Path, TimeProvider.System) { SoleOrganization = () => sole };
        Assert.AreEqual(FleetNodePeople.Allowed, people.Named("bea", "acme"), "the machine linked to Acme alone: she is Acme's Bea");

        // The machine leaves Acme and joins Globex, which has a person "bea" too: she is not the person the owner named.
        sole = "globex";
        Assert.IsNull(people.Named("bea", "globex"));
        Assert.AreEqual(FleetNodePeople.Allowed, people.Named("bea", "acme"));
        // Also after a restart of the node.
        Assert.IsNull(new FleetNodePeople(folder.Path, TimeProvider.System) { SoleOrganization = () => sole }.Named("bea", "globex"));
        Assert.AreEqual("acme", people.Read().People.Single().OrganizationId, "kept with the organization it was bound to");
    }

    [TestMethod]
    public void APersonNamedBeforeOrganizationsWereKept_GetsNothing_WhileTheirOrganizationCannotBeKeptWithThem()
    {
        using var folder = new TestFolder();
        var path = Path.Combine(folder.Path, "people.json");
        var legacy = """{ "people": [ { "personId": "bea", "permissions": ["view", "drive", "start"], "addedAt": "2026-10-01T09:00:00+00:00" } ], "asks": [], "refused": [] }""";
        File.WriteAllText(path, legacy);
        // The file cannot be written (a full disk, a locked file): the organization the person is bound to is not kept.
        var writes = 0;
        void Failing(string file, byte[] bytes)
        {
            writes++;
            throw new IOException("No space left on device.");
        }

        var people = new FleetNodePeople(folder.Path, TimeProvider.System) { WriteFile = Failing, SoleOrganization = () => "acme" };
        Assert.IsNull(people.Named("bea", "acme"), "a binding that is not kept would be another one after a restart: nothing until it is");
        Assert.IsTrue(writes > 0, "the binding was tried");

        // Restarted while the machine is linked to Globex alone: its "bea" is not the person the owner named.
        var restarted = new FleetNodePeople(folder.Path, TimeProvider.System) { WriteFile = Failing, SoleOrganization = () => "globex" };
        Assert.IsNull(restarted.Named("bea", "globex"));
        Assert.AreEqual(legacy, File.ReadAllText(path));

        // Once it can be written, the person is bound and kept so.
        var kept = new FleetNodePeople(folder.Path, TimeProvider.System) { SoleOrganization = () => "acme" };
        Assert.AreEqual(FleetNodePeople.Allowed, kept.Named("bea", "acme"));
        Assert.AreEqual(FleetNodePeople.Allowed, new FleetNodePeople(folder.Path, TimeProvider.System).Named("bea", "acme"));
    }
}
