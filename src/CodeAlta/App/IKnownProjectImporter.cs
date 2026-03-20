namespace CodeAlta.App;

internal interface IKnownProjectImporter
{
    Task ImportAsync(CancellationToken cancellationToken);
}