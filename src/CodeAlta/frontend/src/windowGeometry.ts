/** Position and size of a floating window inside its bounds, in CSS pixels. */
export type WindowGeometry = Readonly<{ x: number; y: number; width: number; height: number }>;
export type WindowSize = Readonly<{ width: number; height: number }>;

/**
 * A window opens at most this share of its bounds wide and high, whatever its preferred size: on a small screen
 * the window behind stays in sight around it.
 */
export const openingShare = 0.9;

// The band at the top of the bounds a window keeps out of, the native title bar, unless the window would then not
// have its minimum height: the window must stay reachable.
function bandOf(bounds: WindowSize, minimum: WindowSize, top: number): number {
  return Math.max(0, Math.min(top, bounds.height - minimum.height));
}

/**
 * Keeps a window inside its bounds and below `top`, the height of a native title bar whose controls are drawn over
 * the page, shrinking it only when the bounds are smaller than the window.
 */
export function clampWindowGeometry(value: WindowGeometry, bounds: WindowSize, minimum: WindowSize, top = 0): WindowGeometry {
  const band = bandOf(bounds, minimum, top), free = bounds.height - band;
  const width = Math.round(Math.min(Math.max(value.width, Math.min(minimum.width, bounds.width)), Math.max(bounds.width, 1)));
  const height = Math.round(Math.min(Math.max(value.height, Math.min(minimum.height, free)), Math.max(free, 1)));
  return { width, height,
    x: Math.round(Math.min(Math.max(value.x, 0), Math.max(bounds.width - width, 0))),
    y: Math.round(Math.min(Math.max(value.y, band), band + Math.max(free - height, 0))) };
}

/** A window of the preferred size, at most `openingShare` of its bounds, centered in them below `top`. */
export function centeredWindowGeometry(bounds: WindowSize, preferred: WindowSize, minimum: WindowSize, top = 0): WindowGeometry {
  const band = bandOf(bounds, minimum, top), free = bounds.height - band;
  const width = Math.min(preferred.width, bounds.width * openingShare), height = Math.min(preferred.height, free * openingShare);
  return clampWindowGeometry({ width, height, x: (bounds.width - width) / 2, y: band + (free - height) / 2 }, bounds, minimum, top);
}

/** What a step of the title bar's Larger and Smaller changes: this share of the bounds, in width and in height. */
export const sizeStep = 0.1;

/**
 * The window one step larger (`direction` 1) or smaller (-1), around its center, kept inside its bounds and above its
 * minimum size: a step that would leave the bounds takes what room is left there.
 */
export function steppedWindowGeometry(value: WindowGeometry, bounds: WindowSize, minimum: WindowSize, top: number, direction: 1 | -1): WindowGeometry {
  const band = bandOf(bounds, minimum, top), free = bounds.height - band;
  const width = value.width + direction * bounds.width * sizeStep, height = value.height + direction * free * sizeStep;
  const centerX = value.x + value.width / 2, centerY = value.y + value.height / 2;
  return clampWindowGeometry({ width, height, x: centerX - width / 2, y: centerY - height / 2 }, bounds, minimum, top);
}

/** Whether a step that way changes the size of the window: not at the size of its bounds, nor at its minimum. */
export function canStepWindow(value: WindowGeometry, bounds: WindowSize, minimum: WindowSize, top: number, direction: 1 | -1): boolean {
  const next = steppedWindowGeometry(value, bounds, minimum, top, direction);
  return next.width !== value.width || next.height !== value.height;
}

/** Reads a stored geometry; anything malformed or non-finite is refused. */
export function parseWindowGeometry(text: string | null): WindowGeometry | null {
  try {
    const value: unknown = text ? JSON.parse(text) : null;
    if (!value || typeof value !== "object") return null;
    const { x, y, width, height } = value as Record<string, unknown>;
    for (const field of [x, y, width, height]) if (typeof field !== "number" || !Number.isFinite(field)) return null;
    if ((width as number) <= 0 || (height as number) <= 0) return null;
    return { x: x as number, y: y as number, width: width as number, height: height as number };
  } catch { return null; }
}

/** Loads a window geometry from WebView-local storage; unavailable storage yields null. */
export function loadWindowGeometry(key: string): WindowGeometry | null {
  try { return parseWindowGeometry(localStorage.getItem(key)); } catch { return null; }
}

/** Saves (or, with null, forgets) a window geometry; storage failures are ignored. */
export function saveWindowGeometry(key: string, value: WindowGeometry | null): void {
  try { if (value) localStorage.setItem(key, JSON.stringify(value)); else localStorage.removeItem(key); } catch { /* The window still works for this run. */ }
}
