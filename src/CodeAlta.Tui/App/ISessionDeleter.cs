using CodeAlta.Catalog;

namespace CodeAlta.Tui.App;

internal interface ISessionDeleter
{
    Task<bool> DeleteSessionAsync(SessionViewDescriptor session, CancellationToken cancellationToken);
}
