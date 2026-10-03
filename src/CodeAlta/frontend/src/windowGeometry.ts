/** Position and size of a floating window inside its bounds, in CSS pixels. */
export type WindowGeometry = Readonly<{ x: number; y: number; width: number; height: number }>;
export type WindowSize = Readonly<{ width: number; height: number }>;

/** Keeps a window inside its bounds, shrinking it only when the bounds are smaller than the window. */
export function clampWindowGeometry(value: WindowGeometry, bounds: WindowSize, minimum: WindowSize): WindowGeometry {
  const width = Math.round(Math.min(Math.max(value.width, Math.min(minimum.width, bounds.width)), Math.max(bounds.width, 1)));
  const height = Math.round(Math.min(Math.max(value.height, Math.min(minimum.height, bounds.height)), Math.max(bounds.height, 1)));
  return { width, height,
    x: Math.round(Math.min(Math.max(value.x, 0), Math.max(bounds.width - width, 0))),
    y: Math.round(Math.min(Math.max(value.y, 0), Math.max(bounds.height - height, 0))) };
}

/** A window of the preferred size centered in its bounds. */
export function centeredWindowGeometry(bounds: WindowSize, preferred: WindowSize, minimum: WindowSize): WindowGeometry {
  return clampWindowGeometry({ width: preferred.width, height: preferred.height,
    x: (bounds.width - preferred.width) / 2, y: (bounds.height - preferred.height) / 2 }, bounds, minimum);
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
