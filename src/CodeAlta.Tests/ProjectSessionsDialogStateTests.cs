using CodeAlta.Tui.App;
using CodeAlta.Tui.ViewModels;

namespace CodeAlta.Tests;

[TestClass]
public sealed class ProjectSessionsDialogStateTests
{
    [TestMethod]
    public void SelectionCommands_UpdateSelectedRows()
    {
        var state = new ProjectSessionsDialogState(
        [
            CreateRow("session-1"),
            CreateRow("session-2"),
            CreateRow("session-3"),
        ]);

        state.SelectAll();
        Assert.AreEqual(3, state.SelectedCount);

        state.InvertSelection();
        Assert.AreEqual(0, state.SelectedCount);

        state.SelectAll();
        state.SelectNone();
        Assert.AreEqual(0, state.SelectedCount);
    }

    [TestMethod]
    public void RemoveSessions_RemovesMatchingRows()
    {
        var state = new ProjectSessionsDialogState(
        [
            CreateRow("session-1"),
            CreateRow("session-2"),
            CreateRow("session-3"),
        ]);

        var removed = state.RemoveSessions(["session-1", "session-3"]);

        Assert.AreEqual(2, removed);
        CollectionAssert.AreEqual(new[] { "session-2" }, state.Rows.Select(static row => row.SessionId).ToArray());
    }

    private static ProjectSessionsDialogRowViewModel CreateRow(string sessionId)
    {
        return new ProjectSessionsDialogRowViewModel
        {
            SessionId = sessionId,
            Title = sessionId,
        };
    }
}
