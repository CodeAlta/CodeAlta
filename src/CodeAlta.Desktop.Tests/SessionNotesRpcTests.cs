using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Desktop.Rpc;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Desktop.Tests;

/// <summary>Generated notes transport through the actual inert workspace admission owner.</summary>
[TestClass]
public sealed class SessionNotesRpcTests
{
    private const string Epoch = "11111111-1111-4111-8111-111111111111";

    [TestMethod]
    public Task Clear_UsesExplicitSessionTargetAndDoesNotMisreportUncertainWrites() => Fixture.Run(_ => Task.FromResult("# Existing"), async f =>
    {
        var targets = new List<string>();
        var rpc = new SessionNotesService(f.Owner, Epoch, (session, _) =>
        {
            targets.Add(session);
            return targets.Count == 1 ? Task.CompletedTask : Task.FromException(new IOException("private journal failure"));
        });
        var invalid = await rpc.ClearAsync(new(Epoch, " other"), default);
        Assert.AreEqual("invalid_request", invalid.Status);
        Assert.IsNull(invalid.SessionId);
        Assert.AreEqual("stale_epoch", (await rpc.ClearAsync(new("22222222-2222-4222-8222-222222222222", "other"), default)).Status);
        Assert.AreEqual(0, targets.Count);
        var result = await rpc.ClearAsync(new(Epoch, "session-A"), default);
        Assert.AreEqual(new SessionNotesClearResponse("ok", Epoch, "session-A"), result);
        var wire = JsonSerializer.SerializeToUtf8Bytes(result, DesktopJsonContext.Default.SessionNotesClearResponse);
        Assert.AreEqual(result, JsonSerializer.Deserialize(wire, DesktopJsonContext.Default.SessionNotesClearResponse));
        result = await rpc.ClearAsync(new(Epoch, "session-B"), default);
        Assert.AreEqual("clear_unconfirmed", result.Status);
        Assert.AreEqual("session-B", result.SessionId);
        Assert.IsFalse(JsonSerializer.Serialize(result, DesktopJsonContext.Default.SessionNotesClearResponse).Contains("private", StringComparison.Ordinal));
        CollectionAssert.AreEqual(new[] { "session-A", "session-B" }, targets);
        Assert.AreEqual("# Existing", (await f.Call(new(Epoch, "session"))).Markdown); // Failed clear cannot imply empty notes.
    });

