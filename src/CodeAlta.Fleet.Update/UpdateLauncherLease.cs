namespace CodeAlta.Fleet.Update;

/// <summary>
/// The exclusive lease of the launcher of a folder of versions (<see cref="UpdateVersionStore.TryAcquireLauncherLease"/>):
/// while a process holds it, no other selects a version at start or starts a version, so two launchers never start the
/// same version on trial nor roll back one the other runs. It is held from the start of <c>run</c> to its end, across the
/// selection, the version that runs, its confirmation and its rollback.
/// </summary>
public sealed class UpdateLauncherLease : IDisposable
{
    private readonly FileStream _lock;

    internal UpdateLauncherLease(FileStream stream) => _lock = stream;

    /// <summary>Releases the lease.</summary>
    public void Dispose() => _lock.Dispose();
}
