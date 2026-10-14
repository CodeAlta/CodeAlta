using System.Text.Json;

namespace CodeAlta.Agent.Claude;

// Prompt suggestions of Claude Code: started with `--prompt-suggestions`, the CLI asks the model once a turn ended
// what the user may write next, with a request of its own that reads the prompt cache of the conversation, and
// says it with a `prompt_suggestion` message. It comes after the result of the turn, when no run reads the CLI, and
// not after every turn: the CLI makes none for the first turn of a conversation, nor when it has nothing to say.
internal sealed partial class ClaudeCodeSession
{
    private Action<string>? _onPromptSuggestion;

    /// <summary>
    /// Registers what is called when the CLI suggested a prompt. The handler is called while the CLI is being read:
    /// it must not wait.
    /// </summary>
    public IDisposable OnPromptSuggestion(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            _onPromptSuggestion = handler;
        }

        return new PromptSuggestionRegistration(this, handler);
    }

    private sealed class PromptSuggestionRegistration(ClaudeCodeSession session, Action<string> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (session._gate)
            {
                if (ReferenceEquals(session._onPromptSuggestion, handler))
                {
                    session._onPromptSuggestion = null;
                }
            }
        }
    }

    private void ReadPromptSuggestion(JsonElement message)
    {
        if (ClaudeCodeJson.GetString(message, "suggestion") is not { Length: > 0 } suggestion)
        {
            return;
        }

        // A turn that goes on, one the CLI started by itself included, is not the one the suggestion answers.
        if (IsCliBusy)
        {
            return;
        }

        Action<string>? handler;
        lock (_gate)
        {
            handler = _onPromptSuggestion;
        }

        handler?.Invoke(suggestion);
    }
}
