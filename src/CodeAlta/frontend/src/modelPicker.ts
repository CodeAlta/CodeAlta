import { useSyncExternalStore } from "react";

// The model list of the composer: what a search keeps of it, and how many models it shows before it scrolls.

/** How many models the list shows at once: a whole number, kept by the window. */
export const modelPickerRowsKey = "codealta.desktop.modelPicker.rows.v1";
export const minimumModelPickerRows = 4, maximumModelPickerRows = 30, defaultModelPickerRows = 10;

/** A model as the list names it. */
export type ModelPickerChoice = Readonly<{ id: string; name: string }>;

/** The models whose name or id holds every word of the search, in the order of the provider; all of them for an empty search. */
export function matchingModels<T extends ModelPickerChoice>(models: readonly T[], query: string): readonly T[] {
  const terms = query.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean);
  if (terms.length === 0) return models;
  return models.filter(model => { const text = `${model.name} ${model.id}`.toLocaleLowerCase(); return terms.every(term => text.includes(term)); });
}

/**
 * The option a key makes the active one in a list of `count` options that shows `rows` of them, from option `index`;
 * null for a key that does not move. Home and End stay with the caret of the search box.
 */
export function modelAfterKey(key: string, index: number, count: number, rows: number): number | null {
  if (count === 0) return null;
  const step = key === "ArrowDown" ? 1 : key === "ArrowUp" ? -1 : key === "PageDown" ? rows : key === "PageUp" ? -rows : 0;
  return step === 0 ? null : Math.min(count - 1, Math.max(0, index + step));
}

/** The number of rows a stored or typed value stands for; null when it is not a whole number the list takes. */
export function modelPickerRows(value: unknown): number | null {
  const rows = typeof value === "string" && /^\d{1,3}$/.test(value.trim()) ? Number(value.trim()) : value;
  return typeof rows === "number" && Number.isInteger(rows) && rows >= minimumModelPickerRows && rows <= maximumModelPickerRows ? rows : null;
}

type Store = Pick<Storage, "getItem" | "setItem">;

/**
 * The number of models the list shows, over a storage: read once, changed by `set`, and told to whoever listens, so that
 * Settings and an open list show the same thing. A storage that cannot be read gives the default, and one that cannot be
 * written keeps the choice until the window closes.
 */
export function createModelPickerPreferences(storage: Store | null) {
  let current = defaultModelPickerRows;
  try { current = modelPickerRows(storage?.getItem(modelPickerRowsKey)) ?? defaultModelPickerRows; } catch { /* The default. */ }
  const listeners = new Set<() => void>();
  return Object.freeze({
    get: (): number => current,
    set(rows: number) {
      if (modelPickerRows(rows) === null || rows === current) return;
      current = rows;
      try { storage?.setItem(modelPickerRowsKey, String(rows)); } catch { /* The choice holds for this run of the window. */ }
      for (const listener of [...listeners]) listener();
    },
    subscribe(listener: () => void) { listeners.add(listener); return () => { listeners.delete(listener); }; },
  });
}

export type ModelPickerPreferenceStore = ReturnType<typeof createModelPickerPreferences>;

function windowStorage(): Store | null {
  try { return typeof localStorage === "undefined" ? null : localStorage; } catch { return null; }
}

/** The preference of this window. */
export const modelPickerPreferences = createModelPickerPreferences(windowStorage());

/** The number of models the list shows, drawn again when it changes. */
export function useModelPickerRows(store: ModelPickerPreferenceStore = modelPickerPreferences): number {
  return useSyncExternalStore(store.subscribe, store.get, store.get);
}
