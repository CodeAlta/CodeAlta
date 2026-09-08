using CodeAlta.Plugin.Mcp;
using CodeAlta.Plugins.Abstractions;
using CodeAlta.Plugins.Tui;

namespace CodeAlta.Tests;

/// <summary>Lifecycle-uninitialized backend enumeration and inert callbacks only; no context, state operations or native construction.</summary>
[TestClass]
public sealed class PluginMcpPresentationTests
{
    [TestMethod]
    public void NeutralBackend_HasNoCommandsAndPortableStatus()
    {
        var plugin = new McpPlugin();
        Assert.AreEqual(0, plugin.GetCommands().Count());
        var status = plugin.GetUiContributions().Single();
        Assert.AreEqual(typeof(PluginContentContribution), status.GetType());
        Assert.AreEqual(PluginUiRegion.SessionStatus, status.Region);
        Assert.AreEqual("mcp-status", status.Name);
        Assert.AreEqual(100, status.Order);
        Assert.IsNotNull(((PluginContentContribution)status).CreateContent);
        Assert.AreNotSame(status, plugin.GetUiContributions().Single());
    }

    [TestMethod]
    public void Construction_ValidatesFactoryAndPreservesInvocation()
    {
        var factories = 0;
        var commands = 0;
        var decorations = 0;
        var plugin = new McpPlugin((management, activation) =>
        {
            factories++;
            Assert.IsNotNull(management);
            Assert.IsNotNull(activation);
            return new McpPluginPresentation(() => { commands++; return []; }, content => { decorations++; return content; });
        });
        Assert.AreEqual(1, factories);
        var commandSequence = plugin.GetCommands();
        var statusSequence = plugin.GetUiContributions();
        Assert.AreEqual(0, commands);
        Assert.AreEqual(0, decorations);
        Assert.AreEqual(0, commandSequence.Count());
        Assert.AreEqual(1, statusSequence.Count());
        Assert.AreEqual(0, plugin.GetCommands().Count());
        Assert.AreEqual(1, plugin.GetUiContributions().Count());
        Assert.AreEqual(1, factories);
        Assert.AreEqual(2, commands);
        Assert.AreEqual(2, decorations);
        Assert.ThrowsExactly<ArgumentNullException>(() => new McpPlugin(null!));
        Assert.ThrowsExactly<InvalidOperationException>(() => new McpPlugin(static (_, _) => null!));
        var failure = new ApplicationException("factory");
        Assert.AreSame(failure, Assert.ThrowsExactly<ApplicationException>(() => new McpPlugin((_, _) => throw failure)));
        var cancellation = new OperationCanceledException("factory");
        Assert.AreSame(cancellation, Assert.ThrowsExactly<OperationCanceledException>(() => new McpPlugin((_, _) => throw cancellation)));
    }

    [TestMethod]
    public void Commands_AreDeferredAndPreserveSequence()
    {
        var first = Command("first");
        var second = Command("second");
        var calls = new List<string>();
        IEnumerable<PluginCommandContribution> Sequence()
        {
            calls.Add("first");
            yield return first;
            calls.Add("second");
            yield return second;
        }
        var plugin = new McpPlugin((_, _) => new McpPluginPresentation(() => { calls.Add("factory"); return Sequence(); }, static content => content));
        var sequence = plugin.GetCommands();
        Assert.AreEqual(0, calls.Count);
        using var iterator = sequence.GetEnumerator();
        Assert.AreEqual(0, calls.Count);
        Assert.IsTrue(iterator.MoveNext());
        Assert.AreSame(first, iterator.Current);
        CollectionAssert.AreEqual(new[] { "factory", "first" }, calls);
        Assert.IsTrue(iterator.MoveNext());
        Assert.AreSame(second, iterator.Current);
        Assert.IsFalse(iterator.MoveNext());
        CollectionAssert.AreEqual(new[] { "factory", "first", "second" }, calls);
    }

    [TestMethod]
    public void Commands_PropagateFactoryAndSequenceFailures()
    {
        var failure = new ApplicationException("commands");
        var plugin = new McpPlugin((_, _) => new McpPluginPresentation(() => throw failure, static content => content));
        var sequence = plugin.GetCommands();
        Assert.AreSame(failure, Assert.ThrowsExactly<ApplicationException>(() => sequence.ToArray()));
        var first = Command("first");
        IEnumerable<PluginCommandContribution> FailingSequence()
        {
            yield return first;
            throw failure;
        }
        var sequenced = new McpPlugin((_, _) => new McpPluginPresentation(FailingSequence, static content => content));
        using var iterator = sequenced.GetCommands().GetEnumerator();
        Assert.IsTrue(iterator.MoveNext());
        Assert.AreSame(first, iterator.Current);
        Assert.AreSame(failure, Assert.ThrowsExactly<ApplicationException>(() => iterator.MoveNext()));
        var cancellation = new OperationCanceledException("commands");
        var canceled = new McpPlugin((_, _) => new McpPluginPresentation(() => throw cancellation, static content => content));
        Assert.AreSame(cancellation, Assert.ThrowsExactly<OperationCanceledException>(() => canceled.GetCommands().ToArray()));
        // A null sequence violates the delegate contract; it is not an empty-command fallback.
        var missing = new McpPlugin(static (_, _) => new McpPluginPresentation(static () => null!, static content => content));
        Assert.ThrowsExactly<NullReferenceException>(() => missing.GetCommands().ToArray());
    }

