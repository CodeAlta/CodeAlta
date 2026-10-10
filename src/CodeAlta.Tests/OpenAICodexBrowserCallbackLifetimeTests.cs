using System.Net;
using System.Net.Sockets;
using CodeAlta.Agent.OpenAI.Codex;

namespace CodeAlta.Tests;

[TestClass]
public sealed class OpenAICodexBrowserCallbackLifetimeTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task CallbackCancellation_JoinsLateAcceptFaultBeforeDisposing(bool callerCanceled, bool disposedFault)
    {
        using var cancellation = new CancellationTokenSource();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var accept = new TaskCompletionSource<HttpListenerContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception lateFault = disposedFault ? new ObjectDisposedException("listener") : new HttpListenerException(995);
        var stopped = false;
        var disposed = false;
        var disposedWithAcceptPending = false;
        var completedLogin = false;
        var callerToken = cancellation.Token;
        var wait = OpenAICodexSubscriptionLoginManager.WaitForBrowserCallbackAsync(
            () =>
            {
                // Cancel while acquiring the original task, before the cancelable wait is
                // attached. Stop is synchronous, but EndGetContext's fault may arrive later.
                (callerCanceled ? cancellation : timeout).Cancel();
                return accept.Task;
            },
            () => stopped = true,
            () =>
            {
                disposedWithAcceptPending = !accept.Task.IsCompleted;
                disposed = true;
            },
            (_, _) =>
            {
                completedLogin = true;
                throw new AssertFailedException("No OAuth callback was supplied.");
            }, timeout.Token, callerToken).AsTask();
        try
        {
            Assert.IsTrue(stopped);
            Assert.IsFalse(wait.IsCompleted, "Cancellation must not abandon the pending GetContextAsync task.");
            Assert.IsFalse(disposed, "The original accept must settle before listener disposal.");

            accept.SetException(lateFault);
            if (callerCanceled)
            {
                var error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreEqual(callerToken, error.CancellationToken);
            }
            else
            {
                var error = await Assert.ThrowsExactlyAsync<TimeoutException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreEqual("ChatGPT sign-in expired. Continue with ChatGPT to try again.", error.Message);
            }

            Assert.IsTrue(disposed);
            Assert.IsFalse(disposedWithAcceptPending);
            Assert.IsFalse(completedLogin);
        }
        finally
        {
            // Even the red fixture owns and observes the original fault; it never relies
            // on finalization or installs a global unobserved-task suppression handler.
            accept.TrySetException(lateFault);
            await Task.WhenAll(ObserveAsync(accept.Task), ObserveAsync(wait)).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AlreadyCanceledCallback_DoesNotStartAcceptAndDisposes(bool callerCanceled)
    {
        var token = new CancellationToken(true);
        var disposed = 0;
        var wait = OpenAICodexSubscriptionLoginManager.WaitForBrowserCallbackAsync(
            () => throw new AssertFailedException("An already canceled callback must not start an accept."),
            () => throw new AssertFailedException("There is no pending accept to stop."),
            () => disposed++,
            (_, _) => throw new AssertFailedException("No callback was supplied."),
            token, callerCanceled ? token : CancellationToken.None).AsTask();

        if (callerCanceled)
        {
            var error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => wait);
            Assert.AreEqual(token, error.CancellationToken);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => wait);
        }

        Assert.AreEqual(1, disposed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AcceptFailure_PropagatesOriginalAndUnregistersBeforeDisposal(bool disposedFault)
    {
        using var cancellation = new CancellationTokenSource();
        Exception failure = disposedFault ? new ObjectDisposedException("listener") : new HttpListenerException(995);
        var accept = Task.FromException<HttpListenerContext>(failure);
        var stopped = 0;
        var disposed = 0;
        var wait = OpenAICodexSubscriptionLoginManager.WaitForBrowserCallbackAsync(
            () => accept,
            () => stopped++,
            () =>
            {
                disposed++;
                // A deadline racing final listener disposal must no longer call Stop.
                cancellation.Cancel();
            },
            (_, _) => throw new AssertFailedException("No callback was supplied."),
            cancellation.Token, CancellationToken.None).AsTask();
        try
        {
            var error = await Assert.ThrowsAsync<Exception>(() => wait);
            Assert.AreSame(failure, error, "A listener failure without cancellation must not be hidden or transformed.");
            Assert.AreEqual(1, disposed);
            Assert.AreEqual(0, stopped, "Unregister Stop before releasing its listener.");
        }
        finally
        {
            await Task.WhenAll(ObserveAsync(accept), ObserveAsync(wait)).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LoopbackCallback_RespondsAndCleansUpAfterIgnoredRequests(bool callbackFails)
    {
        using var listener = StartLoopbackListener();
        using var cancellation = new CancellationTokenSource();
        using var browser = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var baseUri = listener.Prefixes.Single();
        var callbackUri = new Uri(baseUri + "auth/callback?code=inert&state=inert");
        var accepts = new List<Task<HttpListenerContext>>();
        var requests = new List<Task<HttpResponseMessage>>();
        var credential = new OpenAICodexSubscriptionCredential { AccessToken = "synthetic-not-a-token" };
        var failure = new InvalidOperationException("synthetic callback rejection");
        var completedLogin = 0;
        var stopped = 0;
        var disposed = 0;
        var disposedWithAcceptPending = false;
        var wait = OpenAICodexSubscriptionLoginManager.WaitForBrowserCallbackAsync(
            () =>
            {
                var accept = listener.GetContextAsync();
                accepts.Add(accept);
                return accept;
            },
            () =>
            {
                stopped++;
                listener.Stop();
            },
            () =>
            {
                disposed++;
                disposedWithAcceptPending = accepts.Any(accept => !accept.IsCompleted);
                listener.Close();
            },
            (uri, token) =>
            {
                completedLogin++;
                Assert.AreEqual(callbackUri, uri);
                Assert.AreEqual(cancellation.Token, token);
                return callbackFails ? ValueTask.FromException<OpenAICodexSubscriptionCredential>(failure) : ValueTask.FromResult(credential);
            }, cancellation.Token, cancellation.Token).AsTask();
        try
        {
            var missing = browser.GetAsync(baseUri + "favicon.ico", cancellation.Token);
            requests.Add(missing);
            using var missingResponse = await missing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(HttpStatusCode.NotFound, missingResponse.StatusCode);

            var post = browser.PostAsync(callbackUri, null, cancellation.Token);
            requests.Add(post);
            using var postResponse = await post.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(HttpStatusCode.NotFound, postResponse.StatusCode);

            var callback = browser.GetAsync(callbackUri, cancellation.Token);
            requests.Add(callback);
            using var response = await callback.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(callbackFails ? HttpStatusCode.BadRequest : HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.CacheControl?.NoStore);
            StringAssert.Contains(await response.Content.ReadAsStringAsync(cancellation.Token), callbackFails ? "login failed" : "login complete");

            if (callbackFails)
            {
                var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreSame(failure, error);
            }
            else
            {
                Assert.AreSame(credential, await wait.WaitAsync(TimeSpan.FromSeconds(5)));
            }

            Assert.AreEqual(1, completedLogin);
            Assert.AreEqual(3, accepts.Count, "Do not start another accept after the final callback.");
            Assert.IsTrue(accepts.All(accept => accept.IsCompletedSuccessfully));
            Assert.AreEqual(1, disposed);
            Assert.IsFalse(disposedWithAcceptPending);
            Assert.ThrowsExactly<ObjectDisposedException>(listener.Start);
            cancellation.Cancel();
            Assert.AreEqual(0, stopped, "Completed login must not leave a Stop registration on a disposed listener.");
        }
        finally
        {
            cancellation.Cancel();
            await Task.WhenAll(requests.Select(ObserveAsync).Append(ObserveAsync(wait))).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(accepts.Select(ObserveAsync)).WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var request in requests.Where(request => request.IsCompletedSuccessfully))
            {
                request.Result.Dispose();
            }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LoopbackCancellation_JoinsActualAcceptAndClosesListener(bool callerCanceled)
    {
        using var listener = StartLoopbackListener();
        using var cancellation = new CancellationTokenSource();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        Task<HttpListenerContext>? accept = null;
        var disposedWithAcceptPending = false;
        var stopped = 0;
        var disposed = 0;
        var callerToken = cancellation.Token;
        var wait = OpenAICodexSubscriptionLoginManager.WaitForBrowserCallbackAsync(
            () => accept = listener.GetContextAsync(),
            () =>
            {
                stopped++;
                listener.Stop();
            },
            () =>
            {
                disposed++;
                disposedWithAcceptPending = accept is null || !accept.IsCompleted;
                listener.Close();
            },
            (_, _) => throw new AssertFailedException("No callback was supplied."),
            timeout.Token, callerToken).AsTask();
        try
        {
            Assert.IsNotNull(accept);
            Assert.IsFalse(accept.IsCompleted);
            (callerCanceled ? cancellation : timeout).Cancel();
            if (callerCanceled)
            {
                var error = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.AreEqual(callerToken, error.CancellationToken);
            }
            else
            {
                await Assert.ThrowsExactlyAsync<TimeoutException>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
            }

            Assert.IsTrue(accept.IsFaulted);
            Assert.AreEqual(1, stopped);
            Assert.AreEqual(1, disposed);
            Assert.IsFalse(disposedWithAcceptPending);
            Assert.ThrowsExactly<ObjectDisposedException>(listener.Start);
        }
        finally
        {
            cancellation.Cancel();
            await Task.WhenAll(ObserveAsync(wait), ObserveAsync(accept ?? Task.CompletedTask)).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LoopbackListenerShutdown_PropagatesOriginalAcceptFault(bool closeListener)
    {
        using var listener = StartLoopbackListener();
        Task<HttpListenerContext>? accept = null;
        var disposedWithAcceptPending = false;
        var wait = OpenAICodexSubscriptionLoginManager.WaitForBrowserCallbackAsync(
            () => accept = listener.GetContextAsync(),
            () => throw new AssertFailedException("No cancellation was requested."),
            () =>
            {
                disposedWithAcceptPending = accept is null || !accept.IsCompleted;
                listener.Close();
            },
            (_, _) => throw new AssertFailedException("No callback was supplied."),
            CancellationToken.None, CancellationToken.None).AsTask();
        try
        {
            Assert.IsNotNull(accept);
            if (closeListener)
            {
                listener.Close();
            }
            else
            {
                listener.Stop();
            }

            var error = await Assert.ThrowsAsync<Exception>(() => wait.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(error is HttpListenerException or ObjectDisposedException);
            Assert.IsTrue(accept.IsFaulted);
            Assert.AreSame(accept.Exception?.InnerException, error);
            Assert.IsFalse(disposedWithAcceptPending);
            Assert.ThrowsExactly<ObjectDisposedException>(listener.Start);
        }
        finally
        {
            listener.Close();
            await Task.WhenAll(ObserveAsync(wait), ObserveAsync(accept ?? Task.CompletedTask)).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static HttpListener StartLoopbackListener()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
            return listener;
        }
        catch
        {
            listener.Close();
            throw;
        }
    }

    private static async Task ObserveAsync(Task task)
        => await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
}
