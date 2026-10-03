export type IdeWidth = Readonly<{ width: number }>;
/** Explorer width bounds in CSS pixels; the splitter's ARIA range uses the same values. */
export const minimumIdeWidth = 220;
export const maximumIdeWidth = 720;
export const defaultIdeWidth: IdeWidth = Object.freeze({ width: 272 });
const clamp = (width: number) => Math.min(maximumIdeWidth, Math.max(minimumIdeWidth, Math.round(width)));
export function parseIdeWidth(text: string | null): IdeWidth {
  try {
    const value: unknown = text ? JSON.parse(text) : null;
    if (!value || typeof value !== "object" || !("width" in value)
      || typeof value.width !== "number" || !Number.isFinite(value.width)) return defaultIdeWidth;
    return { width: clamp(value.width) };
  } catch { return defaultIdeWidth; }
}
export function resizeIdeWidth(value: IdeWidth, delta: number): IdeWidth {
  return { width: clamp(value.width + delta) };
}
export function persistIdeWidth(save: (value: string) => void, value: IdeWidth): boolean {
  try { save(JSON.stringify(value)); return true; } catch { return false; }
}
