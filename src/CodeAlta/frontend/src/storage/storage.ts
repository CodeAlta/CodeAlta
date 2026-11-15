import type { storage, StorageMeasurement } from "#neoastra";
import type { MessageKey } from "../localization";
export type { StoragePreferences, StorageMeasurement } from "#neoastra";

/** Injectable generated RPC contract. Paths are returned by the host, never supplied for cleanup. */
export type StorageApi = Pick<typeof storage, "readSettings" | "saveSettings" | "scan">;

/**
 * Fixed category order keeps colors stable across empty and partial measurements. The chart palette has ten colors:
 * a category the host measures without a row here (reviews, browser data, local models) is shown in Other state.
 */
export const storageCategories = [
  ["sessions", "Sessions and attachments"], ["scratchpad", "Scratchpads"], ["savedStatistics", "Saved Statistics"],
  ["database", "Application database and backups"], ["configuration", "Configuration and credentials"],
  ["extensions", "Prompts, skills, plugins and plugin data"], ["projects", "Projects and working copies"],
  ["cacheLogs", "Caches and logs"], ["other", "Other state"], ["developer", "Developer data"],
] as const;
export type StorageCategoryId = typeof storageCategories[number][0];

/** The bytes of each row, in the fixed order: a measured category without a row adds to Other state, so every byte is in one row. */
export function categoryBytes(categories: readonly { id: string; bytes: string }[]): Map<StorageCategoryId, number> {
  const rows = new Map<StorageCategoryId, number>(storageCategories.map(([id]) => [id, 0]));
  for (const category of categories) {
    const id = rows.has(category.id as StorageCategoryId) ? category.id as StorageCategoryId : "other";
    rows.set(id, rows.get(id)! + storageBytes(category.bytes));
  }
  return rows;
}
export const retentionUnits = ["days", "weeks", "months", "years"] as const;

/** Validate the string, not NumericInput's permissive floating-point interpretation. The host checks calendar overflow too. */
export function retentionAmount(draft: string): number | null {
  if (!/^\d+$/.test(draft)) return null;
  const amount = Number(draft);
  return Number.isSafeInteger(amount) && amount > 0 && amount <= 2_147_483_647 ? amount : null;
}

/** Percentage always uses all measured bytes, independent of chart legend selection. */
export function measuredPercentage(bytes: number, total: number): number { return total > 0 ? bytes / total : 0; }

/**
 * What the next start removes of the scratchpad of this instance: its files old enough for the cleanup, as one line;
 * null before the scan, and when there are none.
 */
export function oldScratchpadText(measurement: Pick<StorageMeasurement, "oldScratchpadBytes" | "oldScratchpadFiles"> | null, locale: string,
  t: (key: MessageKey, parameters?: Readonly<Record<string, string | number>>) => string): string | null {
  const files = measurement ? storageBytes(measurement.oldScratchpadFiles) : 0;
  if (!measurement || files === 0) return null;
  return t(files === 1 ? "The next start removes {size} of old scratchpad files ({files} file)." : "The next start removes {size} of old scratchpad files ({files} files).",
    { size: storageSize(storageBytes(measurement.oldScratchpadBytes), locale), files: new Intl.NumberFormat(locale).format(files) });
}

/** A share of the whole as the user reads it: a part that holds bytes never reads as 0%, but as "<0.1%". */
export function shareText(bytes: number, total: number, locale: string): string {
  const share = new Intl.NumberFormat(locale, { style: "percent", maximumFractionDigits: 1 });
  const part = measuredPercentage(bytes, total);
  return bytes > 0 && part < .001 ? `<${share.format(.001)}` : share.format(part);
}

/** Int64 wire values remain strings; only presentation/chart values use finite floating-point approximations. */
export function storageBytes(value: string): number {
  if (!/^\d+$/.test(value)) return 0;
  const number = Number(value);
  return Number.isFinite(number) ? number : 0;
}

/** Logical file sizes, not allocated disk space. */
export function storageSize(bytes: number, locale: string): string {
  const units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
  const index = bytes > 0 ? Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024))) : 0;
  return `${new Intl.NumberFormat(locale, { maximumFractionDigits: index === 0 ? 0 : 1 }).format(bytes / 1024 ** index)} ${units[index]}`;
}
