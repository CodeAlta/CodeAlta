using CodeAlta.Agent;
using CodeAlta.Agent.ModelCatalog;
using CodeAlta.Catalog;

namespace CodeAlta.Hosting;

/// <summary>
/// Applies cached-provider inspection policy and probes directly owned temporary configured runtimes.
/// </summary>
/// <remarks>
/// The caller owns active-state lookup, localization, progress and the choice of state root. This class
/// does not discover a profile, own a registry, refresh metadata, or change active provider state.
/// Synchronous message factories belong to the frontend and must not access controls or mutate UI state.
/// No synchronization-context suppression is applied to awaits or message callbacks.
/// </remarks>
public static class ConfiguredProviderInspection
{
    // Mandatory, call-scoped seam: tests never fall back to concrete provider factories.
    internal delegate bool TryCreateProviderRuntime(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out IModelProviderRuntime runtime);

    /// <summary>
    /// Tries to handle a connectivity test using an already looked-up active provider's state.
    /// </summary>
    /// <param name="availability">Ready succeeds; Probing, Failed and Unsupported return a cached failure. Other values are unhandled.</param>
    /// <param name="statusMessage">Borrowed status text returned unchanged for a cached failure, including empty text.</param>
    /// <param name="models">Borrowed models; only their count is read for Ready. No models are enumerated or copied.</param>
    /// <param name="formatReady">Synchronous frontend-owned formatter invoked only for Ready, with its model count.</param>
    /// <param name="result">The handled result, or the default sentinel when this method returns false.</param>
    /// <returns>Whether the cached state handles this test; false is not a failed test result.</returns>
    /// <exception cref="ArgumentNullException">The status text, models or formatter is null.</exception>
    /// <remarks>There is no configuration comparison or cancellation check. Model-count and formatter exceptions propagate unchanged.</remarks>
    public static bool TryBuildActiveProviderTestResult(
        ModelProviderAvailability availability,
        string statusMessage,
        IReadOnlyList<AgentModelInfo> models,
        Func<int, string> formatReady,
        out ProviderInspectionTestResult result)
    {
        ArgumentNullException.ThrowIfNull(statusMessage);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(formatReady);

        result = default;
        switch (availability)
        {
            case ModelProviderAvailability.Ready:
                result = new ProviderInspectionTestResult(
                    true,
                    formatReady(models.Count),
                    models.Count);
                return true;
            case ModelProviderAvailability.Probing:
            case ModelProviderAvailability.Failed:
            case ModelProviderAvailability.Unsupported:
                result = new ProviderInspectionTestResult(false, statusMessage, 0);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Tries to handle model listing using an already looked-up Ready provider's models.
    /// </summary>
    /// <param name="availability">Only Ready is handled, unlike cached connectivity testing.</param>
    /// <param name="models">The borrowed list returned unchanged and unsorted, even if the caller's configuration requests sorting.</param>
    /// <param name="formatReady">Synchronous frontend-owned formatter invoked only for Ready, with its model count.</param>
    /// <param name="result">The handled result, or the default sentinel when this method returns false.</param>
    /// <returns>Whether the Ready cache handles this request; false requires the caller to choose its fallback.</returns>
    /// <exception cref="ArgumentNullException">The models or formatter is null.</exception>
    /// <remarks>There is no configuration comparison or cancellation check. Model-count and formatter exceptions propagate unchanged.</remarks>
    public static bool TryBuildActiveProviderModelListResult(
        ModelProviderAvailability availability,
        IReadOnlyList<AgentModelInfo> models,
        Func<int, string> formatReady,
        out ProviderInspectionModelListResult result)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(formatReady);

        result = default;
        if (availability != ModelProviderAvailability.Ready)
        {
            return false;
        }

        result = new ProviderInspectionModelListResult(
            true,
            formatReady(models.Count),
            models);
        return true;
    }

    /// <summary>
    /// Tests one uncached configured provider by directly creating, probing and asynchronously disposing a temporary runtime.
    /// </summary>
    /// <param name="definition">Borrowed definition passed unchanged to the configured-provider builder; it is not saved.</param>
    /// <param name="stateRootPath">Required nonblank caller-supplied root forwarded unchanged. This class does not resolve a default root or create its directory.</param>
    /// <param name="modelCatalog">Optional borrowed metadata service. The caller owns refresh/disposal and must keep it alive through runtime disposal.</param>
    /// <param name="formatInvalidSettings">Synchronous frontend-owned formatter invoked only when registration is rejected.</param>
    /// <param name="formatSuccess">Synchronous frontend-owned formatter invoked with the probe model count before runtime disposal.</param>
    /// <param name="cancellationToken">Passed to ProbeAsync only; there is no early cancellation check or cancellable disposal.</param>
    /// <returns>A rejected-settings result or successful completed-probe result, including empty or non-Ready probes.</returns>
    /// <exception cref="ArgumentNullException">The definition, root or either formatter is null.</exception>
    /// <exception cref="ArgumentException">The root is blank or configured-provider conversion rejects a value.</exception>
    /// <exception cref="OperationCanceledException">The probe observes cancellation, unless disposal throws another exception.</exception>
    /// <remarks>
    /// No cache lookup, registry registration, explicit StartAsync or StopAsync occurs. Registration can read
    /// configured credential environment variables and shipped defaults; runtime creation/probing can perform
    /// provider I/O. Builder/factory exceptions propagate without an acquired runtime to dispose. Once acquired,
    /// disposal is awaited on success, probe failure, cancellation or formatting failure; a disposal exception
    /// takes precedence. Returned probe availability/diagnostics and requested model sorting are ignored.
    /// All other builder, factory, probe, model-count, formatter and disposal exceptions propagate unchanged.
    /// </remarks>
    public static async Task<ProviderInspectionTestResult> TestProviderAsync(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        Func<string> formatInvalidSettings,
        Func<int, string> formatSuccess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);
        ArgumentNullException.ThrowIfNull(formatInvalidSettings);
        ArgumentNullException.ThrowIfNull(formatSuccess);

        return await TestProviderAsync(definition, stateRootPath, modelCatalog, TryCreateRuntime,
            formatInvalidSettings, formatSuccess, cancellationToken);
    }