    [TestMethod]
    public void StatusDecoration_ReceivesExactContentOnceWithoutEvaluatingContent()
    {
        PluginContentContribution? received = null;
        var calls = 0;
        var plugin = new McpPlugin((_, _) => new McpPluginPresentation(static () => [], content =>
        {
            calls++;
            received = content;
            return content;
        }));
        var sequence = plugin.GetUiContributions();
        Assert.AreEqual(0, calls);
        var status = sequence.Single();
        Assert.AreEqual(1, calls);
        Assert.AreSame(received, status);
        Assert.IsNotNull(received!.CreateContent);
        Assert.AreEqual("mcp-status", received.Name);
        // Evaluating this real callback would read workspace/configuration; deliberately do not invoke it.
    }

    [TestMethod]
    public void StatusDecoration_PropagatesNullAndFailuresWithoutFallback()
    {
        // Deliberately violate the non-null delegate result contract to verify no neutral fallback.
        var absent = new McpPlugin(static (_, _) => new McpPluginPresentation(static () => [], static _ => null!));
        Assert.IsNull(absent.GetUiContributions().Single());
        var failure = new ApplicationException("decoration");
        var plugin = new McpPlugin((_, _) => new McpPluginPresentation(static () => [], _ => throw failure));
        var sequence = plugin.GetUiContributions();
        Assert.AreSame(failure, Assert.ThrowsExactly<ApplicationException>(() => sequence.ToArray()));
        var cancellation = new OperationCanceledException("decoration");
        var canceled = new McpPlugin((_, _) => new McpPluginPresentation(static () => [], _ => throw cancellation));
        Assert.AreSame(cancellation, Assert.ThrowsExactly<OperationCanceledException>(() => canceled.GetUiContributions().ToArray()));
    }

    [TestMethod]
    public void TerminalDecoration_PreservesMetadataAndDeferredCallbacks()
    {
        var contentCalls = 0;
        var visualCalls = 0;
        var content = new PluginContentContribution
        {
            Region = PluginUiRegion.SessionStatus,
            Name = "literal-status",
            Order = 37,
            CreateContent = _ => { contentCalls++; throw new AssertFailedException("Content must remain deferred."); },
        };
        var decorated = McpTerminalPresentation.DecorateStatus(content, _ => { visualCalls++; throw new AssertFailedException("Visual must remain deferred."); });
        Assert.IsInstanceOfType<PluginVisualContribution>(decorated);
        Assert.AreEqual(content.Region, decorated.Region);
        Assert.AreEqual(content.Name, decorated.Name);
        Assert.AreEqual(content.Order, decorated.Order);
        Assert.AreSame(content.CreateContent, decorated.CreateContent);
        Assert.IsNotNull(decorated.CreateVisual);
        Assert.IsNull(decorated.Visual);
        Assert.AreEqual(0, contentCalls);
        Assert.AreEqual(0, visualCalls);
    }

    [TestMethod]
    public void Revision_DirectAccessIncrementsWithoutPosting()
    {
        var checks = 0;
        var increments = 0;
        McpTerminalPresentation.IncrementRevision(() => { checks++; return true; }, () => increments++, _ => throw new AssertFailedException("No post expected."));
        Assert.AreEqual(1, checks);
        Assert.AreEqual(1, increments);
    }

    [TestMethod]
    public void Revision_NoAccessPostsWithoutEagerIncrement()
    {
        var checks = 0;
        var increments = 0;
        var queue = new Queue<Action>();
        McpTerminalPresentation.IncrementRevision(() => { checks++; return false; }, () => increments++, queue.Enqueue);
        Assert.AreEqual(1, checks);
        Assert.AreEqual(0, increments);
        Assert.AreEqual(1, queue.Count);
        queue.Dequeue()();
        Assert.AreEqual(1, increments);
    }

    [TestMethod]
    public void Revision_CheckAccessAndDirectIncrementFailuresEscape()
    {
        var failure = new InvalidOperationException("direct");
        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => McpTerminalPresentation.IncrementRevision(() => throw failure, () => Assert.Fail(), _ => Assert.Fail())));
        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => McpTerminalPresentation.IncrementRevision(static () => true, () => throw failure, _ => Assert.Fail())));
    }

    [TestMethod]
    public void Revision_PostInvalidOperationIsIgnored()
    {
        var posts = 0;
        McpTerminalPresentation.IncrementRevision(static () => false, () => Assert.Fail(), _ => { posts++; throw new InvalidOperationException("no app"); });
        Assert.AreEqual(1, posts);
    }

    [TestMethod]
    public void Revision_OtherPostFailuresEscape()
    {
        var failure = new ApplicationException("post");
        Assert.AreSame(failure, Assert.ThrowsExactly<ApplicationException>(() => McpTerminalPresentation.IncrementRevision(static () => false, () => Assert.Fail(), _ => throw failure)));
        var cancellation = new OperationCanceledException("post");
        Assert.AreSame(cancellation, Assert.ThrowsExactly<OperationCanceledException>(() => McpTerminalPresentation.IncrementRevision(static () => false, () => Assert.Fail(), _ => throw cancellation)));
    }

    [TestMethod]
    public void Revision_DeferredIncrementFailureEscapesAfterPosting()
    {
        var queue = new Queue<Action>();
        var failure = new InvalidOperationException("later callback");
        McpTerminalPresentation.IncrementRevision(static () => false, () => throw failure, queue.Enqueue);
        Assert.AreEqual(1, queue.Count);
        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => queue.Dequeue()()));
    }

    private static PluginCommandContribution Command(string name) => new()
    {
        Name = name,
        Handler = static (_, _) => throw new AssertFailedException("Handler must remain deferred."),
    };
}