    [TestMethod]
    public Task Clear_MissingAndCancelled_DoNotMasqueradeAsSuccessful() => Fixture.Run(_ => Task.FromResult(""), async f =>
    {
        var rpc = new SessionNotesService(f.Owner, Epoch, (session, _) =>
            Task.FromException(new SessionNotesSessionNotFoundException(session)));
        Assert.AreEqual("clear_unconfirmed", (await rpc.ClearAsync(new(Epoch, "missing"), default)).Status);
        var disposedAfterCommit = new SessionNotesService(f.Owner, Epoch, (_, _) =>
            Task.FromException(new ObjectDisposedException("private observer")));
        Assert.AreEqual("clear_unconfirmed", (await disposedAfterCommit.ClearAsync(new(Epoch, "session"), default)).Status);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => rpc.ClearAsync(new(Epoch, "missing"), cancelled.Token));
    });

    [TestMethod]
    public Task InvalidIdentityAndEpoch_RejectBeforeReadAndBoundErrors() => Fixture.Run(_ => throw new AssertFailedException("No read."), async f =>
    {
        foreach (var session in new string?[] { null, "", " ", " session", "session\n", new('x', 65536) })
        {
            var request = JsonSerializer.Deserialize(JsonSerializer.Serialize(new { expectedHostEpoch = Epoch, sessionId = session }), DesktopJsonContext.Default.SessionNotesRequest);
            Assert.IsNotNull(request);
            var result = await f.Call(request);
            Assert.AreEqual("invalid_request", result.Status);
            Assert.IsNull(result.SessionId);
            Assert.IsNull(result.Markdown);
            Assert.IsTrue(JsonSerializer.SerializeToUtf8Bytes(result, DesktopJsonContext.Default.SessionNotesResponse).Length < 256);
        }
        // JSON serialization can normalize lone surrogates; exercise that boundary directly.
        var malformed = await f.Call(new(Epoch, "\ud800"));
        Assert.AreEqual("invalid_request", malformed.Status);
        Assert.IsNull(malformed.SessionId);
        Assert.IsNull(malformed.Markdown);
        foreach (var epoch in new[] { "", Epoch + "\n", "00000000-0000-0000-0000-000000000000", "ABCDEFAB-1111-4111-8111-111111111111" })
            Assert.AreEqual("invalid_request", (await f.Call(new(epoch, "session"))).Status);
        Assert.AreEqual("stale_epoch", (await f.Call(new("22222222-2222-4222-8222-222222222222", "session"))).Status);
        var cancel = f.Keep(() => f.Caller.CancelAsync()); await cancel;
        var cancelled = f.Keep(() => f.Rpc.CurrentAsync(new(Epoch, "session"), f.Caller.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled);
    });

    [TestMethod]
    public Task GeneratedInboundAndFullEscaping_RoundTripWithinFramedBudget() => Fixture.Run(_ => Task.FromResult(new string('\u0001', 16384)), async f =>
    {
        var request = new SessionNotesRequest(Epoch, new string('\u4e00', 256));
        var json = JsonSerializer.Serialize(request, DesktopJsonContext.Default.SessionNotesRequest);
        var incoming = JsonSerializer.Deserialize(json, DesktopJsonContext.Default.SessionNotesRequest);
        Assert.IsNotNull(incoming);
        Assert.AreEqual(request, incoming);
        Assert.AreEqual(json, JsonSerializer.Serialize(incoming, DesktopJsonContext.Default.SessionNotesRequest));
        var result = await f.Call(incoming);
        Assert.AreEqual("ok", result.Status);
        Assert.AreEqual(new string('\u0001', 16384), result.Markdown);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, DesktopJsonContext.Default.SessionNotesResponse);
        using var document = JsonDocument.Parse(bytes);
        var escapedMarkdown = document.RootElement.GetProperty("markdown").GetRawText();
        var escapedIdentity = document.RootElement.GetProperty("sessionId").GetRawText();
        Assert.AreEqual(2 + 6 * 16384, escapedMarkdown.Length);
        Assert.AreEqual(16384, escapedMarkdown.Split("\\u0001", StringSplitOptions.None).Length - 1);
        Assert.AreEqual(2 + 6 * 256, escapedIdentity.Length);
        Assert.AreEqual(256, escapedIdentity.Split("\\u4E00", StringSplitOptions.None).Length - 1);
        Console.WriteLine($"Owned notes generated serializer: Markdown UTF-16={result.Markdown!.Length}, escaped JSON={escapedMarkdown.Length}; identity UTF-16={result.SessionId!.Length}, escaped JSON={escapedIdentity.Length}; payload UTF-8={bytes.Length}; framing=4096; framed={bytes.Length + 4096}; budget={SessionNotesService.MaximumResponseBytes}.");
        Assert.IsTrue(bytes.Length + 4096 <= SessionNotesService.MaximumResponseBytes);
        Assert.AreEqual(result, JsonSerializer.Deserialize(bytes, DesktopJsonContext.Default.SessionNotesResponse));
    });

    [TestMethod]
    public async Task CompleteText_EmptyUnicodeAndLimitsAreNotTruncated()
    {
        foreach (var markdown in new[] { "", "# Exact\r\n😀\u0085\ufeff  ", string.Concat(Enumerable.Repeat("😀", 8192)), new string('x', 16385), "\ud800", "\udc00" })
            await Fixture.Run(_ => Task.FromResult(markdown), async f =>
            {
                var result = await f.Call(new(Epoch, "session"));
                var valid = markdown.Length <= 16384 && markdown is not ("\ud800" or "\udc00");
                Assert.AreEqual(valid ? "ok" : "wire_limit", result.Status);
                Assert.AreEqual(valid ? markdown : null, result.Markdown);
            });
    }

    [TestMethod]
    public async Task ReadFailures_AreSanitizedAndNeverEmptyOrCapacity()
    {
        foreach (var failure in new Exception[] { new InvalidOperationException("private downstream"), new IOException("private path"),
            new SessionNotesSessionNotFoundException("private missing identity"), new ObjectDisposedException("private owner"), new OperationCanceledException("not caller cancellation") })
            await Fixture.Run(_ => throw failure, async f =>
            {
                var result = await f.Call(new(Epoch, "session"));
                Assert.AreEqual(failure is SessionNotesSessionNotFoundException ? "missing_session" : failure is ObjectDisposedException ? "closed" : "read_failed", result.Status);
                Assert.IsNull(result.Markdown);
                Assert.AreEqual("session", result.SessionId);
                Assert.IsFalse(JsonSerializer.Serialize(result, DesktopJsonContext.Default.SessionNotesResponse).Contains("private", StringComparison.Ordinal));
            });
    }

    [TestMethod]
    public Task SynchronousGateCapacityAndClosure_AreDistinctFromDownstreamFailure() => Fixture.Run(null, async f =>
    {
        var originals = Enumerable.Range(0, 8).Select(_ => f.Keep(() => f.Owner.ReadNotesMarkdownAsync("session", default))).ToArray();
        Assert.AreEqual("capacity", (await f.Call(new(Epoch, "session"))).Status);
        var drain = f.Keep(() => f.Owner.DisposeAsync().AsTask());
        Assert.IsFalse(drain.IsCompleted);
        Assert.AreEqual("closed", (await f.Call(new(Epoch, "session"))).Status);
        f.Release.TrySetResult("done");
        await Task.WhenAll(originals);
        await drain;
    });

    [TestMethod]
    public Task CancelledRpcWait_DoesNotCompleteActualRead() => Fixture.Run(null, async f =>
    {
        var read = f.Keep(() => f.Rpc.CurrentAsync(new(Epoch, "session"), f.Caller.Token));
        await f.Entered.Task;
        var cancellation = f.Keep(() => f.Caller.CancelAsync()); await cancellation;
        await Assert.ThrowsAsync<OperationCanceledException>(() => read);
        var drain = f.Keep(() => f.Owner.DisposeAsync().AsTask());
        Assert.IsFalse(drain.IsCompleted);
        f.Release.TrySetResult("retained");
        await drain;
    });

    [TestMethod]
    public Task GeneratedInboundMissingNullAndWrongTypes_DoNotCreateAuthority() => Fixture.Run(_ => throw new AssertFailedException("No read."), async f =>
    {
        foreach (var json in new[] { "{}", "{\"expectedHostEpoch\":null,\"sessionId\":null}", "{\"expectedHostEpoch\":\"" + Epoch + "\"}" })
        {
            var request = JsonSerializer.Deserialize(json, DesktopJsonContext.Default.SessionNotesRequest);
            Assert.IsNotNull(request);
            Assert.AreEqual("invalid_request", (await f.Call(request)).Status);
        }
        foreach (var property in new[] { "expectedHostEpoch", "sessionId" })
        foreach (var value in new[] { "1", "true", "[]", "{}" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"" + property + "\":" + value + "}", DesktopJsonContext.Default.SessionNotesRequest));
    });

    private sealed class Fixture
    {
        internal TaskCompletionSource<string> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenSource Caller { get; } = new();
        internal OwnedSessionWorkspace Owner { get; }
        internal SessionNotesService Rpc { get; }
        private readonly List<Task> _originals = [];
        private readonly TaskCompletionSource _stopLaunch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _stop = Task.CompletedTask;
        private Task _stopObserver = Task.CompletedTask;
        private Task _lifetimeObserver = Task.CompletedTask;
        private Task? _cancellation;
        private Task? _drain;
        private Exception? _readFailure;
        private Task? _lifetime;
        internal Task Keep(Func<Task> start) => Keep(async () => { await start().ConfigureAwait(false); return true; });
        internal Task<T> Keep<T>(Func<Task<T>> start)
        {
            var acquisition = new TaskCompletionSource<Task<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = Observe(acquisition.Task);
            _originals.Add(observer); // Publish the acquisition and observer before invoking any original.
            try { acquisition.SetResult(start()); } catch (Exception ex) { acquisition.SetException(ex); }
            return observer;
        }
        private static async Task<T> Observe<T>(Task<Task<T>> acquisition) => await (await acquisition.ConfigureAwait(false)).ConfigureAwait(false);
        internal Task<SessionNotesResponse> Call(SessionNotesRequest request) => Keep(() => Rpc.CurrentAsync(request, default));

        private Fixture(Func<CancellationToken, Task<string>>? read)
        {
            Owner = new(static _ => Task.FromResult<IReadOnlyList<ProjectDescriptor>>([]), Empty,
                static (_, _, _) => throw new AssertFailedException("No history."),
                async (_, token) =>
                {
                    try { Assert.AreEqual(CancellationToken.None, token); Entered.TrySetResult(); return await (read is null ? Release.Task : read(token)); }
                    catch (Exception ex) { _readFailure = ex; throw; }
                });
            Rpc = new(Owner, Epoch);
        }

        internal static async Task Run(Func<CancellationToken, Task<string>>? read, Func<Fixture, Task> body)
        {
            var f = new Fixture(read);
            var launch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f._stop = f.StopCore(); // Retain shutdown/late acquisitions before either launch can be released.
            f._stopObserver = Settle(f._stop);
            f._lifetime = f.RunCore(body, launch.Task);
            f._lifetimeObserver = Settle(f._lifetime);
            launch.TrySetResult();
            try { await f._lifetime.WaitAsync(TimeSpan.FromSeconds(10)); await f._lifetimeObserver; f.Caller.Dispose(); }
            catch (Exception ex) { ex.Data["RetainedFixture"] = f; ex.Data["OriginalLifetime"] = f._lifetime; throw; }
            finally { f.Release.TrySetResult(""); f._stopLaunch.TrySetResult(); }
        }

        private async Task RunCore(Func<Fixture, Task> body, Task launch)
        {
            await launch;
            Exception? primary = null;
            try { var originalBody = Keep(() => body(this)); await originalBody; }
            catch (Exception ex) { primary = ex; throw; }
            finally
            {
                Release.TrySetResult("");
                _stopLaunch.TrySetResult();
                await Task.WhenAll(_originals.Select(Settle));
                try { await _stopObserver; await _stop; }
                // The workspace observer may still be removing a read when RPC returns its sanitized
                // result. Only that identical, body-inspected failure is permitted from cleanup.
                catch (Exception ex) when (primary is null && ReferenceEquals(ex, _readFailure)) { }
                catch (Exception ex) { if (primary is not null) throw new AggregateException(primary, ex); throw; }
            }
        }

        private async Task StopCore()
        {
            await _stopLaunch.Task;
            Release.TrySetResult("");
            _cancellation = Caller.CancelAsync();
            _drain = Owner.DisposeAsync().AsTask();
            await Task.WhenAll(Settle(_cancellation), Settle(_drain));
            await _cancellation;
            await _drain;
        }

        private static async Task Settle(Task original) { try { await original; } catch { /* Outcome asserted by the body. */ } }
        private static async IAsyncEnumerable<AgentSessionMetadata> Empty([EnumeratorCancellation] CancellationToken token)
        { await Task.CompletedTask; token.ThrowIfCancellationRequested(); yield break; }
    }
}
