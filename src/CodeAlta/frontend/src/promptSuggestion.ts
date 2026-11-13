import type { SessionRuntimeStateEntry, SessionRuntimeStateResponse } from "#neoastra";

/** A suggestion longer than this is not one somebody would send as it is. */
const maximumPromptSuggestionLength = 256;

/** A suggestion and the run whose answer made it, when the host says it: a later run may suggest the same text. */
export type PromptSuggestion = { text: string; runId: string | null };

/**
 * What the provider of a session suggested as the next prompt, once its last turn ended (Claude Code's prompt
 * suggestion): none while a run goes on, for an attachment that ends, and for what is not one line of text.
 */
export function entryPromptSuggestion(entry: SessionRuntimeStateEntry | null | undefined): string | null {
  if (!entry || entry.isTerminated || entry.isRetiring || entry.activeRunId) return null;
  return promptSuggestionText(entry.promptSuggestion);
}

/**
 * The suggestion a state of a session says, with its run: the one of its entry, or, for a session without one (detached
 * once idle, or not attached since the host started), the one its journal keeps.
 */
export function observedPromptSuggestion(state: SessionRuntimeStateResponse | null | undefined): PromptSuggestion | null {
  if (!state) return null;
  if (state.entry) {
    const text = entryPromptSuggestion(state.entry);
    return text ? { text, runId: runIdentity(state.entry.promptSuggestionRunId) } : null;
  }
  const text = promptSuggestionText(state.promptSuggestion);
  return text ? { text, runId: runIdentity(state.promptSuggestionRunId) } : null;
}

/** A suggestion as a prompt can take it: one line of text, trimmed; null for anything else. */
export function promptSuggestionText(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const text = value.trim();
  return text.length > 0 && text.length <= maximumPromptSuggestionLength && !/[\p{Cc}\p{Zl}\p{Zp}]/u.test(text) ? text : null;
}

function runIdentity(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

/**
 * The suggestion the prompt shows in place of its placeholder: whenever the prompt is empty and nothing is being
 * sent, also after something else was written and deleted. The one a prompt was sent after is not shown again: the
 * one of the same run when both name their run, else the one of the same text. The same text suggested by a later
 * run is shown, also to a prompt that did not watch the run in between.
 */
export function shownPromptSuggestion(suggestion: string | PromptSuggestion | null,
  state: { text: string; images: number; busy: boolean; answered: string | PromptSuggestion | null }): string | null {
  const shown = typeof suggestion === "string" ? { text: suggestion, runId: null } : suggestion;
  if (!shown?.text || state.busy || state.text || state.images !== 0) return null;
  return answeredPromptSuggestion(shown, state.answered) ? null : shown.text;
}

/**
 * The suggestion each prompt was sent after, kept by the key of its request from the moment its submission is made
 * until the host accepts it: a run fast enough to suggest something new before the answer of the host arrives does not
 * have its new suggestion taken for the one the prompt answered. A request sent again (a retry, a replay) keeps the
 * one it was first made after. Bounded: the oldest are forgotten.
 */
export function createAnsweredSuggestions(limit = 32) {
  const captured = new Map<string, PromptSuggestion | null>();
  return {
    /** Keeps what was shown when the submission of a request is made; nothing when the request already has one. */
    capture(key: string, shown: PromptSuggestion | null) {
      if (captured.has(key)) return;
      if (captured.size >= limit) captured.delete(captured.keys().next().value as string);
      captured.set(key, shown);
    },
    /** What the request was sent after, once the host accepted it; null when nothing was shown or it is not known. */
    accepted(key: string): PromptSuggestion | null {
      const shown = captured.get(key) ?? null;
      captured.delete(key);
      return shown;
    },
  };
}

function answeredPromptSuggestion(suggestion: PromptSuggestion, answered: string | PromptSuggestion | null): boolean {
  if (answered === null) return false;
  const mark = typeof answered === "string" ? { text: answered, runId: null } : answered;
  return suggestion.runId && mark.runId ? suggestion.runId === mark.runId : suggestion.text === mark.text;
}

/** Whether a key takes the suggestion that is shown: Tab or the right arrow, alone, as in Claude Code. */
export function takesPromptSuggestion(event: { key: string; ctrlKey: boolean; shiftKey: boolean; altKey: boolean; metaKey: boolean; isComposing: boolean; keyCode: number }): boolean {
  return (event.key === "Tab" || event.key === "ArrowRight") && !event.ctrlKey && !event.shiftKey && !event.altKey && !event.metaKey
    && !event.isComposing && event.keyCode !== 229;
}
