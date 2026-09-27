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
