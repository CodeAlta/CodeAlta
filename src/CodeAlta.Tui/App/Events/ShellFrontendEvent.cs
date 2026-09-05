using CodeAlta.Tui.App.State;

namespace CodeAlta.Tui.App.Events;

internal abstract record ShellFrontendEvent;

internal sealed record CatalogChangedEvent : ShellFrontendEvent;

internal sealed record StartupCatalogProjectionReadyEvent : ShellFrontendEvent;

internal sealed record SelectionChangedEvent(ShellFrontendStateSnapshot? Snapshot = null) : ShellFrontendEvent;

internal sealed record OpenTabsChangedEvent(IReadOnlyList<ShellTabSnapshot> Tabs) : ShellFrontendEvent;

internal sealed record SelectedTabChangedEvent(ShellTabSnapshot? SelectedTab) : ShellFrontendEvent;

internal sealed record HeaderChangedEvent : ShellFrontendEvent;

internal sealed record ShellChromeChangedEvent : ShellFrontendEvent;

internal sealed record SessionStatusChangedEvent(string SessionId) : ShellFrontendEvent;

internal sealed record PromptDraftChangedEvent(string PromptSessionId) : ShellFrontendEvent;

internal sealed record PromptImagesChangedEvent(string PromptSessionId) : ShellFrontendEvent;

internal sealed record PromptAvailabilityChangedEvent : ShellFrontendEvent;

internal sealed record PromptFocusRequestedEvent : ShellFrontendEvent;

internal sealed record QueuedPromptListChangedEvent(string SessionId) : ShellFrontendEvent;

internal sealed record AskQueueChangedEvent(string SessionId) : ShellFrontendEvent;

internal sealed record SessionUsageChangedEvent(string SessionId) : ShellFrontendEvent;

internal sealed record ModelProviderStateChangedEvent(string ModelProviderId) : ShellFrontendEvent;

internal sealed record ModelProviderCatalogChangedEvent : ShellFrontendEvent;

internal sealed record RuntimeTimelineChangedEvent(string SessionId) : ShellFrontendEvent;
