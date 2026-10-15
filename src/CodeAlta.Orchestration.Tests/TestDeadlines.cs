namespace CodeAlta.Orchestration.Tests;

/// <summary>The bounds that tell a hang from a slow machine in the session runtime fixtures.</summary>
internal static class TestDeadlines
{
    /// <summary>
    /// How long one awaited operation of a fixture (a host start, a whole test body, a cleanup, a wait for a state) may take before the
    /// test reports a hang. These tests run 32 methods at a time and take up to 4 s each on an idle machine; a full solution run, with
    /// every test project and the browser tests at once, makes them several times slower. A bound of a few seconds reports that
    /// slowness as a failure: this one only reports what never ends.
    /// </summary>
    internal static readonly TimeSpan Hang = TimeSpan.FromSeconds(30);
}
