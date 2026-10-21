import { useEffect, useRef, useState } from "react";
import { applyFontPreferences, fontPreferencesKey, persistFontPreferences, restoreFontPreferences, type FontPreferences } from "./codeFonts";

/** Sent on the window, with the preferences, once the fonts of the page changed: the editors measure their font again. */
export const fontPreferencesEvent = "codealta:font-preferences";

function readFontPreferences(): FontPreferences {
  return restoreFontPreferences(() => { try { return localStorage.getItem(fontPreferencesKey); } catch { return null; } });
}

function changed(preferences: FontPreferences) {
  applyFontPreferences(document.documentElement.style, preferences);
  window.dispatchEvent(new CustomEvent(fontPreferencesEvent, { detail: preferences }));
}

/**
 * Gives the page the fonts the user chose, before anything is shown, and the ones another window of the
 * application chooses later. Returns the disposer.
 */
export function installFontPreferences(target: Window): () => void {
  applyFontPreferences(target.document.documentElement.style, readFontPreferences());
  const stored = (event: StorageEvent) => { if (event.key === fontPreferencesKey) changed(readFontPreferences()); };
  target.addEventListener("storage", stored);
  return () => target.removeEventListener("storage", stored);
}

/**
 * The font preferences of the window and what changes them, with whether the last change could be kept. A change
 * shows at once; one that could not be kept still stands until the window is closed.
 */
export function useFontPreferences(): readonly [FontPreferences, (change: Partial<FontPreferences>) => void, boolean] {
  const [preferences, setPreferences] = useState(readFontPreferences);
  const [saved, setSaved] = useState(true);
  const latest = useRef(preferences);
  useEffect(() => {
    const listener = (event: Event) => {
      const next = (event as CustomEvent<FontPreferences>).detail;
      if (!next || next === latest.current) return;
      latest.current = next; setPreferences(next);
    };
    window.addEventListener(fontPreferencesEvent, listener);
    return () => window.removeEventListener(fontPreferencesEvent, listener);
  }, []);
  const [update] = useState(() => (change: Partial<FontPreferences>) => {
    const next = { ...latest.current, ...change };
    latest.current = next; setPreferences(next);
    setSaved(persistFontPreferences(value => localStorage.setItem(fontPreferencesKey, value), next));
    changed(next);
  });
  return [preferences, update, saved];
}
