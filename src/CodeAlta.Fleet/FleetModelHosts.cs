using CodeAlta.Catalog;
using CodeAlta.Catalog.Secrets;
using CodeAlta.ModelServers;

namespace CodeAlta.Fleet;

/// <summary>
/// The model host of an instance of CodeAlta: its builds, downloads and settings under <c>~/.alta/llama</c>, shared by the
/// instances of the profile, and its servers under <c>&lt;state root&gt;/llama</c>, so that a developer instance has its own.
/// The token of Hugging Face is the <c>HF_TOKEN</c> secret of the machine, else its environment.
/// </summary>
public static class FleetModelHosts
{
    /// <summary>Creates the model host of an instance; <see cref="ModelHost.StartAsync"/> starts it.</summary>
    /// <param name="catalog">The roots of the instance.</param>
    /// <param name="serversOutliveHost">Whether its servers go on when it stops (a node, whose switches of version must not reload the models).</param>
    /// <returns>The model host.</returns>
    public static ModelHost Create(CatalogOptions catalog, bool serversOutliveHost)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var root = catalog.GlobalRoot;
        return new ModelHost(new ModelHostOptions
        {
            SharedRoot = Path.Combine(root, "llama"),
            InstanceRoot = Path.Combine(catalog.StateRoot, "llama"),
            ServersOutliveHost = serversOutliveHost,
            HuggingFaceToken = () => HuggingFaceToken(root),
            KeepAwake = CodeAlta.Core.Power.SleepAssertion.Acquire,
        });
    }

    private static string? HuggingFaceToken(string root)
    {
        try
        {
            if (SecretReferenceResolver.ForMachine(root).TryResolve("${secret:HF_TOKEN}", out var token, out _) && token.Length > 0) return token;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }

        return Environment.GetEnvironmentVariable("HF_TOKEN");
    }
}
