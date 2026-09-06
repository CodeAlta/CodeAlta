using CodeAlta.LiveTool;

namespace CodeAlta.Tests;

// Pure queue tests: no host, catalog, provider, filesystem, or terminal startup.
[TestClass]
public sealed class AltaAskQueueTests
{
    [TestMethod]
    public async Task QueueAsync_OwnsNestedCallerCollections()
    {
        var choices = new List<AltaAskChoice> { new() { Title = " First ", Description = "description" }, new() { Title = "Second" } };
        var question = new AltaAskQuestion { Title = " Title ", Question = "Question?", Choices = choices, Freeform = new() { Title = "Other", Placeholder = "text" } };
        var questions = new[] { question, new AltaAskQuestion { Title = "Next", Question = "Next?", Freeform = new() } };
        var request = new AltaAskRequest { Questions = questions, File = new() { Path = "not-accessed.md" } };
        var caller = new AltaCallerIdentity { Kind = "agent", SourceSessionId = "source", SourceAgentId = "agent", SourceProjectId = "project", PluginRuntimeKey = "plugin" };
        var service = new AltaAskService();
        var before = DateTimeOffset.UtcNow;
        var result = await service.QueueAsync(request, " session ", caller);
        var original = service.Peek("session")!;

        choices[0] = new() { Title = "Changed" };
        choices.Clear();
        questions[0] = new() { Title = "Replaced" };

        var queued = service.Peek("session")!;
        Assert.AreEqual(result.AskId, queued.AskId);
        Assert.AreEqual("session", result.SessionId);
        Assert.AreEqual("session", queued.SessionId);
        Assert.AreEqual(caller, queued.Caller);
        Assert.IsTrue(queued.CreatedAt >= before && queued.CreatedAt <= DateTimeOffset.UtcNow);
        Assert.AreEqual(original.CreatedAt, queued.CreatedAt);
        Assert.AreEqual("not-accessed.md", queued.Request.File!.Path);
        Assert.AreEqual(2, queued.Request.Questions.Count);
        Assert.AreEqual(" Title ", queued.Request.Questions[0].Title);
        Assert.AreEqual("Next", queued.Request.Questions[1].Title);
        Assert.AreEqual("Question?", queued.Request.Questions[0].Question);
        Assert.AreEqual(question.Freeform, queued.Request.Questions[0].Freeform);
        Assert.AreEqual(2, queued.Request.Questions[0].Choices.Count);
        Assert.AreEqual(" First ", queued.Request.Questions[0].Choices[0].Title);
        Assert.AreEqual("description", queued.Request.Questions[0].Choices[0].Description);
        Assert.AreEqual("Second", queued.Request.Questions[0].Choices[1].Title);
    }

    [TestMethod]
    public async Task Peek_CollectionsCannotBeMutated()
    {
        var service = new AltaAskService();
        await service.QueueAsync(CreateRequest(), "session", AltaCallerIdentity.Host);
        var queued = service.Peek("session")!;
        AssertReadOnly(queued.Request.Questions, new AltaAskQuestion());
        AssertReadOnly(queued.Request.Questions[0].Choices, new AltaAskChoice());
        Assert.AreEqual("Question", service.Peek("session")!.Request.Questions[0].Title);
        Assert.AreEqual("Choice", service.Peek("session")!.Request.Questions[0].Choices[0].Title);
    }

    [TestMethod]
    public async Task QueueAsync_OwnsQuestionListAndChoiceArray()
    {
        var choices = new[] { new AltaAskChoice { Title = "Original" } };
        var questions = new List<AltaAskQuestion> { new() { Title = "Question", Choices = choices } };
        var service = new AltaAskService();
        await service.QueueAsync(new() { Questions = questions }, "session", AltaCallerIdentity.Host);
        choices[0] = new() { Title = "Changed" };
        Assert.AreEqual("Original", service.Peek("session")!.Request.Questions[0].Choices[0].Title);
        questions.Clear();
        Assert.AreEqual("Question", service.Peek("session")!.Request.Questions[0].Title);
    }

