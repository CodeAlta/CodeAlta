using CodeAlta.Plugin.Statistics.Query;
using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Plugin.Statistics.Tests;

[TestClass]
public sealed class ProjectDirectoryTests
{
    [TestMethod]
    public async Task A_read_that_fails_at_the_start_of_the_host_is_made_again_after_a_few_seconds()
    {
        var clock = new ManualClock();
        var alta = new ScriptedAlta { Ready = false };
        var directory = new AltaProjectDirectory(alta, clock);

        Assert.AreEqual(0, (await directory.ListProjectsAsync()).Count, "The commands are not ready yet.");
        Assert.AreEqual(0, (await directory.ListSpacesAsync()).Count);
        var calls = alta.Calls;

        alta.Ready = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, (await directory.ListProjectsAsync()).Count, "A failed read is not repeated at every call.");
        Assert.AreEqual(calls, alta.Calls);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, (await directory.ListProjectsAsync()).Count, "A few seconds later the commands are read again.");
        Assert.AreEqual(2, (await directory.ListSpacesAsync()).Count);
    }

    [TestMethod]
    public async Task A_read_that_throws_is_not_the_end_of_the_directory()
    {
        var clock = new ManualClock();
        var alta = new ScriptedAlta { Ready = false, Throws = true };
        var directory = new AltaProjectDirectory(alta, clock);
        Assert.AreEqual(0, (await directory.ListProjectsAsync()).Count);

        alta.Ready = true;
        alta.Throws = false;
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.AreEqual(2, (await directory.ListProjectsAsync()).Count);
    }

    [TestMethod]
    public async Task A_complete_read_is_kept_for_a_long_time_and_survives_a_later_failure()
    {
        var clock = new ManualClock();
        var alta = new ScriptedAlta { Ready = true };
        var directory = new AltaProjectDirectory(alta, clock);
        Assert.AreEqual(2, (await directory.ListProjectsAsync()).Count);
        var calls = alta.Calls;

        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.AreEqual(2, (await directory.ListProjectsAsync()).Count);
        Assert.AreEqual(calls, alta.Calls, "Within the cache time nothing is read.");

        alta.Ready = false;
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.AreEqual(2, (await directory.ListProjectsAsync()).Count, "What was found stays when the next read fails.");
        Assert.AreEqual(2, (await directory.ListSpacesAsync()).Count);
    }

    [TestMethod]
    public async Task The_providers_are_read_with_the_name_the_host_shows_each_under()
    {
        var directory = new AltaProjectDirectory(new ScriptedAlta { Ready = true }, new ManualClock());

        var providers = await directory.ListProvidersAsync();

        CollectionAssert.AreEqual(new[] { "claude-code=Claude Code", "codex=Codex", "bare=bare" }, providers.Select(static provider => $"{provider.Key}={provider.Name}").ToArray(), "a provider without a name is shown under its key");
        Assert.AreEqual(2, (await directory.ListProjectsAsync()).Count, "the projects and the spaces are read as before");
        Assert.AreEqual(2, (await directory.ListSpacesAsync()).Count);
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan time) => _now += time;
    }

    private sealed class ScriptedAlta : IPluginAltaService
    {
        public bool Ready { get; set; }

        public bool Throws { get; set; }

        public int Calls { get; private set; }

        public ValueTask<PluginAltaCommandResult> InvokeAsync(IReadOnlyList<string> args, string? stdin = null, PluginAltaInvocationOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (!Ready)
            {
                return Throws ? throw new InvalidOperationException("The host is starting.") : ValueTask.FromResult(new PluginAltaCommandResult { ExitCode = 1, TranscriptJsonl = string.Empty });
            }

            var text = string.Join(' ', args);
            var transcript = text.StartsWith("project list", StringComparison.Ordinal)
                ? "{\"type\":\"alta.project.item\",\"projectId\":\"project-0\",\"slug\":\"alpha\",\"displayName\":\"Alpha\",\"spaces\":[\"work\"]}\n{\"type\":\"alta.project.item\",\"projectId\":\"project-1\",\"slug\":\"beta\",\"displayName\":\"Beta\",\"spaces\":[]}\n"
                : text.StartsWith("provider list", StringComparison.Ordinal)
                    ? "{\"type\":\"alta.provider.item\",\"providerKey\":\"claude-code\",\"displayName\":\"Claude Code\"}\n{\"type\":\"alta.provider.item\",\"providerKey\":\"codex\",\"displayName\":\"Codex\"}\n{\"type\":\"alta.provider.item\",\"providerKey\":\"bare\"}\n"
                    : "{\"type\":\"alta.space.item\",\"id\":\"default\",\"name\":\"Default\",\"default\":true}\n{\"type\":\"alta.space.item\",\"id\":\"work\",\"name\":\"Work\",\"default\":false}\n";
            return ValueTask.FromResult(new PluginAltaCommandResult { ExitCode = 0, TranscriptJsonl = transcript });
        }
    }
}
