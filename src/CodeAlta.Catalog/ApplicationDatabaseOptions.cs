namespace CodeAlta.Catalog;

/// <summary>
/// Options of an <see cref="ApplicationDatabase"/>.
/// </summary>
public sealed record ApplicationDatabaseOptions
{
    /// <summary>Gets the path of the database file; its folder is created.</summary>
    public required string DatabasePath { get; init; }

    /// <summary>
    /// Gets the path of the file the session list lived in before the application database existed. When it exists
    /// and <see cref="DatabasePath"/> does not, it is moved there with its log files.
    /// </summary>
    public string? LegacyDatabasePath { get; init; }

    /// <summary>Gets the folder of the copies; the default is <c>backups</c> beside the database.</summary>
    public string? BackupDirectory { get; init; }

    /// <summary>Gets how long an operation waits for a lock another connection or process holds; the default is 5 seconds.</summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets how long a write may hold the queue before a warning is logged; the default is 2 seconds.</summary>
    public TimeSpan SlowWriteThreshold { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets the age after which the newest copy is replaced by a new one; the default is one day.</summary>
    public TimeSpan BackupInterval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Gets the number of copies kept; the default is 2.</summary>
    public int BackupsToKeep { get; init; } = 2;

    /// <summary>
    /// Gets how long the upkeep waits after <see cref="ApplicationDatabase.StartMaintenance"/> before its first
    /// check; the default is 20 seconds, so that it never competes with the start of the application.
    /// </summary>
    public TimeSpan MaintenanceStartDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Gets the time between two checks of the upkeep; the default is one hour.</summary>
    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Gets the clock; tests give their own.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
