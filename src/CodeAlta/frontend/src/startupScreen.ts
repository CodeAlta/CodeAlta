import { mixColors, shellColor } from "./shellColors";
import { defaultSplashColors, parseSplashAppearance, splashColorNames, splashStorageKey, type SplashAppearance } from "./splashMarkup";

/** Removes the start-up screen once the workspace is there to take its place. Safe to call more than once. */
export function dismissStartupScreen(): void {
  const screen = document.getElementById("splash");
  if (!screen || screen.classList.contains("splash-done")) return;
  screen.classList.add("splash-done");
  // The fade is short; the element goes even where transitions do not run.
  const remove = () => screen.remove();
  screen.addEventListener("transitionend", remove, { once: true });
  window.setTimeout(remove, 400);
}

/** The colors of the theme the window shows now, as the start-up screen needs them. */
export function currentAppearance(theme: "dark" | "light"): SplashAppearance {
  const probe = document.documentElement.appendChild(document.createElement("span"));
  const read = (property: string) => shellColor(probe, property);
  const fallback = defaultSplashColors;
  const background = read("--bg") ?? fallback.background, text = read("--text") ?? fallback.text;
  const appearance: SplashAppearance = { theme, background, surface: read("--panel") ?? background, text,
    accent: read("--accent") ?? fallback.accent,
    // Borders are translucent in the theme; the screen needs what they look like on the background.
    line: read("--line") ?? mixColors(background, text, .16) };
  probe.remove();
  return appearance;
}

/**
 * Keeps the theme for the next start: in the window's storage for the start-up screen, and, when it
 * changed, with the host (`remember`), which paints the window in its background before any page exists.
 */
export function rememberAppearance(theme: "dark" | "light", remember: (appearance: SplashAppearance) => void): void {
  const appearance = currentAppearance(theme);
  let previous: SplashAppearance | null = null;
  try { previous = parseSplashAppearance(localStorage.getItem(splashStorageKey)); } catch { /* Storage is unavailable: the host still learns the theme. */ }
  if (previous && previous.theme === appearance.theme && splashColorNames.every(name => previous![name] === appearance[name])) return;
  try { localStorage.setItem(splashStorageKey, JSON.stringify(appearance)); } catch { /* The next start uses the default colors. */ }
  remember(appearance);
}
