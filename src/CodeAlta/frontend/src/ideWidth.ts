export type IdeWidth = Readonly<{ width: number; full: boolean }>;
/** Explorer width bounds in CSS pixels; the splitter's ARIA range uses the same values. */
export const minimumIdeWidth = 220;
export const maximumIdeWidth = 720;
const defaultWidth: IdeWidth = Object.freeze({ width: 272, full: false });
const clamp = (width: number) => Math.min(maximumIdeWidth, Math.max(minimumIdeWidth, Math.round(width)));
export function parseIdeWidth(text: string | null): IdeWidth {
  try {
    const value: unknown = text ? JSON.parse(text) : null;
    if (!value || typeof value !== "object" || !("width" in value) || !("full" in value)
      || typeof value.width !== "number" || !Number.isFinite(value.width) || typeof value.full !== "boolean") return defaultWidth;
    return { width: clamp(value.width), full: value.full };
  } catch { return defaultWidth; }
}
export function resizeIdeWidth(value: IdeWidth, delta: number): IdeWidth {
  return { width: clamp(value.width + delta), full: false };
}
export function persistIdeWidth(save: (value: string) => void, value: IdeWidth): boolean {
  try { save(JSON.stringify(value)); return true; } catch { return false; }
}
