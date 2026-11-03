namespace CodeAlta.Fleet.Update;

/// <summary>The codes of the failures of an update, stable for the callers that report them (the fleet update).</summary>
public static class UpdateErrorCodes
{
    /// <summary>The source is not a feed, or not a valid one.</summary>
    public const string InvalidSource = "update.invalid_source";

    /// <summary>The local configuration of the machine does not allow the source.</summary>
    public const string SourceNotAllowed = "update.source_not_allowed";

    /// <summary>The feed did not answer.</summary>
    public const string FeedUnavailable = "update.feed_unavailable";

    /// <summary>The feed has no such package or version.</summary>
    public const string NotFound = "update.not_found";

    /// <summary>The package could not be downloaded.</summary>
    public const string DownloadFailed = "update.download_failed";

    /// <summary>The package does not match its hash, or no hash could check it.</summary>
    public const string HashMismatch = "update.hash_mismatch";

    /// <summary>The package is not a runtime package of the product.</summary>
    public const string InvalidPackage = "update.invalid_package";

    /// <summary>No package is published for the OS or the architecture of this machine.</summary>
    public const string UnsupportedRuntime = "update.unsupported_runtime";

    /// <summary>The version was rolled back on this machine before.</summary>
    public const string BadVersion = "update.bad_version";

    /// <summary>The state of the versions could not be read or written.</summary>
    public const string StateUnavailable = "update.state_unavailable";

    /// <summary>The data could not be copied before a switch (the server's database): the switch does not happen.</summary>
    public const string BackupFailed = "update.backup_failed";

    /// <summary>Another process holds the lease of the launcher of the folder of versions.</summary>
    public const string LauncherBusy = "update.launcher_busy";

    /// <summary>The owner of the machine turned off the updates of the fleet (<c>"fleet": false</c> in <c>update.json</c>).</summary>
    public const string FleetRefused = "update.fleet_refused";
}

/// <summary>An update that could not be done, with a stable code (<see cref="UpdateErrorCodes"/>).</summary>
public sealed class UpdateException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="UpdateException"/> class.</summary>
    /// <param name="code">The code.</param>
    /// <param name="message">What went wrong, for a person.</param>
    /// <param name="innerException">The cause, or null.</param>
    public UpdateException(string code, string message, Exception? innerException = null) : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Gets the code of the failure.</summary>
    public string Code { get; }
}
