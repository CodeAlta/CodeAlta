import { useEffect, useRef, useState } from "react";
import { changesPreferencesKey, persistChangesPreferences, restoreChangesPreferences, type ChangesPreferences } from "./projectChanges";

/** Sent on the window, with the preferences, when a changes tab or the settings changed them. */
export const changesPreferencesEvent = "codealta:changes-preferences";

/**
 * The preferences of the changes tabs and what changes them. They are one set for the window: a change made
 * in a tab or in the settings is kept for the next start and shows at once in the other tabs.
 */
export function useChangesPreferences(): readonly [ChangesPreferences, (change: Partial<ChangesPreferences>) => void] {
  const [preferences, setPreferences] = useState(() => restoreChangesPreferences(() => localStorage.getItem(changesPreferencesKey)));
  const latest = useRef(preferences);
  useEffect(() => {
    const changed = (event: Event) => {
      const next = (event as CustomEvent<ChangesPreferences>).detail;
      if (!next || next === latest.current) return;
      latest.current = next; setPreferences(next);
    };
    window.addEventListener(changesPreferencesEvent, changed);
    return () => window.removeEventListener(changesPreferencesEvent, changed);
  }, []);
  const [update] = useState(() => (change: Partial<ChangesPreferences>) => {
    const next = { ...latest.current, ...change };
    latest.current = next; setPreferences(next);
    persistChangesPreferences(value => localStorage.setItem(changesPreferencesKey, value), next);
    window.dispatchEvent(new CustomEvent(changesPreferencesEvent, { detail: next }));
  });
  return [preferences, update];
}
