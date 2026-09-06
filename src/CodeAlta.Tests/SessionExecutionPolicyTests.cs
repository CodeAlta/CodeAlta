using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

// Pure assembly tests: owned absolute paths and descriptors only; no runtime or discovery.
[TestClass]
public sealed class SessionExecutionPolicyTests
{
    [TestMethod]
    public void CaptureSession_ValidatesResolvedProjectAssociation()
    {
        using var temp = TestTempDirectory.Create();
        var session = new SessionViewDescriptor { Kind = SessionViewKind.ProjectSession, ProjectRef = "a", WorkingDirectory = temp.Path };
        var unrelated = new ProjectDescriptor { Id = "b", ProjectPath = temp.Path };

        Assert.ThrowsExactly<ArgumentException>(() => SessionExecutionPolicy.CaptureSession(
            session, unrelated, temp.Path, default, null, null, null));
    }

    [TestMethod]
    public void CaptureSession_MissingProjectUsesStoredDirectoryAndProviderFallback()
    {
        using var temp = TestTempDirectory.Create();
        var session = new SessionViewDescriptor
        {
            Kind = SessionViewKind.ProjectSession, SessionId = "session-a", ProjectRef = "missing",
            WorkingDirectory = Path.Combine(temp.Path, "stored"), ProviderKey = "unknown-provider", AgentPromptId = " stored-prompt ",
        };

        var request = SessionExecutionPolicy.CaptureSession(session, null, temp.Path, default, "model", AgentReasoningEffort.High, null);

        Assert.AreEqual(session.WorkingDirectory, request.WorkingDirectory);
        Assert.AreEqual("missing", request.ProjectId);
        Assert.AreEqual("unknown-provider", request.ProviderId.Value);
        Assert.AreEqual("stored-prompt", request.AgentPromptId);
        Assert.AreEqual(0, request.ProjectRoots.Count);
    }

    [TestMethod]
    public void CaptureSession_GlobalIgnoresStaleProjectReference()
    {
        using var temp = TestTempDirectory.Create();
        var session = new SessionViewDescriptor { Kind = SessionViewKind.GlobalSession, ProjectRef = "a", WorkingDirectory = Path.Combine(temp.Path, "stale") };
        var project = new ProjectDescriptor { Id = "a", ProjectPath = Path.Combine(temp.Path, "project") };

        var request = SessionExecutionPolicy.CaptureSession(session, project, temp.Path, ModelProviderIds.Codex, null, null, "   ");

        Assert.AreEqual(temp.Path, request.WorkingDirectory);
        Assert.IsNull(request.ProjectId);
        Assert.IsNull(request.AgentPromptId);
        Assert.AreEqual(0, request.ProjectRoots.Count);
    }

    [TestMethod]
    public void CapturePreferred_CopiesRootsAndProjectAndBuildsNeutralOptions()
    {
        using var temp = TestTempDirectory.Create();
        var project = new ProjectDescriptor { Id = "a", ProjectPath = Path.Combine(temp.Path, "project") };
        var roots = new List<string> { project.ProjectPath };
        var request = SessionExecutionPolicy.CapturePreferred(ModelProviderIds.Codex, project.ProjectPath, roots, project,
            "model", AgentReasoningEffort.High, " prompt ");
        roots.Clear();
        project.Id = "b";
        project.ProjectPath = temp.Path;
        AgentPermissionRequestHandler permission = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));
        AgentUserInputRequestHandler input = static (_, _) => Task.FromResult(new AgentUserInputResponse(new Dictionary<string, string>()));

        var first = SessionExecutionPolicy.BuildOptions(request, null, permission, input);
        var second = SessionExecutionPolicy.BuildOptions(request, [], permission, input);

        Assert.AreEqual("a", request.ProjectId);
        Assert.AreEqual(Path.Combine(temp.Path, "project"), first.ProjectRoots.Single());
        Assert.AreEqual(first.WorkingDirectory, second.WorkingDirectory);
        Assert.AreEqual(first.ProviderId, second.ProviderId);
        Assert.AreEqual("model", first.Model);
        Assert.AreEqual(AgentReasoningEffort.High, first.ReasoningEffort);
        Assert.AreEqual("prompt", first.AgentPromptId);
        Assert.AreSame(permission, first.OnPermissionRequest);
        Assert.AreSame(input, first.OnUserInputRequest);
        Assert.IsTrue(first.ProjectRoots is not string[], "Do not expose the mutable copied array.");
    }

    [TestMethod]
    public void CapturePreferred_RequiresExplicitProjectForRoots()
    {
        using var temp = TestTempDirectory.Create();
        Assert.ThrowsExactly<ArgumentException>(() => SessionExecutionPolicy.CapturePreferred(
            ModelProviderIds.Codex, temp.Path, [temp.Path], null, null, null, null));
    }

    [TestMethod]
    public void CaptureSession_PreservesStoredProviderKeyAlongsideNormalizedProviderId()
    {
        using var temp = TestTempDirectory.Create();
        var session = new SessionViewDescriptor
        {
            Kind = SessionViewKind.GlobalSession, WorkingDirectory = temp.Path, ProviderKey = " stored-provider ",
        };
        var request = SessionExecutionPolicy.CaptureSession(session, null, temp.Path, default, null, null, null);
        var options = SessionExecutionPolicy.BuildOptions(request, null,
            static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)), null);

        Assert.AreEqual("stored-provider", options.ProviderId.Value);
        Assert.AreEqual(session.ProviderKey, options.ProviderKey);
    }

    [TestMethod]
    [DataRow(null, "stored-prompt")]
    [DataRow("", null)]
    [DataRow("  ", null)]
    [DataRow(" override ", "override")]
    public void CaptureSession_PreservesPromptOverrideVersusFallback(string? promptOverride, string? expected)
    {
        using var temp = TestTempDirectory.Create();
        var session = new SessionViewDescriptor
        {
            Kind = SessionViewKind.GlobalSession, WorkingDirectory = temp.Path, AgentPromptId = " stored-prompt ",
        };

        var request = SessionExecutionPolicy.CaptureSession(session, null, temp.Path, ModelProviderIds.Codex, null, null, promptOverride);

        Assert.AreEqual(expected, request.AgentPromptId);
    }
}
