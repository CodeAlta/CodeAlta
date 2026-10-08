const maximumDraftUnits = 32768;

export function draftStorageKey(sessionId: string): string {
  return `codealta.desktop.prompt.${sessionId}`;
}

export function restoreDraft(read: (key: string) => string | null, sessionId: string): string {
  try {
    const value = read(draftStorageKey(sessionId));
    return value && value.length <= maximumDraftUnits ? value : "";
  } catch { return ""; }
}

export function persistDraft(write: (key: string, value: string) => void, remove: (key: string) => void, sessionId: string, text: string): boolean {
  try {
    if (text) write(draftStorageKey(sessionId), text.slice(0, maximumDraftUnits));
    else remove(draftStorageKey(sessionId));
    return true;
  } catch { return false; }
}

// Explicit create handoff only. Failed reads never certify an empty destination;
// the original local draft remains authoritative even after a successful copy.
export function transferPromptDraft(read: (key: string) => string | null, write: (key: string, text: string) => void,
  sessionId: string, text: string): boolean {
  if (!text || text.length > maximumDraftUnits) return false;
  try {
    const key = draftStorageKey(sessionId);
    if (read(key)) return false;
    write(key, text);
    return read(key) === text;
  } catch { return false; }
}

// An indicator describes edits observed by this window, never a restored value or a journal
// guess. Failed storage leaves only the selected live editor authoritative.
export function createDraftIndicators() {
  const edits = new Map<string, { generation: number; persisted: boolean }>();
  const listeners = new Set<() => void>();
  let revision = 0;
  let generation = 0;
  const publish = () => { revision++; for (const listener of listeners) listener(); };
  return {
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    snapshot: () => revision,
    visible: (id: string, selectedId: string | null) => edits.has(id) && (id === selectedId || edits.get(id)?.persisted === true),
    edit(id: string, text: string, restored: string) {
      const valid = text.length > 0 && text.length <= maximumDraftUnits && text !== restored
        && !/^[\s\u0085]*$/u.test(text) && isWellFormed(text);
      if (!valid) { if (edits.delete(id)) publish(); return null; }
      // Each edit gets an exact identity; an earlier persistence effect cannot certify this edit.
      const current = ++generation;
      edits.set(id, { generation: current, persisted: false });
      publish();
      return current;
    },
    persisted(id: string, editGeneration: number | null, success: boolean) {
      const current = edits.get(id);
      if (current && current.generation === editGeneration && current.persisted !== success) {
        edits.set(id, { ...current, persisted: success });
        publish();
      }
    },
    clear(id: string) { if (edits.delete(id)) publish(); },
  };
}

function isWellFormed(value: string): boolean {
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i);
    if (code < 0xd800 || code > 0xdfff) continue;
    if (code > 0xdbff || ++i === value.length || value.charCodeAt(i) < 0xdc00 || value.charCodeAt(i) > 0xdfff) return false;
  }
  return true;
}

/**
 * The prompts typed before their session exists, by scope (a project, or the chats). The prompt that shows a
 * text follows it; the window follows only what decides whether it can be sent (see draftSendFacts), so that
 * a keystroke renders the prompt and nothing around it.
 */
export function createLocalDrafts() {
  const drafts = new Map<string, Readonly<{ text: string; revision: number }>>();
  const listeners = new Set<() => void>();
  return {
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    /** The draft of a scope; `restore` gives its text the first time the scope is asked for. */
    get(scope: string, restore: () => string) {
      let draft = drafts.get(scope);
      if (!draft) drafts.set(scope, draft = { text: restore(), revision: 0 });
      return draft;
    },
    /** The draft of a scope as it is now, if the scope was asked for. */
    peek: (scope: string) => drafts.get(scope),
    /** Replaces the text of a scope: each edit is another revision, the same text included. */
    edit(scope: string, text: string) {
      drafts.set(scope, { text, revision: (drafts.get(scope)?.revision ?? 0) + 1 });
      for (const listener of [...listeners]) listener();
    },
  };
}

/**
 * What a draft says of whether it can be sent, as one number that changes only when one of these does: it is
 * empty, it holds only white space, it is longer than a prompt with images may be.
 */
export function draftSendFacts(text: string, imageTextLimit: number): number {
  return (text === "" ? 1 : 0) | (text.trim() ? 0 : 2) | (text.length > imageTextLimit ? 4 : 0);
}
