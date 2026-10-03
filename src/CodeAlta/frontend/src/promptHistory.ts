/** Where the prompt editor is in its history: `index` null means the user's own draft is shown. */
export type PromptRecall = Readonly<{ index: number | null; draft: string }>;
export const noPromptRecall: PromptRecall = Object.freeze({ index: null, draft: "" });

/** Sent prompts per session, newest last, kept in memory for this window like the terminal UI's editor history. */
export function createPromptHistory(limit = 100) {
  const sessions = new Map<string, string[]>();
  return {
    add(session: string, text: string) {
      if (!text.trim()) return;
      const entries = sessions.get(session) ?? [];
      // Sending the same prompt twice in a row keeps one entry.
      if (entries.at(-1) !== text) entries.push(text);
      if (entries.length > limit) entries.splice(0, entries.length - limit);
      sessions.set(session, entries);
    },
    list(session: string): readonly string[] { return sessions.get(session) ?? []; },
  };
}

/**
 * Alt+Up (direction -1) walks back through sent prompts, Alt+Down (+1) forward; stepping past the newest
 * entry restores the draft that was being typed. Returns null when there is nothing to change.
 */
export function recallPrompt(entries: readonly string[], state: PromptRecall, direction: -1 | 1, current: string): { text: string; state: PromptRecall } | null {
  if (direction === -1) {
    if (!entries.length || state.index === 0) return null;
    const index = state.index === null ? entries.length - 1 : state.index - 1;
    return { text: entries[index], state: { index, draft: state.index === null ? current : state.draft } };
  }
  if (state.index === null) return null;
  if (state.index < entries.length - 1) return { text: entries[state.index + 1], state: { index: state.index + 1, draft: state.draft } };
  return { text: state.draft, state: noPromptRecall };
}
