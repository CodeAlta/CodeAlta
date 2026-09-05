namespace CodeAlta.Tui.Views;

internal sealed record ModelProviderSelectorController(
    Action<int> SelectProvider,
    Action<int> SelectModel,
    Action<int> SelectReasoning,
    Action CompactSession,
    Action OpenModels)
{
    public static ModelProviderSelectorController Create(
        Action<int> selectProvider,
        Action<int> selectModel,
        Action<int> selectReasoning,
        Action compactSession)
        => Create(selectProvider, selectModel, selectReasoning, compactSession, static () => { });

    public static ModelProviderSelectorController Create(
        Action<int> selectProvider,
        Action<int> selectModel,
        Action<int> selectReasoning,
        Action compactSession,
        Action openModels)
    {
        ArgumentNullException.ThrowIfNull(selectProvider);
        ArgumentNullException.ThrowIfNull(selectModel);
        ArgumentNullException.ThrowIfNull(selectReasoning);
        ArgumentNullException.ThrowIfNull(compactSession);
        ArgumentNullException.ThrowIfNull(openModels);
        return new ModelProviderSelectorController(selectProvider, selectModel, selectReasoning, compactSession, openModels);
    }
}
