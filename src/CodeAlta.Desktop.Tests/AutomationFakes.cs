using System.Collections.Concurrent;
using CodeAlta.Desktop.Automations;
using CodeAlta.Plugin.Git;

namespace CodeAlta.Desktop.Tests;

// What the tests of the automations put in place of the network, the clock and the sessions.

internal sealed class FakeAutomationFeed : IAutomationFeed
{
    internal GitRepositoryReference Repository { get; } = GitRepositoryReference.GitHub("org", "repo");

    internal List<GitFeedItem> Issues { get; } = [];

    internal List<GitFeedItem> PullRequests { get; } = [];

    /// <summary>What every reading answers, instead of the lists.</summary>
    internal GitFeedPage? Answer { get; set; }

    internal string? EntityTag { get; set; }

    /// <summary>What is answered about an item that does not say who can write.</summary>
    internal bool? Trust { get; set; }

    internal List<(string Directory, GitFeedKind Kind, string? Tag)> Reads { get; } = [];

    /// <summary>What happens while a list is being read, such as the user pausing the automations.</summary>
    internal Action? OnRead { get; set; }

    public ValueTask<GitFeedPage> ReadAsync(string directory, GitFeedKind kind, string? entityTag, CancellationToken cancellationToken)
    {
        Reads.Add((directory, kind, entityTag));
        OnRead?.Invoke();
        if (Answer is { } answer) return new(answer);
        if (EntityTag is not null && entityTag == EntityTag) return new(new GitFeedPage(GitFeedStatus.NotModified, Repository, [], entityTag));
        return new(new GitFeedPage(GitFeedStatus.Ok, Repository, [.. kind == GitFeedKind.Issues ? Issues : PullRequests], EntityTag));
    }

    public ValueTask<bool?> IsTrustedAsync(GitRepositoryReference repository, GitFeedItem item, CancellationToken cancellationToken)
        => new(item.Trusted ?? Trust);
}

internal sealed class AutomationClock : TimeProvider
{
    private long _ticks;

    public DateTimeOffset Now
    {
        get => new(Volatile.Read(ref _ticks), TimeSpan.Zero);
        set => Volatile.Write(ref _ticks, value.UtcTicks);
    }

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeAutomationRunner : IAutomationRunner
{
    private readonly ConcurrentQueue<FakeAutomationStart> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);

    internal ConcurrentBag<FakeAutomationStart> Starts { get; } = [];

    internal string? Refuse { get; set; }

    public Task<AutomationStart> StartAsync(AutomationEntry entry, string runId, string prompt, string? detail, CancellationToken cancellationToken)
    {
        var start = new FakeAutomationStart(entry, runId, prompt) { Detail = detail };
        Starts.Add(start);
        _pending.Enqueue(start);
        _signal.Release();
        return Task.FromResult(Refuse is { } problem ? new AutomationStart(null, problem, null) : new AutomationStart("session-" + Starts.Count, null, start.Completion));
    }

    internal async Task<FakeAutomationStart> NextAsync()
    {
        Assert.IsTrue(await _signal.WaitAsync(TimeSpan.FromSeconds(10)), "No run was started.");
        Assert.IsTrue(_pending.TryDequeue(out var start));
        return start!;
    }
}

internal sealed class FakeAutomationStart(AutomationEntry entry, string runId, string prompt)
{
    private readonly TaskCompletionSource<AutomationOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal AutomationEntry Entry => entry;

    internal string RunId => runId;

    internal string Prompt => prompt;

    internal string? Detail { get; init; }

    internal Task<AutomationOutcome> Completion => _completion.Task;

    internal void Complete(AutomationOutcome outcome) => _completion.TrySetResult(outcome);
}

internal sealed class AutomationTempRoot : IDisposable
{
    internal string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodeAlta-automations-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
