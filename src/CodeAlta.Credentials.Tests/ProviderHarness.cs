using CodeAlta.Plugins.Abstractions;

namespace CodeAlta.Credentials.Tests;

// Creates a built-in credential provider straight from its contribution, with the settings a test gives, and checks what it
// shows and what the fake program was given.
internal static class ProviderHarness
{
    public static ICredentialProvider Create(PluginCredentialProviderContribution contribution, IReadOnlyDictionary<string, object?> settings, List<string>? masked = null)
        => contribution.Create(new PluginCredentialProviderContext("test", settings) { Mask = masked is null ? static _ => { } : masked.Add });

    public static async Task<CredentialProviderException> ReadFailsAsync(ICredentialProvider provider, string reference, bool interactive = true)
    {
        try
        {
            await provider.ReadAsync(reference, interactive, CancellationToken.None);
        }
        catch (CredentialProviderException exception)
        {
            return exception;
        }

        Assert.Fail($"{reference} was read.");
        throw new InvalidOperationException();
    }

    public static async Task<CredentialProviderException> BrowseFailsAsync(ICredentialProvider provider, string? parent)
    {
        try
        {
            await provider.BrowseAsync(new CredentialBrowseRequest(parent), CancellationToken.None);
        }
        catch (CredentialProviderException exception)
        {
            return exception;
        }

        Assert.Fail($"{parent} was listed.");
        throw new InvalidOperationException();
    }

    // Every text of a page: the labels, ids, references and details of its entries, as the picker would get them.
    public static void AssertHoldsNone(CredentialBrowsePage page, params string[] values)
    {
        var texts = page.Entries.Select(static entry => entry.ToString()).Append(page.ToString()).Append(page.Path ?? string.Empty).ToArray();
        foreach (var value in values)
        {
            Assert.IsFalse(texts.Any(text => text.Contains(value, StringComparison.Ordinal)), $"the page holds the value {value}: {string.Join(" | ", texts)}");
        }
    }

    public static void AssertNoArgumentHolds(FakeVault vault, params string[] secrets)
    {
        var calls = vault.Calls();
        foreach (var secret in secrets)
        {
            Assert.IsFalse(calls.Any(call => call.Contains(secret, StringComparison.Ordinal)), $"a secret is in the arguments: {string.Join(" | ", calls)}");
        }
    }
}
