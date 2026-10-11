using CodeAlta.Catalog;

namespace CodeAlta.Hosting.Tests;

[TestClass]
public sealed class ConfiguredCodexSubscriptionTests
{
    [TestMethod]
    [DataRow("login")]
    [DataRow("signout")]
    [DataRow("test")]
    [DataRow("metadata")]
    public async Task Operations_ForwardOriginalDefinitionRootAndToken(string operation)
    {
        var definition = new CodeAltaProviderDocument { ProviderType = "codex", ProviderKey = "configured" };
        Func<string> root = () => throw new AssertFailedException("Core must defer root selection to the operation.");
        Func<string> mismatch = () => throw new AssertFailedException("Matching type must not format an error.");
        var token = new CancellationToken(true);
        var metadata = new CodexAccountMetadata("Workspace", "subject", "issued-client", false);
        var called = false;

        void Check(CodeAltaProviderDocument actual, Func<string> actualRoot, CancellationToken actualToken)
        {
            called = true;
            Assert.AreSame(definition, actual);
            Assert.AreSame(root, actualRoot);
            Assert.AreEqual(token, actualToken);
        }

        switch (operation)
        {
            case "login":
                Action<Uri> report = _ => { };
                Action<Uri> open = _ => { };
                var login = await ConfiguredCodexAuthentication.LoginWithBrowserAsync(definition, root, mismatch, report, open,
                    (actual, actualRoot) => (actualReport, actualOpen, actualToken) =>
                    {
                        Check(actual, actualRoot, actualToken);
                        Assert.AreSame(report, actualReport);
                        Assert.AreSame(open, actualOpen);
                        return Task.FromResult(metadata);
                    }, token);
                Assert.AreSame(metadata, login);
                Assert.IsFalse(login.HasPlanUsagePermission);
                break;
            case "signout":
                Assert.IsFalse(await ConfiguredCodexAuthentication.SignOutAsync(definition, root, mismatch,
                    (actual, actualRoot) => actualToken =>
                    {
                        Check(actual, actualRoot, actualToken);
                        return ValueTask.FromResult(false);
                    }, token));
                break;
            case "test":
                Assert.AreSame(metadata, await ConfiguredCodexAuthentication.TestAuthenticationAsync(definition, root, mismatch,
                    (actual, actualRoot) => actualToken =>
                    {
                        Check(actual, actualRoot, actualToken);
                        return Task.FromResult(metadata);
                    }, token));
                break;
            case "metadata":
                // Like main, local registration lookup adds no provider-type or configured-account override.
                definition.ProviderType = "other";
                Assert.AreSame(metadata, await ConfiguredCodexAuthentication.ReadAccountMetadataAsync(definition, root,
                    (actual, actualRoot) => actualToken =>
                    {
                        Check(actual, actualRoot, actualToken);
                        return Task.FromResult<CodexAccountMetadata?>(metadata);
                    }, token));
                break;
        }

        Assert.IsTrue(called);
    }

