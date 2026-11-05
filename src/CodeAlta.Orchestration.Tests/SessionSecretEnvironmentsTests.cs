using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// The admissions of sessions to environments of the fleet, kept across a restart of the host: bound to the environment,
/// the version of its rules and the approval taken, holding only while the host says that version is still the one it has.
/// </summary>
[TestClass]
public sealed class SessionSecretEnvironmentsTests
{
    [TestMethod]
    public void AnAdmission_IsKeptAcrossARestart_AndHoldsOnlyWhileItsVersionDoes()
    {
        var folder = Directory.CreateTempSubdirectory("alta-admissions-").FullName;
        var path = Path.Combine(folder, "fleet", "environment-admissions.json");
        try
        {
            var first = new SessionSecretEnvironments(path);
            first.Admit("s1", new EnvironmentAdmission("production", "env-1", 3, "approval-1"));
            first.Admit("s2", "staging");
            Assert.AreEqual("production", first.Get("s1"));

            // Another instance of the host on the same profile, as after a restart.
            var restarted = new SessionSecretEnvironments(path);
            Assert.AreEqual(new EnvironmentAdmission("production", "env-1", 3, "approval-1"), restarted.Find("s1")?.Admission);
            Assert.AreEqual("staging", restarted.Get("s2"));

            // The rules the host holds now decide: another version, or another environment of that name, admits nothing.
            long current = 3;
            restarted.Validate = admission => admission.Admission.EnvironmentId == "env-1" && admission.Admission.Version == current;
            Assert.AreEqual("production", restarted.Get("s1"));
            current = 4;
            Assert.IsNull(restarted.Get("s1"));
            Assert.IsNull(restarted.Get("s2"), "an admission by name alone holds for a host that keeps no rules only");

            Assert.IsTrue(restarted.Remove("s1"));
            Assert.IsNull(new SessionSecretEnvironments(path).Find("s1"), "a removal is kept too");
            Assert.IsFalse(new SessionSecretEnvironments(path).Remove("unknown"));

            // A file that does not read admits nothing; in memory, nothing outlives the instance.
            File.WriteAllText(path, "{ broken");
            Assert.IsNull(new SessionSecretEnvironments(path).Find("s2"));
            var memory = new SessionSecretEnvironments();
            memory.Admit("s3", "production");
            Assert.AreEqual("production", memory.Get("s3"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
