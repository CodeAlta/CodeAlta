using CodeAlta.LiveTool;
using NeoAstra.Rpc;

namespace CodeAlta.Desktop.Rpc;

// The Test of a decision provider (type `typesafe`): it runs no session, so the registry of the model providers does
// not hold it and their probe does not reach it. Its test lists the models of its account, which is not charged.
internal sealed partial class GlobalConfigService
{
    private const int MaximumDecisionModels = 32;

    /// <summary>The decision providers the window consults; null when the host has none.</summary>
    internal Func<IAltaDecisionService?>? Decisions { get; init; }

    /// <summary>
    /// Tests one saved decision provider: its key and its endpoint, by the list of the models of its account.
    /// </summary>
    /// <returns><c>ok</c> with the models, <c>failed</c> with the reason, <c>unavailable</c> or <c>stale_epoch</c>.</returns>
    [NeoRpcMethod("testDecisionProvider")]
    public async Task<GlobalConfigTestDecisionProviderResponse> TestDecisionProvider(GlobalConfigTestDecisionProviderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Decisions?.Invoke() is not { } decisions) return new("unavailable", null, []);
        if (!string.Equals(request.ExpectedEpoch, _epoch, StringComparison.Ordinal)) return new("stale_epoch", null, []);
        var key = request.Key?.Trim();
        if (string.IsNullOrEmpty(key) || key.Length > 64) return new("invalid", null, []);
        try
        {
            var (_, models) = await decisions.ListModelsAsync(key, cancellationToken).ConfigureAwait(false);
            return new("ok", null, [.. models.Take(MaximumDecisionModels).Select(static model => Bound(model.Name)!)]);
        }
        catch (AltaDecisionException exception)
        {
            return new("failed", Bound(exception.Message), []);
        }
    }
}

/// <summary>Asks to test a saved decision provider by its key.</summary>
internal sealed record GlobalConfigTestDecisionProviderRequest(string? ExpectedEpoch, string? Key);

/// <summary>The test of a decision provider: its status, the reason of a failure, and the models its account can ask for.</summary>
internal sealed record GlobalConfigTestDecisionProviderResponse(string Status, string? Message, IReadOnlyList<string> Models);