    [TestMethod]
    [DataRow(false, "fault", false)]
    [DataRow(true, "fault", false)]
    [DataRow(false, "cancel", false)]
    [DataRow(true, "cancel", false)]
    [DataRow(false, "success", false)]
    [DataRow(true, "success", false)]
    [DataRow(false, "fault", true)]
    [DataRow(true, "fault", true)]
    public async Task PresentationFailure_StopsAndJoinsCallbackBeforeReleasingLogin(
        bool failOpening, string callbackOutcome, bool stopFails)
    {
        var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presentationFailure = new InvalidOperationException("synthetic presentation failure");
        var callbackFailure = new IOException("synthetic late callback failure");
        var uri = new Uri("https://example.invalid/authorize");
        var calls = new List<string>();
        var released = false;

        async Task RunAsync()
        {
            try
            {
                await ConfiguredCodexAuthentication.PresentAuthorizationAndWaitAsync(uri, callback.Task,
                    actual =>
                    {
                        Assert.AreSame(uri, actual);
                        calls.Add("report");
                        if (!failOpening) throw presentationFailure;
                    },
                    actual =>
                    {
                        Assert.AreSame(uri, actual);
                        calls.Add("open");
                        throw presentationFailure;
                    },
                    () =>
                    {
                        calls.Add("stop");
                        if (stopFails) throw new ObjectDisposedException("synthetic listener");
                    });
            }
            finally { released = true; }
        }

        var operation = RunAsync();
        try
        {
            CollectionAssert.AreEqual(failOpening ? new[] { "report", "open", "stop" } : ["report", "stop"], calls);
            Assert.IsFalse(operation.IsCompleted, "Presentation failure must still join the original callback waiter.");
            Assert.IsFalse(released, "The enclosing login scope cannot be released while its waiter is pending.");
            switch (callbackOutcome)
            {
                case "fault": callback.SetException(callbackFailure); break;
                case "cancel": callback.SetCanceled(new CancellationToken(true)); break;
                default: callback.SetResult(); break;
            }
            var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreSame(presentationFailure, actual, "Secondary stop/wait failures must not replace the presentation failure.");
            Assert.IsTrue(released);
        }
        finally
        {
            callback.TrySetResult();
            await Task.WhenAll(ObserveAsync(callback.Task), ObserveAsync(operation));
        }

        async Task ObserveAsync(Task task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception failure) when (ReferenceEquals(failure, presentationFailure)
                || ReferenceEquals(failure, callbackFailure) || failure is OperationCanceledException) { }
        }
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("fault")]
    [DataRow("cancel")]
    public async Task PresentedAuthorization_PreservesOrderAndOriginalCallbackOutcome(string callbackOutcome)
    {
        var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("synthetic callback failure");
        var token = new CancellationToken(true);
        var uri = new Uri("https://example.invalid/authorize");
        var calls = new List<string>();
        var operation = ConfiguredCodexAuthentication.PresentAuthorizationAndWaitAsync(uri, callback.Task,
            actual => { Assert.AreSame(uri, actual); calls.Add("report"); },
            actual => { Assert.AreSame(uri, actual); calls.Add("open"); },
            () => calls.Add("stop"));
        try
        {
            CollectionAssert.AreEqual(new[] { "report", "open" }, calls);
            Assert.IsFalse(operation.IsCompleted);
            switch (callbackOutcome)
            {
                case "fault":
                    callback.SetException(failure);
                    Assert.AreSame(failure, await Assert.ThrowsExactlyAsync<IOException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5))));
                    break;
                case "cancel":
                    callback.SetCanceled(token);
                    var canceled = await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.AreEqual(token, canceled.CancellationToken);
                    break;
                default:
                    callback.SetResult();
                    await operation.WaitAsync(TimeSpan.FromSeconds(5));
                    break;
            }
            CollectionAssert.AreEqual(new[] { "report", "open" }, calls, "Only failed presentation stops the waiter.");
        }
        finally
        {
            callback.TrySetResult();
            await Task.WhenAll(ObserveAsync(callback.Task), ObserveAsync(operation));
        }

        async Task ObserveAsync(Task task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception actual) when (ReferenceEquals(actual, failure) || actual is OperationCanceledException) { }
        }
    }

    [TestMethod]
    [DataRow("login")]
    [DataRow("signout")]
    [DataRow("test")]
    public async Task ProviderType_IsExactAndCheckedBeforeFactory(string operation)
    {
        var definition = new CodeAltaProviderDocument { ProviderType = "Codex" };
        Func<string> root = () => throw new AssertFailedException("Root must not be read.");
        Func<string> mismatch = () => "Select a Codex provider first.";
        Task Invoke() => operation switch
        {
            "login" => ConfiguredCodexAuthentication.LoginWithBrowserAsync(definition, root, mismatch, _ => { }, _ => { },
                (_, _) => throw new AssertFailedException("Factory must not run."), CancellationToken.None),
            "signout" => ConfiguredCodexAuthentication.SignOutAsync(definition, root, mismatch,
                (_, _) => throw new AssertFailedException("Factory must not run."), CancellationToken.None),
            _ => ConfiguredCodexAuthentication.TestAuthenticationAsync(definition, root, mismatch,
                (_, _) => throw new AssertFailedException("Factory must not run."), CancellationToken.None),
        };

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(Invoke);
        Assert.AreEqual("Select a Codex provider first.", failure.Message);
    }

    [TestMethod]
    public async Task PublicGuards_FailBeforeProductionConstruction()
    {
        var definition = new CodeAltaProviderDocument { ProviderType = "other" };
        Func<string> root = () => throw new AssertFailedException("Root must not run.");
        Func<string> mismatch = () => throw new AssertFailedException("Formatter must not run.");
        Action<Uri> callback = _ => throw new AssertFailedException("Callback must not run.");
        var cases = new (string Parameter, Func<Task> Invoke)[]
        {
            ("definition", () => ConfiguredCodexAuthentication.LoginWithBrowserAsync(null!, root, mismatch, callback, callback, CancellationToken.None)),
            ("getStateRootPath", () => ConfiguredCodexAuthentication.LoginWithBrowserAsync(definition, null!, mismatch, callback, callback, CancellationToken.None)),
            ("formatInvalidProvider", () => ConfiguredCodexAuthentication.LoginWithBrowserAsync(definition, root, null!, callback, callback, CancellationToken.None)),
            ("reportAuthorization", () => ConfiguredCodexAuthentication.LoginWithBrowserAsync(definition, root, mismatch, null!, callback, CancellationToken.None)),
            ("openBrowser", () => ConfiguredCodexAuthentication.LoginWithBrowserAsync(definition, root, mismatch, callback, null!, CancellationToken.None)),
            ("definition", () => ConfiguredCodexAuthentication.SignOutAsync(null!, root, mismatch, CancellationToken.None)),
            ("getStateRootPath", () => ConfiguredCodexAuthentication.SignOutAsync(definition, null!, mismatch, CancellationToken.None)),
            ("formatInvalidProvider", () => ConfiguredCodexAuthentication.SignOutAsync(definition, root, null!, CancellationToken.None)),
            ("definition", () => ConfiguredCodexAuthentication.TestAuthenticationAsync(null!, root, mismatch, CancellationToken.None)),
            ("getStateRootPath", () => ConfiguredCodexAuthentication.TestAuthenticationAsync(definition, null!, mismatch, CancellationToken.None)),
            ("formatInvalidProvider", () => ConfiguredCodexAuthentication.TestAuthenticationAsync(definition, root, null!, CancellationToken.None)),
            ("definition", () => ConfiguredCodexAuthentication.ReadAccountMetadataAsync(null!, root, CancellationToken.None)),
            ("getStateRootPath", () => ConfiguredCodexAuthentication.ReadAccountMetadataAsync(definition, null!, CancellationToken.None)),
        };
        foreach (var (parameter, invoke) in cases)
        {
            var failure = await Assert.ThrowsExactlyAsync<ArgumentNullException>(invoke);
            Assert.AreEqual(parameter, failure.ParamName);
        }
    }

    [TestMethod]
    [DataRow("login", false)]
    [DataRow("signout", false)]
    [DataRow("test", false)]
    [DataRow("metadata", false)]
    [DataRow("login", true)]
    [DataRow("signout", true)]
    [DataRow("test", true)]
    [DataRow("metadata", true)]
    public async Task Operations_PropagateFactoryAndOperationFailures(string operation, bool asynchronous)
    {
        var definition = new CodeAltaProviderDocument { ProviderType = "codex" };
        var failure = new InvalidOperationException("synthetic provider failure");
        Func<string> root = () => throw new AssertFailedException("Root must not run.");
        Task Invoke() => operation switch
        {
            "login" => ConfiguredCodexAuthentication.LoginWithBrowserAsync(definition, root, () => "wrong", _ => { }, _ => { },
                (_, _) => asynchronous ? (_, _, _) => Task.FromException<CodexAccountMetadata>(failure) : throw failure, CancellationToken.None),
            "signout" => ConfiguredCodexAuthentication.SignOutAsync(definition, root, () => "wrong",
                (_, _) => asynchronous ? _ => ValueTask.FromException<bool>(failure) : throw failure, CancellationToken.None),
            "test" => ConfiguredCodexAuthentication.TestAuthenticationAsync(definition, root, () => "wrong",
                (_, _) => asynchronous ? _ => Task.FromException<CodexAccountMetadata>(failure) : throw failure, CancellationToken.None),
            _ => ConfiguredCodexAuthentication.ReadAccountMetadataAsync(definition, root,
                (_, _) => asynchronous ? _ => Task.FromException<CodexAccountMetadata?>(failure) : throw failure, CancellationToken.None),
        };

        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(Invoke);
        Assert.AreSame(failure, actual);
    }

    [TestMethod]
    [DataRow("login")]
    [DataRow("signout")]
    [DataRow("test")]
    [DataRow("metadata")]
    public async Task InternalFactory_IsRequiredBeforeTypeSelection(string operation)
    {
        var definition = new CodeAltaProviderDocument { ProviderType = "other" };
        Func<string> callback = () => throw new AssertFailedException("Callback must not run.");
        Task Invoke() => operation switch
        {
            "login" => ConfiguredCodexAuthentication.LoginWithBrowserAsync(definition, callback, callback, _ => { }, _ => { }, null!, CancellationToken.None),
            "signout" => ConfiguredCodexAuthentication.SignOutAsync(definition, callback, callback, null!, CancellationToken.None),
            "test" => ConfiguredCodexAuthentication.TestAuthenticationAsync(definition, callback, callback, null!, CancellationToken.None),
            _ => ConfiguredCodexAuthentication.ReadAccountMetadataAsync(definition, callback, null!, CancellationToken.None),
        };

        var actual = await Assert.ThrowsExactlyAsync<ArgumentNullException>(Invoke);
        Assert.AreEqual("createOperation", actual.ParamName);
    }

    [TestMethod]
    [DataRow("login")]
    [DataRow("signout")]
    [DataRow("test")]
    [DataRow("metadata")]
    public async Task Operations_PreserveProviderCancellationToken(string operation)
    {
        var definition = new CodeAltaProviderDocument { ProviderType = "codex" };
        var token = new CancellationToken(true);
        Task Invoke() => operation switch
        {
            "login" => ConfiguredCodexAuthentication.LoginWithBrowserAsync(definition, () => "root", () => "wrong", _ => { }, _ => { },
                (_, _) => (_, _, _) => Task.FromCanceled<CodexAccountMetadata>(token), CancellationToken.None),
            "signout" => ConfiguredCodexAuthentication.SignOutAsync(definition, () => "root", () => "wrong",
                (_, _) => _ => new ValueTask<bool>(Task.FromCanceled<bool>(token)), CancellationToken.None),
            "test" => ConfiguredCodexAuthentication.TestAuthenticationAsync(definition, () => "root", () => "wrong",
                (_, _) => _ => Task.FromCanceled<CodexAccountMetadata>(token), CancellationToken.None),
            _ => ConfiguredCodexAuthentication.ReadAccountMetadataAsync(definition, () => "root",
                (_, _) => _ => Task.FromCanceled<CodexAccountMetadata?>(token), CancellationToken.None),
        };

        var actual = await Assert.ThrowsExactlyAsync<TaskCanceledException>(Invoke);
        Assert.AreEqual(token, actual.CancellationToken);
    }

    [TestMethod]
    [DataRow("login")]
    [DataRow("test")]
    public async Task Authentication_AwaitsOriginalOperationAndPreservesFailure(string operation)
    {
        var original = new TaskCompletionSource<CodexAccountMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("synthetic provider failure");
        var definition = new CodeAltaProviderDocument { ProviderType = "codex" };
        var route = operation == "login"
            ? ConfiguredCodexAuthentication.LoginWithBrowserAsync(definition, () => "synthetic root", () => "wrong provider",
                _ => { }, _ => { }, (_, _) => (_, _, _) => original.Task, CancellationToken.None)
            : ConfiguredCodexAuthentication.TestAuthenticationAsync(definition, () => "synthetic root", () => "wrong provider",
                (_, _) => _ => original.Task, CancellationToken.None);
        try
        {
            Assert.IsFalse(route.IsCompleted);
            original.SetException(failure);
            var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => route.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreSame(failure, actual);
        }
        finally
        {
            original.TrySetException(failure);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => original.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => route.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [TestMethod]
    public async Task Metadata_AwaitsOriginalReadBeforeReportingMissingRegistration()
    {
        var original = new TaskCompletionSource<CodexAccountMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var route = ConfiguredCodexAuthentication.ReadAccountMetadataAsync(new CodeAltaProviderDocument(), () => "root",
            (_, _) => _ => original.Task, CancellationToken.None);
        try
        {
            Assert.IsFalse(route.IsCompleted);
            original.SetResult(null);
            Assert.IsNull(await route.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            original.TrySetResult(null);
            await Task.WhenAll(original.Task, route).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
