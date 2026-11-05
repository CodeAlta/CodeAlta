using CodeAlta.Fleet.NodeApi;

namespace CodeAlta.Fleet.Tests;

/// <summary>
/// Whose work each session of a shared machine is (<c>doc/fleet/security-and-privacy.md</c>, shared machines): a session another
/// person started, or a run nobody attends that is not the owner's, resolves none of the owner's values. What is not recorded, or
/// cannot be, never becomes the owner's work.
/// </summary>
[TestClass]
public sealed class FleetSessionPrincipalsTests
{
    [TestMethod]
    public void TheWorkOfAPerson_IsNeverForgottenForTheSessionsRecordedAfterIt()
    {
        using var folder = new TestFolder();
        var principals = new FleetSessionPrincipals(folder.Path);
        principals.Record("max-session", "max", "org");
        for (var index = 0; index < FleetSessionPrincipals.MaxSessions + 10; index++) principals.Record($"session-{index}", "mia", "org");

        Assert.IsTrue(principals.IsAttributed("max-session"), "a session of a person stays theirs, however many come after it");
        Assert.IsTrue(new FleetSessionPrincipals(folder.Path).IsAttributed("max-session"), "also after a restart");
        Assert.AreEqual("max", new FleetSessionPrincipals(folder.Path).PersonOf("max-session"));
    }

    [TestMethod]
    public void ASubSessionWhoseWorkCannotBeRecorded_DoesNotStart()
    {
        using var folder = new TestFolder();
        var principals = new FleetSessionPrincipals(folder.Path);
        principals.Record("max-session", "max", "org");
        // The file of the records cannot be written: its name is taken by a folder.
        File.Delete(Path.Combine(folder.Path, "session-principals.json"));
        Directory.CreateDirectory(Path.Combine(folder.Path, "session-principals.json"));

        Assert.ThrowsExactly<IOException>(() => principals.Inherit("max-child", "max-session"), "the session is refused rather than left unrecorded");
    }

    [TestMethod]
    public void ARecordThatDoesNotRead_MakesNoSessionTheOwnersWork()
    {
        using var folder = new TestFolder();
        File.WriteAllText(Path.Combine(folder.Path, "session-principals.json"), "[ damaged");
        var principals = new FleetSessionPrincipals(folder.Path);

        Assert.IsTrue(principals.IsAttributed("max-session"), "a session whose record may be in the damaged file is not the owner's work");
        Assert.ThrowsExactly<IOException>(() => principals.Record("new-session", "mia", "org"), "nothing is written over what does not read");
    }
}
