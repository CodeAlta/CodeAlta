using CodeAlta.Catalog;
using CodeAlta.Orchestration.Runtime.SystemPrompts;

namespace CodeAlta.Orchestration.Tests;

/// <summary>
/// What is of one session alone (where it works, its parent, its scratchpad) is the Session Context, apart from the
/// instructions every session of the prompt shares, so that a provider serves those from its prompt cache.
/// </summary>
[TestClass]
public sealed class SessionContextPromptTests
{
    [TestMethod]
    public void TwoSessionsOfAProjectInTwoWorktrees_ShareTheirInstructions_AndDifferInTheirSessionContextAlone()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "codealta-session-context-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
            File.WriteAllText(Path.Combine(project, "AGENTS.md"), "Project rules.");
            var builder = new SystemPromptBuilder();
            SystemPromptBundle Build(string sessionId, string worktreeName, string? parent)
            {
                var worktree = Directory.CreateDirectory(Path.Combine(root, "worktrees", worktreeName)).FullName;
                File.WriteAllText(Path.Combine(worktree, "AGENTS.md"), "Project rules.");
                return builder.Build(new SystemPromptBuildRequest
                {
                    ProviderKey = "fake",
                    Session = new SessionViewDescriptor
                    {
                        SessionId = sessionId, ProviderId = "fake", ProviderKey = "fake", Kind = SessionViewKind.ProjectSession,
                        WorkingDirectory = project, WorktreeDirectory = worktree, ParentSessionId = parent,
                    },
                    Project = new ProjectDescriptor { Id = "p", ProjectPath = project },
                    ProjectRoots = [project],
                    UserCodeAltaRoot = Path.Combine(root, "global"),
                    ScratchpadRoot = Path.Combine(root, "scratchpad-" + worktreeName),
                });
            }

            var first = Build("session-1", "first", parent: null);
            var second = Build("session-2", "second", parent: "parent-session");

            Assert.AreEqual(first.SystemMessage, second.SystemMessage);
            Assert.AreEqual(first.DeveloperInstructions, second.DeveloperInstructions, "The text before the Session Context is the same.");
            StringAssert.Contains(first.DeveloperInstructions!, "File: `AGENTS.md`", "A file of the project is named from its root.");
            Assert.DoesNotContain(root, first.DeveloperInstructions!, "No folder of a session is in the shared text.");

            StringAssert.StartsWith(first.SessionInstructions!, "# Session Context");
            StringAssert.Contains(first.SessionInstructions!, $"- Current working directory: `{Path.Combine(root, "worktrees", "first")}`");
            StringAssert.Contains(first.SessionInstructions!, $"The main checkout of the project is `{project}`");
            StringAssert.Contains(first.SessionInstructions!, "- Scratchpad file: ");
            Assert.DoesNotContain("Parent session", first.SessionInstructions!);
            StringAssert.Contains(second.SessionInstructions!, "- Parent session: parent-session");
            StringAssert.Contains(second.SessionInstructions!, Path.Combine(root, "scratchpad-second"));
            Assert.IsTrue(first.Manifest.Parts.Any(static part => part.Key == "session.context" && part.Target == "session"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }
}
