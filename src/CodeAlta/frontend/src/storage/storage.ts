import type { storage } from "#neoastra";
export type { StoragePreferences, StorageMeasurement } from "#neoastra";

/** Injectable generated RPC contract. Paths are returned by the host, never supplied for cleanup. */
export type StorageApi = Pick<typeof storage, "readSettings" | "saveSettings" | "scan">;

/** Fixed category order keeps colors stable across empty and partial measurements. */
export const storageCategories = [
  ["sessions", "Sessions and attachments"], ["scratchpad", "Scratchpads"], ["savedStatistics", "Saved Statistics"],
  ["database", "Application database and backups"], ["configuration", "Configuration and credentials"],
  ["extensions", "Prompts, skills, plugins and plugin data"], ["projects", "Projects and working copies"],
  ["cacheLogs", "Caches and logs"], ["other", "Other state"], ["developer", "Developer data"],
] as const;
export const retentionUnits = ["days", "weeks", "months", "years"] as const;

/** Validate the string, not NumericInput's permissive floating-point interpretation. The host checks calendar overflow too. */
export function retentionAmount(draft: string): number | null {
  if (!/^\d+$/.test(draft)) return null;
  const amount = Number(draft);
  return Number.isSafeInteger(amount) && amount > 0 && amount <= 2_147_483_647 ? amount : null;
}

/** Percentage always uses all measured bytes, independent of chart legend selection. */
export function measuredPercentage(bytes: number, total: number): number { return total > 0 ? bytes / total : 0; }

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
