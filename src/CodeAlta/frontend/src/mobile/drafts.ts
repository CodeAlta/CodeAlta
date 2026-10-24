import type { KeyValueStore } from "./api";

/** The draft of the new-session form, beside the drafts of the sessions. */
export const newSessionDraft = "new";

/**
 * The text typed for each session, kept as it is typed: a session opens on its own draft, a send clears it, and a text
 * that could not be sent comes back to the draft of the session it was written for, whichever session is open by then.
 */
export function createDrafts(store: KeyValueStore) {
  const key = (id: string) => `alta.draft.${id}`;
  return {
    get: (id: string): string => store.get(key(id)) ?? "",
    set(id: string, text: string): void { if (text.trim()) store.set(key(id), text); else store.remove(key(id)); },
    clear: (id: string): void => store.remove(key(id)),
    /** Gives a text back to its session unless something else was typed there since: true when it was given back. */
    giveBack(id: string, text: string): boolean {
      if ((store.get(key(id)) ?? "").trim()) return false;
      store.set(key(id), text);
      return true;
    },
  };
}

/** The drafts of the page. */
export type Drafts = ReturnType<typeof createDrafts>;
