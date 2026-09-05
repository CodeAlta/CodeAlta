using System.Reflection;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Tui.Models;
using CodeAlta.Tui.Presentation.Formatting;
using CodeAlta.Tui.Presentation.Styling;
using CodeAlta.Tui.Presentation.Timeline;
using CodeAlta.Tui.Threading;
using XenoAtom.Terminal.UI;
using XenoAtom.Terminal.UI.Controls;
using XenoAtom.Terminal.UI.Geometry;

namespace CodeAlta.Tests;

[TestClass]
public sealed class FileChangePresenterTests
{
    [TestMethod]
    public void ObserveActivity_EmitsSingleRecapGroupWhenRunBecomesIdle()
    {
        var presenter = CreatePresenter();
        var timestamp = DateTimeOffset.UtcNow;
        using var details = JsonDocument.Parse(
            """
            {
              "result": {
                "detailedContent": "diff --git a/src/Foo.cs b/src/Foo.cs\n--- a/src/Foo.cs\n+++ b/src/Foo.cs\n@@ -1 +1,2 @@\n-old\n+new\n+line\ndiff --git a/src/Bar.cs b/src/Bar.cs\n--- a/src/Bar.cs\n+++ b/src/Bar.cs\n@@ -5,2 +5 @@\n-a\n-b\n+c"
              }
            }
            """);

        presenter.ObserveActivity(new AgentActivityEvent(
            ModelProviderIds.Copilot,
            "session-1",
            timestamp,
            null,
            AgentActivityKind.FileChange,
            AgentActivityPhase.Completed,
            "tool-1",
            null,
            "apply_patch",
            null,
            details.RootElement.Clone()));

        presenter.ObserveSessionUpdate(new AgentSessionUpdateEvent(
            ModelProviderIds.Copilot,
            "session-1",
            timestamp.AddSeconds(1),
            null,
            AgentSessionUpdateKind.Idle,
            null));

        var groups = GetGroups(presenter);
        Assert.AreEqual(1, groups.Count);

        var group = groups[0];
        Assert.AreEqual(2, group.Files.Count);
        Assert.AreEqual(3, group.TotalAdditions);
        Assert.AreEqual(3, group.TotalDeletions);

        var foo = group.Files["src/Foo.cs"];
        Assert.AreEqual(2, foo.Additions);
        Assert.AreEqual(1, foo.Deletions);
        Assert.AreEqual(FileChangeOperation.Modified, foo.Operation);
        Assert.AreEqual("[bold]Foo.cs[/]", foo.FileNameText.Text);
        Assert.AreEqual("[dim]src[/]", foo.DirectoryText.Text);
        Assert.AreEqual(
            $"[{UiPalette.GetToolStatusMarkup(ToolCallDisplayStatus.Completed)}]+2[/] [{UiPalette.GetToolStatusMarkup(ToolCallDisplayStatus.Failed)}]-1[/]",
            foo.CountsText.Text);

        var bar = group.Files["src/Bar.cs"];
        Assert.AreEqual(1, bar.Additions);
        Assert.AreEqual(2, bar.Deletions);
    }

    [TestMethod]
    public void ObserveSessionUpdate_UsesWorkspaceFileChangedFallbackWhenDiffIsUnavailable()
    {
        var presenter = CreatePresenter();
        var timestamp = DateTimeOffset.UtcNow;
        using var details = JsonDocument.Parse("""{"path":"src/NewFile.cs","operation":"create"}""");

        presenter.ObserveSessionUpdate(new AgentSessionUpdateEvent(
            ModelProviderIds.Copilot,
            "session-1",
            timestamp,
            null,
            AgentSessionUpdateKind.DiffUpdated,
            "Workspace file changed.",
            details.RootElement.Clone()));
        presenter.ObserveSessionUpdate(new AgentSessionUpdateEvent(
            ModelProviderIds.Copilot,
            "session-1",
            timestamp.AddSeconds(1),
            null,
            AgentSessionUpdateKind.Idle,
            null));

        var groups = GetGroups(presenter);
        Assert.AreEqual(1, groups.Count);
        var entry = groups[0].Files["src/NewFile.cs"];
        Assert.AreEqual(FileChangeOperation.Created, entry.Operation);
        Assert.AreEqual(0, entry.Additions);
        Assert.AreEqual(0, entry.Deletions);
        Assert.IsNull(entry.DiffText);
    }

    [TestMethod]
    public void GetDiffLineMarkup_UsesPlainTextForUnstyledContextLines()
    {
        Assert.AreEqual(" context line", FileChangeSummaryFormatter.GetDiffLineMarkup(" context line"));
        Assert.AreEqual(
            $"[{UiPalette.GetToolStatusMarkup(ToolCallDisplayStatus.Completed)}]+added[/]",
            FileChangeSummaryFormatter.GetDiffLineMarkup("+added"));
        Assert.AreEqual(
            $"[{UiPalette.GetToolStatusMarkup(ToolCallDisplayStatus.Failed)}]-removed[/]",
            FileChangeSummaryFormatter.GetDiffLineMarkup("-removed"));
    }

    private static FileChangePresenter CreatePresenter()
        => new(
            new DocumentFlow(),
            new InlineUiDispatcher(),
            static _ => { },
            static () => (Rectangle?)null);

    private static List<FileChangeGroupState> GetGroups(FileChangePresenter presenter)
    {
        var field = typeof(FileChangePresenter).GetField("_groups", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        var groups = field.GetValue(presenter) as List<FileChangeGroupState>;
        Assert.IsNotNull(groups);
        return groups;
    }

    private sealed class InlineUiDispatcher : IUiDispatcher
    {
        public bool CheckAccess()
            => true;

        public void Post(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
        }

        public Task InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            action();
            return Task.CompletedTask;
        }

        public Task<T> InvokeAsync<T>(Func<T> action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return Task.FromResult(action());
        }
    }
}
