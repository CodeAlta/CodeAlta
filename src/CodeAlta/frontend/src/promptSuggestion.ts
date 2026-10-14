import type { SessionRuntimeStateEntry } from "#neoastra";

/** A suggestion longer than this is not one somebody would send as it is. */
const maximumPromptSuggestionLength = 256;

/**
 * What the provider of a session suggested as the next prompt, once its last turn ended (Claude Code's prompt
 * suggestion): none while a run goes on, for an attachment that ends, and for what is not one line of text.
 */
export function entryPromptSuggestion(entry: SessionRuntimeStateEntry | null | undefined): string | null {
  if (!entry || entry.isTerminated || entry.isRetiring || entry.activeRunId) return null;
  const value = entry.promptSuggestion;
  if (typeof value !== "string") return null;
  const text = value.trim();
  return text.length > 0 && text.length <= maximumPromptSuggestionLength && !/[\p{Cc}\p{Zl}\p{Zp}]/u.test(text) ? text : null;
}

/**
 * The suggestion the prompt shows in place of its placeholder: whenever the prompt is empty and nothing is being
 * sent, also after something else was written and deleted. The one a prompt was sent after is not shown again.
 */
export function shownPromptSuggestion(suggestion: string | null, state: { text: string; images: number; busy: boolean; answered: string | null }): string | null {
  return suggestion && !state.busy && !state.text && state.images === 0 && state.answered !== suggestion ? suggestion : null;
}

/** Whether a key takes the suggestion that is shown: Tab or the right arrow, alone, as in Claude Code. */
export function takesPromptSuggestion(event: { key: string; ctrlKey: boolean; shiftKey: boolean; altKey: boolean; metaKey: boolean; isComposing: boolean; keyCode: number }): boolean {
  return (event.key === "Tab" || event.key === "ArrowRight") && !event.ctrlKey && !event.shiftKey && !event.altKey && !event.metaKey
    && !event.isComposing && event.keyCode !== 229;
}
