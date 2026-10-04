/**
 * The start-up screen: the title bar with the application mark, the logo and a progress bar, in the colors
 * of the theme the window last had. It is what the window shows from the moment its view exists until the
 * workspace is ready, in two documents: `splash.html`, shown while the host starts, and `index.html`, where
 * it stays until the application has its first data. Both are built from this one description.
 *
 * Nothing here may depend on the application's stylesheet or scripts: the screen is there before they load.
 */

/** Where the application keeps the colors of its theme for the next start. */
export const splashStorageKey = "codealta.desktop.splash.v1";

/** The colors the start-up screen takes from the last theme; each is `#rrggbb`. */
export const splashColorNames = ["background", "surface", "text", "accent", "line"] as const;
export type SplashColors = Readonly<Record<typeof splashColorNames[number], string>>;
export type SplashAppearance = Readonly<{ theme: "dark" | "light" } & SplashColors>;

/** The default dark theme, for a first start. */
export const defaultSplashColors: SplashColors = Object.freeze({
  background: "#1c2127", surface: "#252a31", text: "#f6f7f9", accent: "#4c90f0", line: "#383e47",
});

/** Reads a stored appearance; anything that is not one is ignored. */
export function parseSplashAppearance(value: string | null): SplashAppearance | null {
  if (!value) return null;
  try {
    const parsed: unknown = JSON.parse(value);
    if (!parsed || typeof parsed !== "object") return null;
    const record = parsed as Record<string, unknown>;
    if (record.theme !== "dark" && record.theme !== "light") return null;
    const colors: Record<string, string> = {};
    for (const name of splashColorNames) {
      const color = record[name];
      if (typeof color !== "string" || !/^#[0-9a-f]{6}$/i.test(color)) return null;
      colors[name] = color.toLowerCase();
    }
    return { theme: record.theme, ...(colors as SplashColors) };
  } catch { return null; }
}

/**
 * The script of the start-up screen, a file of its own because the page allows no inline script. It runs
 * before the first paint and gives the document the stored colors; without them the defaults apply.
 */
export const splashScript = `(function () {
  try {
    var saved = JSON.parse(localStorage.getItem(${JSON.stringify(splashStorageKey)}) || "null");
    if (!saved || typeof saved !== "object") return;
    var root = document.documentElement;
    if (saved.theme === "light" || saved.theme === "dark") root.dataset.theme = saved.theme;
    ${JSON.stringify(splashColorNames)}.forEach(function (name) {
      var color = saved[name];
      if (typeof color === "string" && /^#[0-9a-f]{6}$/i.test(color)) root.style.setProperty("--splash-" + name, color);
    });
  } catch (error) { /* The default colors stay. */ }
})();
`;

const color = (name: keyof SplashColors) => `var(--splash-${name}, ${defaultSplashColors[name]})`;

/** The style and the elements of the start-up screen. `logo` is the application mark as inline SVG. */
export function splashMarkup(logo: string): string {
  const mark = logo.replace(/<\?xml[^>]*\?>/u, "").trim();
  return `<style>
html { background: ${color("background")}; }
#splash { position: fixed; inset: 0; z-index: 2147483000; display: flex; flex-direction: column; background: ${color("background")}; color: ${color("text")};
  font: 13px/1.3 "Segoe UI", system-ui, -apple-system, sans-serif; user-select: none; transition: opacity .18s ease-out; }
#splash.splash-done { opacity: 0; pointer-events: none; }
#splash .splash-titlebar { flex: 0 0 38px; display: flex; align-items: center; gap: 9px; padding: 0 12px 0 calc(11px + var(--neoastra-titlebar-inset-left, 0px));
  border-bottom: 1px solid ${color("line")}; background: ${color("surface")}; font-weight: 600; letter-spacing: .01em; app-region: drag; -webkit-app-region: drag; }
#splash .splash-titlebar svg { flex: none; width: 22px; height: 22px; }
#splash .splash-body { flex: 1 1 auto; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 26px; padding-bottom: 38px; }
#splash .splash-body svg { width: 96px; height: 96px; filter: drop-shadow(0 10px 28px rgb(0 0 0 / .28)); }
#splash .splash-progress { position: relative; width: 168px; height: 3px; overflow: hidden; border-radius: 2px; background: ${color("line")}; }
#splash .splash-progress::after { content: ""; position: absolute; inset: 0 auto 0 0; width: 40%; border-radius: 2px; background: ${color("accent")};
  animation: splash-progress 1.15s cubic-bezier(.45, .05, .55, .95) infinite; }
@keyframes splash-progress { from { transform: translateX(-110%); } to { transform: translateX(270%); } }
@media (prefers-reduced-motion: reduce) { #splash .splash-progress::after { animation: none; width: 100%; opacity: .55; } }
</style>
<div id="splash" role="status" aria-label="CodeAlta">
  <div class="splash-titlebar" data-neoastra-drag-region>${mark}<span>CodeAlta</span></div>
  <div class="splash-body">${mark}<div class="splash-progress"></div></div>
</div>`;
}

/** The document shown while the host starts: the start-up screen alone. */
export function splashDocument(logo: string): string {
  return `<!doctype html>
<html lang="en">
  <head><meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1.0"><title>CodeAlta</title><script src="./splash.js"></script></head>
  <body>${splashMarkup(logo)}</body>
</html>
`;
}
