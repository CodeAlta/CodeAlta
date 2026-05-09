namespace CodeAlta.LiveTool;

/// <summary>
/// Stable exit codes for the in-process <c>alta</c> command surface.
/// </summary>
public static class AltaExitCodes
{
    /// <summary>Command completed successfully.</summary>
    public const int Success = 0;

    /// <summary>Command failed because of a runtime, validation, or backend error.</summary>
    public const int Failure = 1;

    /// <summary>Command-line parse or usage error.</summary>
    public const int Usage = 2;

    /// <summary>Target project, session, skill, or command was not found.</summary>
    public const int NotFound = 3;

    /// <summary>Permission or policy denied.</summary>
    public const int PolicyDenied = 4;

    /// <summary>Required in-process service or context is unavailable.</summary>
    public const int ServiceUnavailable = 5;

    /// <summary>Timeout or cancellation.</summary>
    public const int TimeoutOrCancellation = 6;

    /// <summary>Unsupported capability in the current backend or runtime.</summary>
    public const int Unsupported = 7;
}
