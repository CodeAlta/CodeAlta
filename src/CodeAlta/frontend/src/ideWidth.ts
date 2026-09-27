export type IdeWidth = Readonly<{ width: number; full: boolean }>;
const defaultWidth: IdeWidth = Object.freeze({ width: 272, full: false });
export function parseIdeWidth(text: string | null): IdeWidth {
  try {
    const value: unknown = text ? JSON.parse(text) : null;
    if (!value || typeof value !== "object" || !("width" in value) || !("full" in value)
      || typeof value.width !== "number" || !Number.isFinite(value.width) || typeof value.full !== "boolean") return defaultWidth;
    return { width: Math.min(360, Math.max(220, Math.round(value.width))), full: value.full };
  } catch { return defaultWidth; }
}
export function resizeIdeWidth(value: IdeWidth, delta: number): IdeWidth {
  return { width: Math.min(360, Math.max(220, Math.round(value.width + delta))), full: false };
}
export function persistIdeWidth(save: (value: string) => void, value: IdeWidth): boolean {
  try { save(JSON.stringify(value)); return true; } catch { return false; }
}