    /// <summary>
    /// Lists one uncached configured provider's models using a directly owned temporary runtime.
    /// </summary>
    /// <param name="definition">Borrowed definition; SortModels is read after probing and applies only to this uncached path.</param>
    /// <param name="stateRootPath">Required nonblank caller-supplied root forwarded unchanged, without default-root discovery or directory creation by this class.</param>
    /// <param name="modelCatalog">Optional borrowed metadata service that the caller keeps alive through temporary-runtime disposal.</param>
    /// <param name="formatInvalidSettings">Synchronous frontend-owned formatter invoked only when registration is rejected.</param>
    /// <param name="formatSuccess">Synchronous frontend-owned formatter invoked after optional sorting, before disposal, with the model count.</param>
    /// <param name="cancellationToken">Passed to ProbeAsync only; even a cancelled token does not prevent factory acquisition or rejection results.</param>
    /// <returns>A rejected-settings result or successful probe list, including empty or non-Ready probes.</returns>
    /// <exception cref="ArgumentNullException">The definition, root or either formatter is null.</exception>
    /// <exception cref="ArgumentException">The root is blank or configured-provider conversion rejects a value.</exception>
    /// <exception cref="OperationCanceledException">The probe observes cancellation, unless disposal throws another exception.</exception>
    /// <remarks>
    /// No registry, cache lookup, explicit StartAsync or StopAsync is used. Registration may read configured
    /// credential environment variables/defaults; creation and probing may perform provider I/O. The runtime is
    /// asynchronously disposed after acquisition on every exit, including probe, sorting and formatting failures.
    /// Disposal is awaited without a token and its exception takes precedence over a pending result/exception.
    /// Other builder, factory, probe, model-list, sorting and formatter exceptions propagate unchanged.
    /// Probe readiness/diagnostics are ignored. Sorting is stable ordinal-ignore-case by DisplayName ?? Id, then Id,
    /// and creates an array; otherwise the original borrowed list is returned, without a snapshot or lifetime guarantee.
    /// </remarks>
    public static async Task<ProviderInspectionModelListResult> ListProviderModelsAsync(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        Func<string> formatInvalidSettings,
        Func<int, string> formatSuccess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);
        ArgumentNullException.ThrowIfNull(formatInvalidSettings);
        ArgumentNullException.ThrowIfNull(formatSuccess);

        return await ListProviderModelsAsync(definition, stateRootPath, modelCatalog, TryCreateRuntime,
            formatInvalidSettings, formatSuccess, cancellationToken);
    }

    internal static async Task<ProviderInspectionTestResult> TestProviderAsync(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        TryCreateProviderRuntime tryCreateRuntime,
        Func<string> formatInvalidSettings,
        Func<int, string> formatSuccess,
        CancellationToken cancellationToken)
    {
        if (!tryCreateRuntime(definition, stateRootPath, modelCatalog, out var runtime))
        {
            return new ProviderInspectionTestResult(false, formatInvalidSettings(), 0);
        }

        await using var _ = runtime;
        var probe = await runtime.ProbeAsync(cancellationToken);
        var models = probe.Models;
        return new ProviderInspectionTestResult(true, formatSuccess(models.Count), models.Count);
    }

    internal static async Task<ProviderInspectionModelListResult> ListProviderModelsAsync(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        TryCreateProviderRuntime tryCreateRuntime,
        Func<string> formatInvalidSettings,
        Func<int, string> formatSuccess,
        CancellationToken cancellationToken)
    {
        if (!tryCreateRuntime(definition, stateRootPath, modelCatalog, out var runtime))
        {
            return new ProviderInspectionModelListResult(false, formatInvalidSettings(), []);
        }

        await using var _ = runtime;
        var probe = await runtime.ProbeAsync(cancellationToken);
        var models = SortModelsIfRequested(probe.Models, definition.SortModels == true);
        return new ProviderInspectionModelListResult(true, formatSuccess(models.Count), models);
    }

    private static bool TryCreateRuntime(
        CodeAltaProviderDocument definition,
        string stateRootPath,
        ModelsDevCatalogService? modelCatalog,
        out IModelProviderRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);

        if (!ConfiguredModelProviderRegistryBuilder.TryCreateProviderRegistration(definition, stateRootPath, modelCatalog, out _, out var createRuntime))
        {
            runtime = null!;
            return false;
        }

        runtime = createRuntime();
        return true;
    }

    private static IReadOnlyList<AgentModelInfo> SortModelsIfRequested(IReadOnlyList<AgentModelInfo> models, bool sortModels)
        => sortModels
            ? models
                .OrderBy(static model => model.DisplayName ?? model.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : models;
}
