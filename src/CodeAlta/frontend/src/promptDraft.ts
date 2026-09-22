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
