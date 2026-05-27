using CodeAlta.Agent;
using CodeAlta.Agent.Runtime;

namespace CodeAlta.Tests;

internal static class ModelProviderRuntimeTestExtensions
{
    public static void RegisterOrReplaceSessionRuntime(
        this ModelProviderRegistry registry,
        ModelProviderDescriptor descriptor,
        Func<ITestModelProviderSessionRuntime> createRuntime)
    {
        registry.RegisterOrReplace(descriptor, () => new TestSessionRuntimeAdapter(descriptor, createRuntime()));
    }

    public static async Task<IReadOnlyList<AgentModelInfo>> ListModelsAsync(
        this IModelProviderRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        var probe = await runtime.ProbeAsync(cancellationToken).ConfigureAwait(false);
        return probe.Models;
    }

    public static async Task<IAgentSession> CreateSessionAsync(
        this IAgentModelProviderRuntime runtime,
        AgentSessionCreateOptions options,
        CancellationToken cancellationToken = default)
    {
        var sessionRuntime = CreateSessionRuntime(runtime, options.WorkingDirectory);
        await TryPrimeModelCacheAsync(sessionRuntime, cancellationToken).ConfigureAwait(false);
        return await sessionRuntime.CreateSessionAsync(options, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IAgentSession> ResumeSessionAsync(
        this IAgentModelProviderRuntime runtime,
        string sessionId,
        AgentSessionResumeOptions options,
        CancellationToken cancellationToken = default)
    {
        var stateRootPath = options.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(stateRootPath))
        {
            stateRootPath = FindStateRootForSession(sessionId);
        }

        var sessionRuntime = CreateSessionRuntime(runtime, stateRootPath);
        await TryPrimeModelCacheAsync(sessionRuntime, cancellationToken).ConfigureAwait(false);
        return await sessionRuntime.ResumeSessionAsync(sessionId, options, cancellationToken).ConfigureAwait(false);
    }

    private static AgentRuntime CreateSessionRuntime(IAgentModelProviderRuntime runtime, string? stateRootPath)
        => new(
            runtime.Descriptor.ProviderId,
            runtime.Descriptor.DisplayName,
            new AgentRuntimeOptions
            {
                StateRootPath = stateRootPath,
                Providers = [runtime.CreateProviderRegistration()],
            });

    private static async Task TryPrimeModelCacheAsync(AgentRuntime runtime, CancellationToken cancellationToken)
    {
        try
        {
            await runtime.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
        }
    }

    private static string? FindStateRootForSession(string sessionId)
    {
        foreach (var sessionsDirectory in Directory.EnumerateDirectories(AppContext.BaseDirectory, "sessions", SearchOption.AllDirectories))
        {
            if (Directory.Exists(Path.Combine(sessionsDirectory, sessionId)))
            {
                return Directory.GetParent(sessionsDirectory)?.FullName;
            }
        }

        return null;
    }

    private sealed class TestSessionRuntimeAdapter : IModelProviderSessionRuntime
    {
        private readonly ITestModelProviderSessionRuntime _runtime;

        public TestSessionRuntimeAdapter(ModelProviderDescriptor descriptor, ITestModelProviderSessionRuntime runtime)
        {
            Descriptor = descriptor;
            _runtime = runtime;
        }

        public ModelProviderDescriptor Descriptor { get; }

        public Task StartAsync(CancellationToken cancellationToken = default) => _runtime.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default) => _runtime.StopAsync(cancellationToken);

        public async Task<ModelProviderProbeResult> ProbeAsync(CancellationToken cancellationToken = default)
        {
            var models = await _runtime.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            return new ModelProviderProbeResult
            {
                ProviderId = Descriptor.ProviderId,
                Availability = ModelProviderAvailability.Ready,
                Models = models,
            };
        }

        public IModelProviderTurnExecutor CreateTurnExecutor()
            => throw new NotSupportedException();

        public Task<IAgentSession> CreateSessionAsync(AgentSessionCreateOptions options, CancellationToken cancellationToken = default)
            => _runtime.CreateSessionAsync(options, cancellationToken);

        public Task<IAgentSession> ResumeSessionAsync(string sessionId, AgentSessionResumeOptions options, CancellationToken cancellationToken = default)
            => _runtime.ResumeSessionAsync(sessionId, options, cancellationToken);

        public ValueTask DisposeAsync() => _runtime.DisposeAsync();
    }
}
