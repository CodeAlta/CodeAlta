/** What the shell can ask of a mounted code editor from outside its panel. */
export type FileEditorActions = Readonly<{
  /** Saves every file that holds unsaved edits; true only when the disk now holds them all. */
  save: () => Promise<boolean>;
  /** Closes the file the editor shows; false when it shows none, and the tab itself is what closes. */
  closeFile?: () => boolean;
}>;

const none: readonly string[] = Object.freeze([]);

/**
 * What the shell knows about the mounted code editors, by tab key: which files of each hold unsaved edits (the
 * tab's unsaved mark, the question asked before closing or exiting) and how to act on one from outside its panel.
 */
export function createFileEditors() {
  const unsaved = new Map<string, readonly string[]>();
  const actions = new Map<string, FileEditorActions>();
  const listeners = new Set<() => void>();
  let version = 0;
  const changed = () => { version++; listeners.forEach(listener => listener()); };
  return {
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    snapshot: () => version,
    dirty: (key: string) => unsaved.has(key),
    /** The names of the files of an editor that hold unsaved edits. */
    unsaved: (key: string) => unsaved.get(key) ?? none,
    /** Whether any editor holds unsaved edits. */
    anyDirty: () => unsaved.size > 0,
    setUnsaved(key: string, names: readonly string[]) {
      const current = unsaved.get(key) ?? none;
      if (current.length === names.length && current.every((name, index) => name === names[index])) return;
      if (names.length) unsaved.set(key, [...names]); else unsaved.delete(key);
      changed();
    },
    /** Registers a mounted editor; the returned function removes it and its unsaved files. */
    attach(key: string, value: FileEditorActions) {
      actions.set(key, value);
      return () => {
        if (actions.get(key) !== value) return;
        actions.delete(key);
        if (unsaved.delete(key)) changed();
      };
    },
    /** Saves the unsaved files of an editor; true only when the disk now holds them. */
    async save(key: string): Promise<boolean> {
      const save = actions.get(key)?.save;
      if (!save) return false;
      try { return await save(); } catch { return false; }
    },
    /** Closes the file an editor shows; false when there is none to close. */
    closeFile(key: string): boolean {
      try { return actions.get(key)?.closeFile?.() ?? false; } catch { return false; }
    },
  };
}
export type FileEditors = ReturnType<typeof createFileEditors>;