    [TestMethod]
    public async Task TryRemoveHead_RejectsStaleWrongSessionWrongAskAndNonHeadKeys()
    {
        IAltaAskService service = new AltaAskService();
        var changes = new List<string>();
        service.QueueChanged += (_, args) => changes.Add(args.SessionId);
        var first = await service.QueueAsync(CreateRequest(), "a", AltaCallerIdentity.Host);
        var second = await service.QueueAsync(CreateRequest(), "a", AltaCallerIdentity.Host);
        var other = await service.QueueAsync(CreateRequest(), "b", AltaCallerIdentity.Host);
        Assert.IsFalse(service.TryRemoveHead("b", first.AskId).Accepted);
        Assert.IsFalse(service.TryRemoveHead("a", other.AskId).Accepted);
        Assert.IsFalse(service.TryRemoveHead("a", second.AskId).Accepted);
        Assert.IsFalse(service.TryRemoveHead("a", "wrong").Accepted);
        Assert.IsFalse(service.TryRemoveHead("A", first.AskId).Accepted);
        Assert.IsFalse(service.TryRemoveHead(" a ", first.AskId).Accepted);
        Assert.IsFalse(service.TryRemoveHead("a", " " + first.AskId).Accepted);
        Assert.AreEqual(3, changes.Count);
        Assert.IsTrue(service.TryRemoveHead("a", first.AskId).Accepted);
        Assert.IsFalse(service.TryRemoveHead("a", first.AskId).Accepted);
        Assert.AreEqual(second.AskId, service.Peek("a")!.AskId);
        Assert.AreEqual(other.AskId, service.Peek("b")!.AskId);
        Assert.IsTrue(service.TryRemoveHead("a", second.AskId).Accepted);
        Assert.IsFalse(service.TryRemoveHead("a", second.AskId).Accepted);
        Assert.AreEqual(0, service.GetPending("a").Count);
        Assert.AreEqual(1, service.GetPending("b").Count);
        Assert.AreEqual(5, changes.Count);
    }

    [TestMethod]
    public async Task TryRemoveHead_StaleCallbackCannotRemoveNextAsk()
    {
        var service = new AltaAskService();
        var first = await service.QueueAsync(CreateRequest(), "session", AltaCallerIdentity.Host);
        var second = await service.QueueAsync(CreateRequest(), "session", AltaCallerIdentity.Host);
        Assert.IsTrue(service.TryRemoveHead("session", first.AskId).Accepted);
        Assert.IsFalse(service.TryRemoveHead("session", first.AskId).Accepted);
        Assert.AreEqual(second.AskId, service.Peek("session")!.AskId);
    }

    [TestMethod]
    public async Task GetPending_IsAnImmutablePointInTimeFifoSnapshot()
    {
        var service = new AltaAskService();
        Assert.AreEqual(0, service.GetPending("unopened").Count);
        var first = await service.QueueAsync(CreateRequest(), "unopened", AltaCallerIdentity.Host);
        var second = await service.QueueAsync(CreateRequest(), "unopened", AltaCallerIdentity.Host);
        var snapshot = service.GetPending("unopened");
        AssertReadOnly(snapshot, snapshot[1]);
        AssertReadOnly(snapshot[0].Request.Questions, new AltaAskQuestion());
        AssertReadOnly(snapshot[0].Request.Questions[0].Choices, new AltaAskChoice());
        Assert.AreEqual(first.AskId, snapshot[0].AskId);
        Assert.AreEqual(second.AskId, snapshot[1].AskId);
        Assert.IsTrue(service.TryRemoveHead("unopened", first.AskId).Accepted);
        await service.QueueAsync(CreateRequest(), "unopened", AltaCallerIdentity.Host);
        Assert.AreEqual(2, snapshot.Count);
        Assert.AreEqual(first.AskId, snapshot[0].AskId);
        Assert.AreEqual(second.AskId, snapshot[1].AskId);
        Assert.AreEqual(second.AskId, service.GetPending("unopened")[0].AskId);
    }

    [TestMethod]
    public async Task QueueChanged_ObserverFailuresAreReturnedWithCommittedOutcomeAndDoNotSkipObservers()
    {
        var service = new AltaAskService();
        using var cancellation = new CancellationTokenSource();
        var snapshots = new List<IReadOnlyList<AltaQueuedAsk>>();
        service.QueueChanged += (_, _) => throw new InvalidOperationException("in-memory observer failure");
        service.QueueChanged += (_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException("observer cancellation");
        };
        service.QueueChanged += (_, args) => snapshots.Add(service.GetPending(args.SessionId));
        var queued = await service.QueueAsync(CreateRequest(), "session", AltaCallerIdentity.Host, cancellation.Token);
        Assert.AreEqual(2, queued.NotificationErrors.Count);
        AssertReadOnly(queued.NotificationErrors, "replacement");
        StringAssert.Contains(queued.NotificationErrors[0], "in-memory observer failure");
        Assert.AreEqual(queued.AskId, snapshots[0][0].AskId);
        Assert.AreEqual(queued.AskId, service.Peek("session")!.AskId);

        var removed = service.TryRemoveHead("session", queued.AskId);
        Assert.IsTrue(removed.Accepted);
        Assert.AreEqual(2, removed.NotificationErrors.Count);
        AssertReadOnly(removed.NotificationErrors, "replacement");
        Assert.AreEqual(0, snapshots[1].Count);
        Assert.IsNull(service.Peek("session"));
        var stale = service.TryRemoveHead("session", queued.AskId);
        Assert.IsFalse(stale.Accepted);
        Assert.AreEqual(0, stale.NotificationErrors.Count);
        Assert.AreEqual(2, snapshots.Count);
    }

