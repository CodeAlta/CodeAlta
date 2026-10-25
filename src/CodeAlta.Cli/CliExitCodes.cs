namespace CodeAlta.Cli;

/// <summary>
/// The exit codes of <c>altacli</c>, the same whatever the output format: a script tells how a run ended from the code
/// alone, and the <c>_codealta/result</c> notification of <c>--format jsonl</c> repeats it with its status.
/// </summary>
internal static class CliExitCodes
{
    /// <summary>The run completed: the session and the sessions it started settled, and it answered.</summary>
    public const int Completed = 0;

    /// <summary>The run failed: a provider or a tool failed, or it ended without an answer.</summary>
    public const int Failed = 1;

    /// <summary>The command line or the configuration is not valid, or the session to resume does not exist.</summary>
    public const int InvalidUsage = 2;

    /// <summary>The run was aborted: SIGINT, SIGTERM or Ctrl+C.</summary>
    public const int Aborted = 3;

    /// <summary>The time given with <c>--timeout</c> ran out; the run was aborted.</summary>
    public const int Timeout = 4;

    /// <summary>Another process runs the session.</summary>
    public const int SessionInUse = 5;

    /// <summary>The provider or the model cannot be used: none is configured, a sign-in or a key is needed, the model is not offered.</summary>
    public const int ProviderNotReady = 6;

    /// <summary>The final answer is not the JSON value <c>--output-schema</c> asks for.</summary>
    public const int InvalidOutput = 7;
}
