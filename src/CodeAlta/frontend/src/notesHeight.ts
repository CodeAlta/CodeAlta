// WebView localStorage is scoped to the Desktop user-data root, not the shared .alta catalog.
export const notesHeightKey = "codealta.desktop.notes-height.v1";
export const defaultNotesHeight = 240;
const minimum = 112;
const maximum = 720;

function clamp(value: number): number { return Math.min(maximum, Math.max(minimum, Math.round(value))); }

export function restoreNotesHeight(load: () => string | null): number {
  try {
    const raw = load();
    if (raw === null) return defaultNotesHeight;
    const value: unknown = JSON.parse(raw);
    return typeof value === "number" && Number.isFinite(value) ? clamp(value) : defaultNotesHeight;
  } catch { return defaultNotesHeight; }
}

export function persistNotesHeight(save: (value: string) => void, height: number): boolean {
  try { save(JSON.stringify(clamp(height))); return true; }
  catch { return false; }
}

export function resizeNotesHeight(preferred: number, delta: number): number { return clamp(preferred + delta); }
export function notesResizeKey(key: string): number | "reset" | null {
  if (key === "ArrowUp") return -16;
  if (key === "ArrowDown") return 16;
  if (key === "Home") return "reset";
  return null;
}
export function visibleNotesHeight(preferred: number, available: number): number {
  return Math.min(clamp(preferred), Math.max(minimum, Math.floor(available * 0.65)));
}
