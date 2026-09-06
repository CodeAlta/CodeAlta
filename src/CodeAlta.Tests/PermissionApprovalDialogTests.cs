using CodeAlta.Agent;
using CodeAlta.Orchestration.Runtime;
using CodeAlta.Tui.Views;
using XenoAtom.Terminal;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Styling;

namespace CodeAlta.Tests;

[TestClass]
public sealed class PermissionApprovalDialogTests
{
    [TestMethod]
    [DataRow(AgentPermissionDecisionKind.AllowOnce)]
    [DataRow(AgentPermissionDecisionKind.AllowForSession)]
    [DataRow(AgentPermissionDecisionKind.Deny)]
    [DataRow(AgentPermissionDecisionKind.Cancel)]
    public void ButtonsAndEscape_ResolveScopedOwnerAndClose(AgentPermissionDecisionKind decision)
    {
        using var terminal = new PermissionTerminalFixture();
        var service = new SessionPermissionService();
        try
        {
            var request = SessionPermissionServiceTests.Request();
            var registration = service.RegisterAsync("session", request, false, CancellationToken.None).GetAwaiter().GetResult();
            using var dialog = new PermissionApprovalDialog(request, service, registration.Snapshot.Handle, static () => null, static () => null);
            dialog.Show();
            terminal.Tick();
            Assert.AreEqual(1, terminal.DialogCount);
            if (decision == AgentPermissionDecisionKind.Cancel)
            {
                terminal.Key(TerminalKey.Escape);
            }
            else
            {
                var tone = decision switch
                {
                    AgentPermissionDecisionKind.AllowOnce => ControlTone.Primary,
                    AgentPermissionDecisionKind.AllowForSession => ControlTone.Success,
                    _ => ControlTone.Error,
                };
                terminal.App.Focus(terminal.App.Root.EnumerateVisualsDepthFirst().OfType<Button>().Single(button => button.Tone == tone));
                terminal.Key(TerminalKey.Enter);
            }

            dialog.Resolution.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Assert.AreEqual(decision, registration.Completion.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult().Kind);
            Assert.AreEqual(0, terminal.DialogCount);
            Assert.AreEqual(0, service.ListAsync().AsTask().GetAwaiter().GetResult().Count);
            Assert.IsFalse(service.ResolveAsync(registration.Snapshot.Handle, AgentPermissionDecisionKind.AllowOnce).AsTask().GetAwaiter().GetResult());
        }
        finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    [TestMethod]
    public void OldDialogButton_CannotResolveReusedProviderInteraction()
    {
        using var terminal = new PermissionTerminalFixture();
        var service = new SessionPermissionService();
        try
        {
            var request = SessionPermissionServiceTests.Request();
            var first = service.RegisterAsync("session", request, false, CancellationToken.None).GetAwaiter().GetResult();
            using var oldDialog = new PermissionApprovalDialog(request, service, first.Snapshot.Handle, static () => null, static () => null);
            oldDialog.Show();
            terminal.Tick();
            service.CancelAsync(first.Snapshot.Handle).AsTask().GetAwaiter().GetResult();
            var second = service.RegisterAsync("session", request, false, CancellationToken.None).GetAwaiter().GetResult();
            terminal.App.Focus(terminal.App.Root.EnumerateVisualsDepthFirst().OfType<Button>().Single(button => button.Tone == ControlTone.Primary));
            terminal.Key(TerminalKey.Enter);
            oldDialog.Resolution.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Assert.AreEqual(0, terminal.DialogCount);
            Assert.AreEqual(AgentPermissionDecisionKind.Cancel, first.Completion.GetAwaiter().GetResult().Kind);
            Assert.IsTrue(second.IsPending);
            Assert.IsFalse(second.Completion.IsCompleted);
            service.CancelAsync(second.Snapshot.Handle).AsTask().GetAwaiter().GetResult();
            Assert.AreEqual(AgentPermissionDecisionKind.Cancel, second.Completion.GetAwaiter().GetResult().Kind);
        }
        finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    [TestMethod]
    public void PresentationDisposal_DoesNotDecidePermission()
    {
        using var terminal = new PermissionTerminalFixture();
        var service = new SessionPermissionService();
        try
        {
            var request = SessionPermissionServiceTests.Request();
            var registration = service.RegisterAsync("session", request, false, CancellationToken.None).GetAwaiter().GetResult();
            using var dialog = new PermissionApprovalDialog(request, service, registration.Snapshot.Handle, static () => null, static () => null);
            dialog.Show();
            terminal.Tick();
            dialog.Dispose();
            Assert.AreEqual(0, terminal.DialogCount);
            Assert.IsTrue(registration.IsPending);
            Assert.IsFalse(registration.Completion.IsCompleted);
            Assert.AreEqual(1, service.ListAsync().AsTask().GetAwaiter().GetResult().Count);
            service.CancelAsync(registration.Snapshot.Handle).AsTask().GetAwaiter().GetResult();
            Assert.AreEqual(AgentPermissionDecisionKind.Cancel, registration.Completion.GetAwaiter().GetResult().Kind);
        }
        finally { service.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
