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
}
