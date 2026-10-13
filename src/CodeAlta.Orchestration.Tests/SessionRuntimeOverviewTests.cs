namespace CodeAlta.Orchestration.Tests;

public sealed partial class SessionRuntimeForwardingLifetimeTests
{
    [TestMethod]
    public Task Overview_NamesTheSessionThatCreatedOne() => Fixture.Run(async f =>
    {
        var parent = f.NewSession();
        await f.Persist(parent);
        f.Session.ParentSessionId = parent.SessionId;
        await f.Persist(f.Session);
        await f.Wait(f.Runtime.EnsureCoordinatorSessionAsync(f.Session, f.Options));

        var overview = f.Runtime.ListOverview().Single(overview => overview.SessionId == f.Session.SessionId);
        Assert.AreEqual(parent.SessionId, overview.ParentSessionId);
    });
}
