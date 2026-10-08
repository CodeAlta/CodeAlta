using System.Text;
using System.Text.Json;
using CodeAlta.Agent;
using CodeAlta.Agent.Claude;
using CodeAlta.Orchestration.Hosting;
using CodeAlta.Orchestration.Runtime;

namespace CodeAlta.Tests;

/// <summary>
/// Runs the provider against a real Claude Code executable. The tests are skipped unless
/// <c>CODEALTA_TEST_CLAUDE_CLI</c> names one (<c>auto</c> for the one the provider finds by itself); the ones
/// that run a model turn also need
/// <c>CODEALTA_TEST_CLAUDE_TURN=1</c>, because they use the account the CLI is signed in to (a few short turns
/// of the smallest model). <c>CODEALTA_TEST_CLAUDE_TRACE=1</c> prints the lines exchanged with the CLI.
/// </summary>
[TestClass]
public sealed class ClaudeCodeLiveCliTests
{
    private const string Model = "haiku";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    [TestMethod]
    public async Task Probe_ListsTheModelsOfTheInstalledCli()
    {
        var command = RequireCli();
        await using var runtime = new ClaudeCodeModelProviderRuntime(CreateOptions(command));

        var probe = await runtime.ProbeAsync().WaitAsync(Timeout);

        Console.WriteLine($"{probe.Availability}: {probe.StatusMessage}");
        Console.WriteLine(string.Join(", ", probe.Models.Select(static model => $"{model.Id} ({model.DisplayName})")));
        Assert.IsTrue(probe.Models.Count > 0, "The CLI lists its models whether it is signed in or not.");
        Assert.IsTrue(probe.Availability is ModelProviderAvailability.Ready or ModelProviderAvailability.Failed);
        if (probe.Availability is ModelProviderAvailability.Failed)
        {
            StringAssert.Contains(probe.StatusMessage, "not signed in");
        }
    }

    [TestMethod]
    public async Task Turn_ReadsAFileAndAnswers()
    {
        using var live = await LiveSession.StartAsync();
        await File.WriteAllTextAsync(Path.Combine(live.Directory, "note.txt"), "The password of the test is 'marmalade'.\n");

        await live.SendAsync("Read note.txt and answer with the password it gives, in one word.");

        StringAssert.Contains(live.LastAnswer.ToLowerInvariant(), "marmalade");
        Assert.IsTrue(live.Events.OfType<AgentActivityEvent>().Any(static e => e is { Name: "Read", Phase: AgentActivityPhase.Completed }));
        Assert.IsTrue(live.Events.OfType<AgentContentDeltaEvent>().Any(static e => e.Kind == AgentContentKind.Assistant), "The answer is streamed.");
        var usage = live.Events.OfType<AgentSessionUpdateEvent>().Last(static e => e.Usage?.Window is not null).Usage!;
        Assert.IsTrue(usage.Window!.CurrentTokens > 0);
        Assert.IsTrue(usage.Window.TokenLimit > 0);
    }

    [TestMethod]
    public async Task Turn_AsksBeforeACommandAndShowsItsOutput()
    {
        using var live = await LiveSession.StartAsync();

        // Claude Code runs a command that only reads without asking: this one writes a file.
        await live.SendAsync("Run exactly this shell command with the Bash tool, in one call, and tell me what it prints: echo codealta-live-$((6*7)) > out.txt && cat out.txt");

        var permission = live.Permissions.OfType<AgentCommandPermissionRequest>().First();
        StringAssert.Contains(permission.Command, "codealta-live");
        Assert.AreEqual(Path.GetFullPath(live.Directory), Path.GetFullPath(permission.WorkingDirectory!));
        var output = live.Events.OfType<AgentContentCompletedEvent>().First(static e => e.Kind == AgentContentKind.ToolOutput);
        StringAssert.Contains(output.Content, "codealta-live-42");
        StringAssert.Contains(live.LastAnswer, "codealta-live-42");
        Assert.IsTrue(File.Exists(Path.Combine(live.Directory, "out.txt")));
    }

    [TestMethod]
    public async Task Turn_EditsAFileAndShowsTheChange()
    {
        using var live = await LiveSession.StartAsync();
        var file = Path.Combine(live.Directory, "note.txt");
        await File.WriteAllTextAsync(file, "The fruit is apple.\n");

        await live.SendAsync("In note.txt, replace the word apple with the word cherry. Use the Edit tool after reading the file.");

        StringAssert.Contains(await File.ReadAllTextAsync(file), "cherry");
        Assert.IsTrue(live.Permissions.OfType<AgentFileChangePermissionRequest>().Any(), "The edit asked the permission of CodeAlta.");
        var edit = live.Events.OfType<AgentActivityEvent>().Last(static e => e is { Name: "Edit" or "Write" or "MultiEdit", Phase: AgentActivityPhase.Completed });
        var diff = edit.Details!.Value.GetProperty("diff").GetString()!;
        StringAssert.Contains(diff, "-The fruit is apple.");
        StringAssert.Contains(diff, "+The fruit is cherry.");
    }

