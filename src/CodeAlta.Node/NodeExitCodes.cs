namespace CodeAlta.Node;

/// <summary>The exit codes of <c>altanode</c>: a service manager or a script tells how a command ended from the code alone.</summary>
public static class NodeExitCodes
{
    /// <summary>The command succeeded: <c>run</c> stopped cleanly, <c>status</c> found an instance on the profile.</summary>
    public const int Success = 0;

    /// <summary>The command failed; for <c>status</c>, no instance holds the profile.</summary>
    public const int Failure = 1;

    /// <summary>The command line is not valid.</summary>
    public const int InvalidUsage = 2;

    /// <summary>Another CodeAlta instance (Desktop, TUI or a node) holds the profile: <c>run</c> does not start.</summary>
    public const int ProfileInUse = 3;

    /// <summary>The command exists but is not implemented yet.</summary>
    public const int NotImplemented = 4;
}
