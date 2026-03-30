using CodeAlta.Catalog;
using CodeAlta.Models;

namespace CodeAlta.Presentation.Prompting
{
    internal readonly record struct PromptComposerProjection(
        string Placeholder,
        bool IsEnabled,
        bool CanSend,
        bool CanSteer,
        bool CanDelegate,
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
        public static PromptComposerProjection Build(
            WorkThreadDescriptor? selectedThread,
            ProjectDescriptor? selectedProject,
            bool globalScopeSelected,
            string backendDisplayName,
            ChatBackendAvailability availability,
            bool anyBackendReady,
            bool draftTabOpen,
            string? selectedThreadId,
            bool selectedThreadHasQueuedPrompts,
            bool selectedThreadCanAlwaysEnqueue,
            bool selectedThreadCanCompact,
            bool selectedThreadCanAbort)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(backendDisplayName);

            var isUnavailable = availability != ChatBackendAvailability.Ready;
            var placeholder = isUnavailable
                ? BuildPromptUnavailablePlaceholder(selectedThread, backendDisplayName, availability, anyBackendReady)
                : BuildPromptPlaceholder(selectedThread, selectedProject, globalScopeSelected);
            var unavailableStatusMessage = isUnavailable
                ? BuildPromptUnavailableStatusText(selectedThread, backendDisplayName, availability, anyBackendReady)
                : null;
            var unavailableStatusTone = availability == ChatBackendAvailability.Connecting
                ? StatusTone.Info
                : StatusTone.Warning;
            var hasThread = selectedThread is not null;

            return new PromptComposerProjection(
                Placeholder: placeholder,
                IsEnabled: !isUnavailable,
                CanSend: !isUnavailable,
                CanSteer: hasThread && !isUnavailable,
                CanDelegate: hasThread && !isUnavailable,
                CanAbort: hasThread && selectedThreadCanAbort,
                CanCompact: hasThread && selectedThreadCanCompact && !isUnavailable,
                CanCloseTab: hasThread || (draftTabOpen && string.IsNullOrWhiteSpace(selectedThreadId)),
                CanClearQueue: hasThread && selectedThreadHasQueuedPrompts,
                CanAlwaysEnqueue: hasThread && selectedThreadCanAlwaysEnqueue,
                UnavailableStatusMessage: unavailableStatusMessage,
                UnavailableStatusTone: unavailableStatusTone);
        }

        internal static string BuildPromptPlaceholder(
            WorkThreadDescriptor? thread,
            ProjectDescriptor? selectedProject,
            bool globalScopeSelected)
        {
            if (thread is not null)
            {
                return $"Continue '{thread.Title}'...";
            }

            if (globalScopeSelected)
            {
                return "Start a global thread...";
            }

            return selectedProject is null
                ? "Select a project to start a thread..."
                : $"Start a thread for {selectedProject.DisplayName}...";
        }

        internal static string BuildPromptUnavailablePlaceholder(
            WorkThreadDescriptor? thread,
            string backendDisplayName,
            ChatBackendAvailability availability,
            bool anyBackendReady)
        {
            if (thread is not null)
            {
                return availability == ChatBackendAvailability.Connecting
                    ? $"Waiting for {backendDisplayName} to reconnect..."
                    : $"'{thread.Title}' is unavailable until {backendDisplayName} is connected.";
            }

            if (availability == ChatBackendAvailability.Connecting)
            {
                return $"Connecting to {backendDisplayName}...";
            }

            return anyBackendReady
                ? "Select a connected backend to start a thread..."
                : "Install or connect Codex/Copilot to start a thread...";
        }

        internal static string BuildPromptUnavailableStatusText(
            WorkThreadDescriptor? thread,
            string backendDisplayName,
            ChatBackendAvailability availability,
            bool anyBackendReady)
        {
            if (thread is not null)
            {
                return availability == ChatBackendAvailability.Connecting
                    ? $"Reconnecting '{thread.Title}' to {backendDisplayName}. Prompt sending is temporarily unavailable."
                    : $"'{thread.Title}' is unavailable because {backendDisplayName} is not connected.";
            }

            if (availability == ChatBackendAvailability.Connecting)
            {
                return $"Connecting to {backendDisplayName}. Prompt sending will be available once the backend is ready.";
            }

            return anyBackendReady
                ? "Select a connected backend to send prompts."
                : "No chat backend is connected. Browse threads and projects, but prompt sending is unavailable.";
        }
    }
}
