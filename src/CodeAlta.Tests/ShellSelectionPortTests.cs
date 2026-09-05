using CodeAlta.Agent;
using CodeAlta.Tui.App;
using CodeAlta.Catalog;
using CodeAlta.Tui.Models;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ShellSelectionPortTests
{
    [TestMethod]
    public void GetSnapshot_CapturesSelectionProjectSessionAndPromptSession()
    {
        var project = new ProjectDescriptor { Id = "project-1", DisplayName = "CodeAlta", ProjectPath = @"C:\repo", Slug = "codealta" };
        var session = new SessionViewDescriptor { SessionId = "session-1", ProjectRef = project.Id, Title = "Session" };
        var promptSession = new PromptSessionBinding(
            new PromptSessionId("prompt-1"),
            ProjectId.NewVersion7(),
            new ShellSessionRef.Running(session.SessionId),
            new ModelProviderId("provider-1"));
        var selection = ShellSelection.Session(session.SessionId, project.Id);
        var port = new DelegatingShellSelectionPort(
            () => selection,
            () => project,
            () => session,
            () => promptSession,
            (_, _) => Task.CompletedTask,
            id => id == session.SessionId);

        var snapshot = port.GetSnapshot();

        Assert.AreEqual(selection, snapshot.Selection);
        Assert.AreSame(project, snapshot.SelectedProject);
        Assert.AreSame(session, snapshot.SelectedSession);
        Assert.AreSame(promptSession, snapshot.PromptSession);
        Assert.IsTrue(port.IsSelectedSession(session.SessionId));
    }

    [TestMethod]
    public async Task SelectAsync_ForwardsSelectionAndCancellationToken()
    {
        var capturedSelection = (ShellSelection?)null;
        var capturedToken = CancellationToken.None;
        using var cancellation = new CancellationTokenSource();
        var port = new DelegatingShellSelectionPort(
            () => ShellSelection.GlobalDraft(),
            () => null,
            () => null,
            () => null,
            (selection, token) =>
            {
                capturedSelection = selection;
                capturedToken = token;
                return Task.CompletedTask;
            },
            _ => false);
        var requested = ShellSelection.ProjectDraft("project-1");

        await port.SelectAsync(requested, cancellation.Token);

        Assert.AreEqual(requested, capturedSelection);
        Assert.AreEqual(cancellation.Token, capturedToken);
    }

    [TestMethod]
    public void IsSelectedSession_RejectsBlankSessionIdBeforeForwarding()
    {
        var invoked = false;
        var port = new DelegatingShellSelectionPort(
            () => ShellSelection.GlobalDraft(),
            () => null,
            () => null,
            () => null,
            (_, _) => Task.CompletedTask,
            _ =>
            {
                invoked = true;
                return true;
            });

        Assert.ThrowsExactly<ArgumentException>(() => port.IsSelectedSession("   "));
        Assert.IsFalse(invoked);
    }
}
