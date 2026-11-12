namespace CodeAlta.Agent;

/// <summary>
/// A provider is registered but waits for something before it can run (<see cref="ModelProviderDescriptor.WaitsFor"/>), such as
/// its key kept in a locked vault. It is a <see cref="KeyNotFoundException"/>, so that what probes the providers sees it as one
/// that is not registered.
/// </summary>
public sealed class ModelProviderWaitingException : KeyNotFoundException
{
    /// <summary>Initializes a new instance of the <see cref="ModelProviderWaitingException"/> class.</summary>
    /// <param name="providerKey">The key of the provider.</param>
    /// <param name="message">What it waits for, in words for the user.</param>
    public ModelProviderWaitingException(string providerKey, string message)
        : base(message)
    {
        ProviderKey = providerKey;
    }

    /// <summary>Gets the key of the provider.</summary>
    public string ProviderKey { get; }
}
