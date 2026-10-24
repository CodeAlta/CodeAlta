import type { KeyValueStore } from "./api";

// The theme and the text size of the page. `public/mobile/boot.js` applies the same before the first paint (a classic
// script of the head: the page's policy allows no inline script); this module applies them again, so the page is
// right without it.

export type Theme = "light" | "dark";
export const themeKey = "alta.theme", fontStepKey = "alta.fontStep";
export const fontSteps = 5;
/** The color of the browser's bar for each theme: the color of the page's top bar. */
export const themeColors: Readonly<Record<Theme, string>> = { dark: "#252a31", light: "#ffffff" };

/** The theme kept, else the system's. */
export function readTheme(store: KeyValueStore, systemLight: boolean): Theme {
  const kept = store.get(themeKey);
  return kept === "light" || kept === "dark" ? kept : systemLight ? "light" : "dark";
}

/** The text size kept, from 1 to 5; 2 is the browser's own size. */
export function readFontStep(store: KeyValueStore): number {
  const step = Number(store.get(fontStepKey));
  return Number.isInteger(step) && step >= 1 && step <= fontSteps ? step : 2;
}

/** The next text size: 1 after the largest. */
export const nextFontStep = (step: number): number => step % fontSteps + 1;

/** Shows a theme and a text size on the document, with the color of the browser's bar. */
export function applyAppearance(document: Document, theme: Theme, fontStep: number) {
  const root = document.documentElement;
  root.dataset.theme = theme;
  root.dataset.fontStep = String(fontStep);
  let meta = document.querySelector<HTMLMetaElement>('meta[name="theme-color"]');
  if (!meta) { meta = document.createElement("meta"); meta.name = "theme-color"; document.head.append(meta); }
  meta.content = themeColors[theme];
}
