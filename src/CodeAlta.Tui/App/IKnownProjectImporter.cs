namespace CodeAlta.Tui.App;

internal interface IKnownProjectImporter
{
    Task<bool> ImportAsync(CancellationToken cancellationToken);
}
