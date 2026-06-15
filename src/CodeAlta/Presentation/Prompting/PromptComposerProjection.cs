using CodeAlta.Agent;
using CodeAlta.Catalog;
using CodeAlta.Models;

namespace CodeAlta.Presentation.Prompting
{
    internal readonly record struct PromptComposerProjection(
        string Placeholder,
        bool IsEnabled,
        bool CanSend,
        bool CanSteer,
        bool CanAbort,
        bool CanCompact,
        bool CanCloseTab,
        bool CanClearQueue,
        bool CanAlwaysEnqueue,
        string? UnavailableStatusMessage,
        StatusTone UnavailableStatusTone)
    {
        public bool HasUnavailableStatus
            => !string.IsNullOrWhiteSpace(UnavailableStatusMessage);
    }

    internal static class PromptComposerProjectionBuilder
    {
        internal static string BuildDefaultPromptPlaceholder()
            => BuildReadyPromptPlaceholder(isContinuation: false, hasProjectContext: false);

        public static PromptComposerProjection Build(
            SessionViewDescriptor? selectedSession,
            ProjectDescriptor? selectedProject,
            bool globalScopeSelected,
            string providerDisplayName,
            ModelProviderAvailability availability,
            bool anyProviderReady,
            bool draftTabOpen,
            int openTabCount,
            string? selectedSessionId,
            bool selectedSessionHasQueuedPrompts,
            bool selectedSessionCanAlwaysEnqueue,
            bool selectedSessionCanCompact,
            bool selectedSessionCanAbort,
            IReadOnlyList<string>? promptPlaceholderContributions = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(providerDisplayName);

            var isUnavailable = availability != ModelProviderAvailability.Ready;
            var placeholder = isUnavailable
                ? BuildPromptUnavailablePlaceholder(selectedSession, providerDisplayName, availability, anyProviderReady)
                : BuildPromptPlaceholder(selectedSession, selectedProject, globalScopeSelected, promptPlaceholderContributions);
            var unavailableStatusMessage = isUnavailable
                ? BuildPromptUnavailableStatusText(selectedSession, providerDisplayName, availability, anyProviderReady)
                : null;
            var unavailableStatusTone = availability == ModelProviderAvailability.Probing
                ? StatusTone.Info
                : StatusTone.Warning;
            var hasSession = selectedSession is not null;

            return new PromptComposerProjection(
                Placeholder: placeholder,
                IsEnabled: !isUnavailable,
                CanSend: !isUnavailable,
                CanSteer: hasSession && !isUnavailable,
                CanAbort: hasSession && selectedSessionCanAbort,
                CanCompact: hasSession && selectedSessionCanCompact && !isUnavailable,
                CanCloseTab: hasSession || (draftTabOpen && string.IsNullOrWhiteSpace(selectedSessionId) && openTabCount > 1),
                CanClearQueue: hasSession && selectedSessionHasQueuedPrompts,
                CanAlwaysEnqueue: hasSession && selectedSessionCanAlwaysEnqueue,
                UnavailableStatusMessage: unavailableStatusMessage,
                UnavailableStatusTone: unavailableStatusTone);
        }

        internal static string BuildPromptPlaceholder(
            SessionViewDescriptor? session,
            ProjectDescriptor? selectedProject,
            bool globalScopeSelected,
            IReadOnlyList<string>? promptPlaceholderContributions = null)
        {
            var hasProjectContext =
                session?.Kind == SessionViewKind.ProjectSession ||
                (!globalScopeSelected && selectedProject is not null);

            return BuildReadyPromptPlaceholder(session is not null, hasProjectContext, promptPlaceholderContributions);
        }

        internal static string BuildPromptUnavailablePlaceholder(
            SessionViewDescriptor? session,
            string providerDisplayName,
            ModelProviderAvailability availability,
            bool anyProviderReady)
        {
            if (session is not null)
            {
                return availability == ModelProviderAvailability.Probing
                    ? SR.T("Waiting for {0} to reconnect...", providerDisplayName)
                    : SR.T("'{0}' is unavailable until {1} is connected.", session.Title, providerDisplayName);
            }

            if (availability == ModelProviderAvailability.Probing)
            {
                return SR.T("Connecting to {0}...", providerDisplayName);
            }

            return anyProviderReady
                ? SR.T("Select a connected provider to start a session...")
                : SR.T("Configure model providers (Ctrl+G Ctrl+R) to start a session...");
        }

        internal static string BuildPromptUnavailableStatusText(
            SessionViewDescriptor? session,
            string providerDisplayName,
            ModelProviderAvailability availability,
            bool anyProviderReady)
        {
            if (session is not null)
            {
                return availability == ModelProviderAvailability.Probing
                    ? SR.T("Reconnecting session '{0}' to {1}. Prompt sending is temporarily unavailable.", session.Title, providerDisplayName)
                    : SR.T("'{0}' is unavailable because {1} is not connected.", session.Title, providerDisplayName);
            }

            if (availability == ModelProviderAvailability.Probing)
            {
                return SR.T("Connecting to {0}. Prompt sending will be available once the provider is ready.", providerDisplayName);
            }

            return anyProviderReady
                ? SR.T("Select a connected provider to send prompts.")
                : SR.T("No model provider is ready. Open Model Providers (Ctrl+G Ctrl+R) to configure one.");
        }

        private static string BuildReadyPromptPlaceholder(
            bool isContinuation,
            bool hasProjectContext,
            IReadOnlyList<string>? promptPlaceholderContributions = null)
        {
            var action = isContinuation
                ? SR.T("Continue the selected session.")
                : SR.T("Start a session.");

            return $"{action} {BuildReadyPromptGuidance(hasProjectContext, promptPlaceholderContributions)}";
        }

        private static string BuildReadyPromptGuidance(bool hasProjectContext, IReadOnlyList<string>? promptPlaceholderContributions)
        {
            var contributionSegments = promptPlaceholderContributions?
                .Where(static segment => !string.IsNullOrWhiteSpace(segment))
                .Select(static segment => segment.Trim())
                .ToArray() ?? [];
            var segments = new List<string>(6 + contributionSegments.Length)
            {
                SR.T("[/] commands"),
                SR.T("[?] help"),
            };
            if (hasProjectContext)
            {
                segments.Add(SR.T("[@] to reference a project file"));
            }

            segments.AddRange(contributionSegments);

            segments.Add(SR.T("[ENTER] to send"));
            segments.Add(SR.T("[SHIFT+ENTER] for new line"));
            segments.Add(SR.T("[CTRL+ENTER] to steer"));
            return string.Join(", ", segments) + ".";
        }
    }
}
