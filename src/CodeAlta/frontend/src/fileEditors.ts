/**
 * What the shell knows about the mounted file editors, by tab key: which hold unsaved edits (the tab's
 * dirty mark, the question asked before closing) and how to save one from outside its panel.
 */
export function createFileEditors() {
  const dirty = new Set<string>();
  const savers = new Map<string, () => Promise<boolean>>();
  const listeners = new Set<() => void>();
  let version = 0;
  const changed = () => { version++; listeners.forEach(listener => listener()); };
  return {
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    snapshot: () => version,
    dirty: (key: string) => dirty.has(key),
    setDirty(key: string, value: boolean) {
      if (dirty.has(key) === value) return;
      if (value) dirty.add(key); else dirty.delete(key);
      changed();
    },
    /** Registers a mounted editor's save; the returned function removes it and its dirty mark. */
    attach(key: string, save: () => Promise<boolean>) {
      savers.set(key, save);
      return () => {
        if (savers.get(key) !== save) return;
        savers.delete(key);
        if (dirty.delete(key)) changed();
      };
    },
    /** Saves an editor's text; true only when the file now holds it. */
    async save(key: string): Promise<boolean> {
      const save = savers.get(key);
      if (!save) return false;
      try { return await save(); } catch { return false; }
    },
  };
}
export type FileEditors = ReturnType<typeof createFileEditors>;