    [TestMethod]
    public async Task Turn_CallsAToolOfCodeAlta()
    {
        var word = $"zebra-{Guid.NewGuid():N}"[..14];
        var calls = 0;
        var tool = new AgentToolDefinition(
            new AgentToolSpec("secret_word", "Returns the secret word of this test.", JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone()),
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(new AgentToolResult(true, [new AgentToolResultItem.Text(word)]));
            });
        using var live = await LiveSession.StartAsync([tool]);

        await live.SendAsync("Call the secret_word tool once and answer with exactly the word it returns.");

        Assert.AreEqual(1, calls);
        StringAssert.Contains(live.LastAnswer, word);
        var activity = live.Events.OfType<AgentActivityEvent>().Last(static e => e.Phase == AgentActivityPhase.Completed);
        Assert.AreEqual("secret_word", activity.Name, "The session knows the tool by its own name.");
    }

    [TestMethod]
    public async Task Session_RemembersResumesAndStops()
    {
        using var live = await LiveSession.StartAsync();
        await live.SendAsync("Remember this word for later: kumquat. Answer with OK only.");

        // The same process answers the next turn with the context of the first.
        await live.SendAsync("Which word did I ask you to remember? Answer with the word only.");
        StringAssert.Contains(live.LastAnswer.ToLowerInvariant(), "kumquat");

        // A turn that is stopped leaves a session that still answers.
        var stopped = live.Session.SendAsync(new AgentSendOptions { Input = AgentInput.Text("Run this shell command with the Bash tool: sleep 60") });
        await live.WaitForAsync(static e => e is AgentActivityEvent { Name: "Bash", Phase: AgentActivityPhase.Started });
        await live.Session.AbortAsync().WaitAsync(Timeout);
        await Assert.ThrowsAsync<OperationCanceledException>(() => stopped.WaitAsync(Timeout));

        // Another runtime, as after a restart of CodeAlta, resumes the transcript of the CLI.
        var sessionId = live.Session.SessionId;
        await live.RestartAsync(sessionId);
        await live.SendAsync("Which word did I ask you to remember at the start? Answer with the word only.");
        StringAssert.Contains(live.LastAnswer.ToLowerInvariant(), "kumquat");
    }

    [TestMethod]
    public async Task Host_RunsATurnWithTheInstructionsOfCodeAlta()
    {
        // The whole composition of an application without a window: the registry of providers, the hub, the
        // session runtime and the instructions it composes for the session.
        var command = RequireCli();
        if (Environment.GetEnvironmentVariable("CODEALTA_TEST_CLAUDE_TURN") != "1")
        {
            Assert.Inconclusive("Set CODEALTA_TEST_CLAUDE_TURN=1 to run turns with the account of the CLI.");
        }

        using var directory = TestTempDirectory.Create();
        var globalRoot = Path.Combine(directory.Path, "global");
        var projectRoot = Path.Combine(directory.Path, "codealta-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(globalRoot);
        Directory.CreateDirectory(projectRoot);
        var options = CreateOptions(command);
        var providerId = new ModelProviderId(options.ProviderKey);
        try
        {
            await using var host = await CodeAltaHost.CreateAsync(
                new CodeAltaHostOptions
                {
                    GlobalRoot = globalRoot,
                    CurrentProjectPath = projectRoot,
                    IsHeadless = true,
                    HasInteractiveUi = false,
                    StartPlugins = false,
                    ConfigureModelProviders = registry => registry.RegisterOrReplace(
                        ClaudeCodeModelProviderRuntime.CreateDescriptor(options),
                        () => new ClaudeCodeModelProviderRuntime(options)),
                });
            var execution = new SessionExecutionOptions
            {
                ProviderId = providerId,
                ProviderKey = providerId.Value,
                WorkingDirectory = projectRoot,
                ProjectRoots = [projectRoot],
                Model = Model,
                // Only the instructions of CodeAlta say this: the answer shows that they reached Claude Code.
                AdditionalDeveloperInstructions = "The code word of this CodeAlta session is 'quince'.",
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            };
            var events = new List<AgentEvent>();
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var streaming = new CancellationTokenSource(Timeout);
            var stream = Task.Run(async () =>
            {
                await foreach (var runtimeEvent in host.RuntimeService.StreamEventsAsync(streaming.Token))
                {
                    if (runtimeEvent is not SessionAgentEvent { Event: var agentEvent })
                    {
                        continue;
                    }

                    lock (events)
                    {
                        events.Add(agentEvent);
                    }

                    if (agentEvent is AgentErrorEvent or AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle })
                    {
                        settled.TrySetResult();
                    }
                }
            });

            var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, execution, title: "Live Claude Code", CancellationToken.None);
            await host.RuntimeService.SendAsync(
                session,
                execution,
                new AgentSendOptions { Input = AgentInput.Text("What is the code word of this CodeAlta session? Answer with the word only.") },
                CancellationToken.None);
            await settled.Task.WaitAsync(Timeout);
            await streaming.CancelAsync();
            await stream.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

            AgentEvent[] snapshot;
            lock (events)
            {
                snapshot = [.. events];
            }

            foreach (var error in snapshot.OfType<AgentErrorEvent>())
            {
                Console.WriteLine($"error {error.Message}");
            }

            var answer = snapshot.OfType<AgentContentCompletedEvent>().Last(static e => e.Kind == AgentContentKind.Assistant).Content;
            Console.WriteLine($"answer {answer}");
            StringAssert.Contains(answer.ToLowerInvariant(), "quince");
            var prompt = snapshot.OfType<AgentSystemPromptEvent>().FirstOrDefault();
            Console.WriteLine($"instructions {prompt?.DeveloperInstructions?.Length} characters");
        }
        finally
        {
            RemoveTranscripts(projectRoot);
        }
    }

    [TestMethod]
    public async Task Host_ShowsTheTurnABackgroundCommandStarts()
    {
        // A command the model leaves running ends after its turn: the CLI then starts a turn by itself, which the
        // application shows as a run of the session.
        var command = RequireCli();
        if (Environment.GetEnvironmentVariable("CODEALTA_TEST_CLAUDE_TURN") != "1")
        {
            Assert.Inconclusive("Set CODEALTA_TEST_CLAUDE_TURN=1 to run turns with the account of the CLI.");
        }

        using var directory = TestTempDirectory.Create();
        var globalRoot = Path.Combine(directory.Path, "global");
        var projectRoot = Path.Combine(directory.Path, "codealta-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(globalRoot);
        Directory.CreateDirectory(projectRoot);
        var options = CreateOptions(command);
        var providerId = new ModelProviderId(options.ProviderKey);
        try
        {
            await using var host = await CodeAltaHost.CreateAsync(
                new CodeAltaHostOptions
                {
                    GlobalRoot = globalRoot,
                    CurrentProjectPath = projectRoot,
                    IsHeadless = true,
                    HasInteractiveUi = false,
                    StartPlugins = false,
                    ConfigureModelProviders = registry => registry.RegisterOrReplace(
                        ClaudeCodeModelProviderRuntime.CreateDescriptor(options),
                        () => new ClaudeCodeModelProviderRuntime(options)),
                });
            var execution = new SessionExecutionOptions
            {
                ProviderId = providerId,
                ProviderKey = providerId.Value,
                WorkingDirectory = projectRoot,
                ProjectRoots = [projectRoot],
                Model = Model,
                OnPermissionRequest = static (_, _) => Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce)),
            };
            var events = new List<AgentEvent>();
            using var streaming = new CancellationTokenSource(Timeout);
            var stream = Task.Run(async () =>
            {
                await foreach (var runtimeEvent in host.RuntimeService.StreamEventsAsync(streaming.Token))
                {
                    if (runtimeEvent is SessionAgentEvent { Event: var agentEvent })
                    {
                        lock (events)
                        {
                            events.Add(agentEvent);
                        }
                    }
                }
            });

            var session = await host.RuntimeService.CreateProjectSessionAsync(host.CurrentProject, execution, title: "Live Claude Code", CancellationToken.None);
            var firstRun = await host.RuntimeService.SendAsync(
                session,
                execution,
                new AgentSendOptions
                {
                    Input = AgentInput.Text(
                        "Run the shell command `sleep 8; echo finished` in the background (run_in_background: true). " +
                        "Then end your turn at once with the single word: started. " +
                        "When the command ends later, answer with the single word: ended."),
                },
                CancellationToken.None);

            AgentEvent[] shown = [];
            while (!streaming.IsCancellationRequested)
            {
                lock (events)
                {
                    shown = [.. events.Where(e => e.RunId is { } runId && runId != firstRun)];
                }

                if (shown.Any(static e => e is AgentErrorEvent or AgentSessionUpdateEvent { Kind: AgentSessionUpdateKind.Idle }))
                {
                    break;
                }

                await Task.Delay(100);
            }

            await streaming.CancelAsync();
            await stream.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            foreach (var content in shown.OfType<AgentContentCompletedEvent>())
            {
                Console.WriteLine($"content {content.Kind}: {content.Content}");
            }

            foreach (var error in shown.OfType<AgentErrorEvent>())
            {
                Console.WriteLine($"error {error.Message}");
            }

            StringAssert.StartsWith(shown.OfType<AgentContentCompletedEvent>().First(static e => e.Kind == AgentContentKind.User).Content, "Claude Code started a turn by itself");
            StringAssert.Contains(shown.OfType<AgentContentCompletedEvent>().Last(static e => e.Kind == AgentContentKind.Assistant).Content.ToLowerInvariant(), "ended");
        }
        finally
        {
            RemoveTranscripts(projectRoot);
        }
    }

    private static string RequireCli()
    {
        var command = Environment.GetEnvironmentVariable("CODEALTA_TEST_CLAUDE_CLI");
        if (string.IsNullOrWhiteSpace(command))
        {
            Assert.Inconclusive("Set CODEALTA_TEST_CLAUDE_CLI to the path of a Claude Code executable, or to `auto`, to run this test.");
        }

        return command!;
    }

    // Claude Code keeps the transcripts of a folder under its own profile, in a folder named after it. The folder
    // of a test has a unique name: what ends with it is what the test left there.
    private static void RemoveTranscripts(string directory)
    {
        var configuration = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var projects = Path.Combine(
            string.IsNullOrWhiteSpace(configuration) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") : configuration,
            "projects");
        var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));
        if (!Directory.Exists(projects) || !name.StartsWith("codealta-tests-", StringComparison.Ordinal) || name.Length < 40)
        {
            return;
        }

        foreach (var transcripts in Directory.EnumerateDirectories(projects, "*" + name))
        {
            try
            {
                Directory.Delete(transcripts, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static ClaudeCodeModelProviderRuntimeOptions CreateOptions(string command)
        => new()
        {
            ProviderKey = "claude-code",
            DisplayName = "Claude Code",
            Command = string.Equals(command, "auto", StringComparison.OrdinalIgnoreCase) ? null : command,
            IdleTimeout = TimeSpan.Zero,
            TransportFactory = Environment.GetEnvironmentVariable("CODEALTA_TEST_CLAUDE_TRACE") == "1" ? new TracingTransportFactory() : null,
        };

    // One session of CodeAlta on a real CLI, in its own folder.
    private sealed class LiveSession : IDisposable
    {
        private readonly TestTempDirectory _directory = TestTempDirectory.Create();
        private readonly List<AgentEvent> _events = [];
        private readonly List<AgentPermissionRequest> _permissions = [];
        private readonly string _command;
        private readonly IReadOnlyList<AgentToolDefinition>? _tools;
        private ClaudeCodeModelProviderRuntime? _runtime;

        private LiveSession(string command, IReadOnlyList<AgentToolDefinition>? tools)
        {
            _command = command;
            _tools = tools;
        }

        public string Directory => _directory.Path;

        public IAgentSession Session { get; private set; } = null!;

        public AgentEvent[] Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public AgentPermissionRequest[] Permissions
        {
            get
            {
                lock (_permissions)
                {
                    return [.. _permissions];
                }
            }
        }

        public string LastAnswer => Events.OfType<AgentContentCompletedEvent>().Last(static e => e.Kind == AgentContentKind.Assistant).Content;

        public static async Task<LiveSession> StartAsync(IReadOnlyList<AgentToolDefinition>? tools = null)
        {
            var command = RequireCli();
            if (Environment.GetEnvironmentVariable("CODEALTA_TEST_CLAUDE_TURN") != "1")
            {
                Assert.Inconclusive("Set CODEALTA_TEST_CLAUDE_TURN=1 to run turns with the account of the CLI.");
            }

            var live = new LiveSession(command, tools);
            live._runtime = new ClaudeCodeModelProviderRuntime(CreateOptions(command));
            var probe = await live._runtime.ProbeAsync().WaitAsync(Timeout);
            if (probe.Availability is not ModelProviderAvailability.Ready)
            {
                await live._runtime.DisposeAsync();
                live._directory.Dispose();
                Assert.Inconclusive($"The CLI cannot run a turn: {probe.StatusMessage}");
            }

            live.Attach(await live._runtime.CreateSessionAsync(live.CreateSessionOptions<AgentSessionCreateOptions>()));
            return live;
        }

        public async Task RestartAsync(string sessionId)
        {
            await Session.DisposeAsync();
            await _runtime!.DisposeAsync();
            _runtime = new ClaudeCodeModelProviderRuntime(CreateOptions(_command));
            Attach(await _runtime.ResumeSessionAsync(sessionId, CreateSessionOptions<AgentSessionResumeOptions>()));
        }

        public async Task SendAsync(string prompt)
        {
            var before = Events.Length;
            try
            {
                await Session.SendAsync(new AgentSendOptions { Input = AgentInput.Text(prompt) }).WaitAsync(Timeout);
            }
            finally
            {
                foreach (var @event in Events.Skip(before).Where(static e => e is not AgentContentDeltaEvent and not AgentRawEvent))
                {
                    Console.WriteLine(@event switch
                    {
                        AgentActivityEvent activity => $"activity {activity.Phase} {activity.Name} {activity.Message}",
                        AgentContentCompletedEvent content => $"content {content.Kind}: {Shorten(content.Content)}",
                        AgentSessionUpdateEvent update => $"update {update.Kind} {update.Message} window={update.Usage?.Window?.CurrentTokens}/{update.Usage?.Window?.TokenLimit} cost={update.Usage?.LastOperation?.Cost}",
                        AgentPermissionRequest permission => $"permission {permission.Kind}",
                        AgentErrorEvent error => $"error {error.Message}",
                        _ => @event.GetType().Name,
                    });
                }
            }
        }

        public Task WaitForAsync(Func<AgentEvent, bool> match)
            => Task.Run(async () =>
            {
                while (!Events.Any(match))
                {
                    await Task.Delay(50);
                }
            }).WaitAsync(Timeout);

        public void Dispose()
        {
            Session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _runtime?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            RemoveTranscripts(Directory);
            _directory.Dispose();
        }

        private void Attach(IAgentSession session)
        {
            Session = session;
            session.Subscribe(@event =>
            {
                lock (_events)
                {
                    _events.Add(@event);
                }
            });
        }

        private TOptions CreateSessionOptions<TOptions>()
            where TOptions : AgentSessionCreateOptions
        {
            AgentPermissionRequestHandler onPermission = (request, _) =>
            {
                lock (_permissions)
                {
                    _permissions.Add(request);
                }

                Console.WriteLine($"permission asked: {request.GetType().Name}");
                return Task.FromResult(new AgentPermissionDecision(AgentPermissionDecisionKind.AllowOnce));
            };
            AgentSessionCreateOptions options = typeof(TOptions) == typeof(AgentSessionResumeOptions)
                ? new AgentSessionResumeOptions { ProviderKey = "claude-code", WorkingDirectory = Directory, Model = Model, Tools = _tools, OnPermissionRequest = onPermission }
                : new AgentSessionCreateOptions { ProviderKey = "claude-code", WorkingDirectory = Directory, Model = Model, Tools = _tools, OnPermissionRequest = onPermission };
            return (TOptions)options;
        }

        private static string Shorten(string text)
            => text.Length <= 300 ? text.ReplaceLineEndings(" ") : text[..300].ReplaceLineEndings(" ") + " …";
    }

    // Prints what is exchanged with the CLI, to see what a version of it really writes.
    private sealed class TracingTransportFactory : IClaudeCodeTransportFactory
    {
        public IClaudeCodeTransport Start(ClaudeCodeLaunch launch)
        {
            Console.WriteLine($"[start] {string.Join(' ', launch.Arguments.Select(static argument => argument.Length > 120 ? argument[..120] + "…" : argument))}");
            return new TracingTransport(ClaudeCodeProcessTransportFactory.Instance.Start(launch));
        }
    }

    private sealed class TracingTransport(IClaudeCodeTransport inner) : IClaudeCodeTransport
    {
        public string StandardErrorTail => inner.StandardErrorTail;

        public int? ExitCode => inner.ExitCode;

        public ValueTask WriteLineAsync(ReadOnlyMemory<byte> utf8Line, CancellationToken cancellationToken)
        {
            Print("[in ] ", Encoding.UTF8.GetString(utf8Line.Span));
            return inner.WriteLineAsync(utf8Line, cancellationToken);
        }

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = await inner.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is not null)
            {
                Print("[out] ", line);
            }

            return line;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        // The account of the CLI is in its answer to `initialize`: that line is not printed.
        private static void Print(string prefix, string line)
        {
            if (line.Contains("\"account\"", StringComparison.Ordinal))
            {
                Console.WriteLine(prefix + "(initialize response)");
                return;
            }

            Console.WriteLine(prefix + (line.Length <= 700 ? line : line[..700] + " …"));
        }
    }
}