    [TestMethod]
    public async Task QueueChanged_AllowsCrossThreadRequeryAndReentrantRemovalOutsideOwnership()
    {
        var service = new AltaAskService();
        var reentered = false;
        service.QueueChanged += (_, args) =>
        {
            var query = Task.Run(() => service.GetPending(args.SessionId));
            Assert.IsTrue(query.Wait(TimeSpan.FromSeconds(5)), "Observer must run outside the queue lock.");
            if (query.Result.Count > 0)
            {
                reentered = service.TryRemoveHead(args.SessionId, query.Result[0].AskId).Accepted;
            }
        };
        var queued = await service.QueueAsync(CreateRequest(), "session", AltaCallerIdentity.Host);
        Assert.AreEqual(0, queued.NotificationErrors.Count);
        Assert.IsTrue(reentered);
        Assert.IsNull(service.Peek("session"));
    }

    [TestMethod]
    public async Task QueueAsync_CancellationBeforeAdmissionDoesNotEnqueueOrNotify()
    {
        var service = new AltaAskService();
        var notifications = 0;
        service.QueueChanged += (_, _) => notifications++;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.QueueAsync(CreateRequest(), "session", AltaCallerIdentity.Host, cancellation.Token));
        Assert.AreEqual(0, service.GetPending("session").Count);
        Assert.AreEqual(0, notifications);
    }

    [TestMethod]
    public async Task QueueAsync_CancellationDuringCaptureIsObservedBeforeAdmission()
    {
        var service = new AltaAskService();
        var notifications = 0;
        service.QueueChanged += (_, _) => notifications++;
        using var cancellation = new CancellationTokenSource();
        var questions = new CancelOnReadList<AltaAskQuestion>(CreateRequest().Questions, cancellation);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.QueueAsync(new() { Questions = questions }, "session", AltaCallerIdentity.Host, cancellation.Token));
        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.AreEqual(0, service.GetPending("session").Count);
        Assert.AreEqual(0, notifications);
    }

    [TestMethod]
    public void QueueOperations_ValidateKeysBeforeMutation()
    {
        var service = new AltaAskService();
        Assert.ThrowsExactly<ArgumentNullException>(() => service.QueueAsync(null!, "session", AltaCallerIdentity.Host));
        Assert.ThrowsExactly<ArgumentNullException>(() => service.QueueAsync(CreateRequest(), "session", null!));
        Assert.ThrowsExactly<ArgumentException>(() => service.QueueAsync(CreateRequest(), " ", AltaCallerIdentity.Host));
        Assert.ThrowsExactly<ArgumentNullException>(() => service.Peek(null!));
        Assert.ThrowsExactly<ArgumentException>(() => service.GetPending(" "));
        Assert.ThrowsExactly<ArgumentNullException>(() => service.TryRemoveHead("session", null!));
        Assert.ThrowsExactly<ArgumentException>(() => service.TryRemoveHead("session", " "));
        Assert.ThrowsExactly<ArgumentException>(() => service.TryRemoveHead(" ", "ask"));
        Assert.IsNull(service.Peek("session"));
    }

    private sealed class CancelOnReadList<T>(IReadOnlyList<T> items, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => items.Count;
        public T this[int index]
        {
            get
            {
                cancellation.Cancel();
                return items[index];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
            {
                yield return this[index];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void AssertReadOnly<T>(IReadOnlyList<T> values, T replacement)
    {
        Assert.IsFalse(values is T[], "Snapshots must not expose writable arrays.");
        if (values is IList<T> list)
        {
            Assert.IsTrue(list.IsReadOnly);
            Assert.ThrowsExactly<NotSupportedException>(() => list[0] = replacement);
            Assert.ThrowsExactly<NotSupportedException>(() => list.Clear());
            Assert.ThrowsExactly<NotSupportedException>(() => list.Add(replacement));
        }
    }

    private static AltaAskRequest CreateRequest() => new()
    {
        Questions = new List<AltaAskQuestion>
        {
            new() { Title = "Question", Question = "Proceed?", Choices = new List<AltaAskChoice> { new() { Title = "Choice" } } },
        },
    };
}
