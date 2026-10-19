namespace CodeAlta.Catalog.Tests;

[TestClass]
public sealed class ProjectCliProfilesTests
{
    [TestMethod]
    public void AProjectFile_GivesCliProfiles_AndKeepsOnlyWhatIsValid()
    {
        var root = Path.Combine(Path.GetTempPath(), "codealta-project-cli-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(Path.Combine(project, ".alta"));
        try
        {
            var store = new CodeAltaConfigStore(new CatalogOptions { GlobalRoot = Path.Combine(root, "global") });
            Assert.AreEqual(0, store.LoadProjectCliProfiles(project).Count, "A project without file gives none.");
            Assert.AreEqual(0, store.LoadProjectCliProfiles(null).Count);

            File.WriteAllText(Path.Combine(project, ".alta", "config.toml"), """
                [identity.cli.kubectl.env]
                KUBECONFIG = "~/.kube/contoso-prod-readonly"

                [identity.cli.aws]
                check = "aws sts get-caller-identity"

                [identity.cli.aws.env]
                AWS_PROFILE = "contoso-prod"
                AWS_SESSION_TOKEN = "the-token-itself"

                [identity.cli."not a name".env]
                A = "b"
                """);
            var profiles = store.LoadProjectCliProfiles(project);

            CollectionAssert.AreEqual(new[] { "kubectl", "aws" }, profiles.Select(static profile => profile.Name).ToArray());
            CollectionAssert.AreEqual(new KeyValuePair<string, string>[] { new("KUBECONFIG", "~/.kube/contoso-prod-readonly") }, profiles[0].Environment.ToArray());
            CollectionAssert.AreEqual(new KeyValuePair<string, string>[] { new("AWS_PROFILE", "contoso-prod") }, profiles[1].Environment.ToArray(), "A secret is never kept.");
            Assert.AreEqual("aws sts get-caller-identity", profiles[1].Check);

            // A value of another type is left out, and the rest of the file is still read.
            File.WriteAllText(Path.Combine(project, ".alta", "config.toml"), """
                [search]
                enabled = false

                [identity.cli.kubectl]
                check = 3

                [identity.cli.kubectl.env]
                KUBECONFIG = "~/.kube/contoso"
                VERBOSITY = 3

                [identity.cli.docker]
                env = "not a table"
                """);
            var typed = store.LoadProjectCliProfiles(project);
            Assert.AreEqual(("kubectl", null), (typed.Single().Name, typed.Single().Check));
            CollectionAssert.AreEqual(new KeyValuePair<string, string>[] { new("KUBECONFIG", "~/.kube/contoso") }, typed.Single().Environment.ToArray());
            Assert.IsTrue(store.IsProjectExcludedFromSearch(project), "A wrong value of the identity does not make the file unreadable.");
            File.WriteAllText(Path.Combine(project, ".alta", "config.toml"), "[search]\nenabled = false\n\n[identity]\ncli = 3\n");
            Assert.AreEqual(0, store.LoadProjectCliProfiles(project).Count);
            Assert.IsTrue(store.IsProjectExcludedFromSearch(project));

            // A file that cannot be read gives none, and the sessions keep the profiles of their space.
            File.WriteAllText(Path.Combine(project, ".alta", "config.toml"), "[identity.cli.kubectl\n");
            Assert.AreEqual(0, store.LoadProjectCliProfiles(project).Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

