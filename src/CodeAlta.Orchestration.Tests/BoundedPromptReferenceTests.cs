using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime.Prompts;

namespace CodeAlta.Orchestration.Tests;

[TestClass]
public sealed class BoundedPromptReferenceTests
{
    [TestMethod]
    public void ObservationUsesDispatchParserAndPolicyWithoutChangingRecency()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta-reference-observation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "file.cs"), "fixture");
            var service = new BoundedPromptReferences();
            foreach (var text in new[] { "@file.cs:2-3", "[label](file.cs)", "@\"file.cs\"" })
            {
                var observation = service.Observe(text, root, CancellationToken.None);
                Assert.AreEqual("resolved", observation.Items.Single().Status);
                Assert.AreEqual(text, text.Substring(observation.Items[0].Start, observation.Items[0].Length));
                Assert.IsFalse(service.Search(root, "file", CancellationToken.None).Items.Single().Recent);
                Assert.AreEqual(2, service.Resolve(text, root, CancellationToken.None).Items.Count);
                service = new BoundedPromptReferences();
            }
            foreach (var text in new[] { "@file.cs:0", "@file.cs:3-2", "@file.cs:1-", "@file.cs:1-2junk", "@missing", "@../file.cs", "@/file.cs" })
                Assert.AreEqual("unresolved", service.Observe(text, root, CancellationToken.None).Items.Single().Status);
            Assert.AreEqual("escaped", service.Observe("@@literal", root, CancellationToken.None).Items.Single().Status);
            var bounded = service.Observe(string.Join(' ', Enumerable.Repeat("@file.cs", 300)), root, CancellationToken.None);
            Assert.AreEqual(256, bounded.Items.Count);
            Assert.IsTrue(bounded.Omitted);
            Assert.AreEqual(32, bounded.Items.Count(item => item.Status == "resolved"));
            Assert.ThrowsExactly<OperationCanceledException>(() => service.Observe("@file.cs", root, new CancellationToken(true)));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LinkedDirectoryCannotEscapeDisposableProjectRoot()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "alta-reference-link-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(fixture, "project");
        var outside = Path.Combine(fixture, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(root, "linked");
        try
        {
            File.WriteAllText(Path.Combine(outside, "outside-secret.txt"), "disposable sentinel");
            Directory.CreateSymbolicLink(link, outside);
            var service = new BoundedPromptReferences();
            var result = service.Search(root, "outside-secret", CancellationToken.None);
            Assert.AreEqual(0, result.Items.Count);
            Assert.IsTrue(result.Omitted);
            Assert.AreEqual(1, service.Resolve("@linked/outside-secret.txt", root, CancellationToken.None).Items.Count);
            Assert.AreEqual("unavailable", service.Search(link, "", CancellationToken.None).Status);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(fixture, true);
        }
    }

    [TestMethod]
    public void ResolveUsesTypedReferencesAndRetainsUnsafeOrMissingLiterals()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta-references-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            File.WriteAllText(Path.Combine(root, "src", "app.cs"), "one\ntwo\nthree");
            var service = new BoundedPromptReferences();
            var input = service.Resolve("See @src/app.cs:2-3 @src/ @@literal @missing @../escape", root, CancellationToken.None);
            Assert.IsInstanceOfType<AgentInputItem.File>(input.Items[1]);
            Assert.AreEqual(new AgentLineRange(2, 3), ((AgentInputItem.File)input.Items[1]).LineRange);
            Assert.IsInstanceOfType<AgentInputItem.Directory>(input.Items[2]);
            var text = ((AgentInputItem.Text)input.Items[0]).Value;
            StringAssert.Contains(text, "@literal @missing @../escape");
            var escaped = service.Resolve("[<b>](src/app.cs)", root, CancellationToken.None);
            StringAssert.Contains(((AgentInputItem.Text)escaped.Items[0]).Value, "[&lt;b&gt;](src/app.cs)");
            Assert.AreEqual(1, service.Resolve("@/src/app.cs @C:/src/app.cs @src/app.cs:1-9999999", root, CancellationToken.None).Items.Count);
            foreach (var invalid in new[] { "@src/app.cs:0", "@src/app.cs:3-2", "@src/app.cs:1-", "@src/app.cs:1-2junk" })
            {
                var literal = service.Resolve(invalid, root, CancellationToken.None);
                Assert.AreEqual(1, literal.Items.Count);
                Assert.AreEqual(invalid, ((AgentInputItem.Text)literal.Items[0]).Value);
            }
            var search = service.Search(root, "app", CancellationToken.None);
            Assert.AreEqual("src/app.cs", search.Items.Single().Path);
            Assert.IsTrue(search.Items.Single().Recent);
            for (var index = 0; index < 80; index++) File.WriteAllText(Path.Combine(root, $"bounded-{index}.txt"), "fixture");
            var bounded = service.Search(root, "bounded-", CancellationToken.None);
            Assert.AreEqual(64, bounded.Items.Count);
            Assert.IsTrue(bounded.Omitted);
            Assert.AreEqual("incomplete", bounded.Status);
            Assert.AreEqual(33, service.Resolve(string.Join(' ', Enumerable.Repeat("@src/app.cs", 40)), root, CancellationToken.None).Items.Count);
            Assert.ThrowsExactly<OperationCanceledException>(() => service.Search(root, "", new CancellationToken(true)));
            File.Delete(Path.Combine(root, "src", "app.cs"));
            Assert.AreEqual(1, service.Resolve("@src/app.cs", root, CancellationToken.None).Items.Count);
        }
        finally { Directory.Delete(root, true); }
    }
}
