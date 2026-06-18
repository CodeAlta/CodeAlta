namespace CodeAlta.App;

internal interface IKnownProjectImporter
{
    Task<bool> ImportAsync(CancellationToken cancellationToken);
}
