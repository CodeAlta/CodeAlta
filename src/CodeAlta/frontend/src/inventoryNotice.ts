import { translate, type Locale, type MessageKey } from "./localization";

// Only local UI keys enter this presentation state. Backend status parameters stay literal.
export type InventoryNotice = MessageKey | "" | Readonly<{ key: MessageKey; status: string }>;
export function inventoryNotice(locale: Locale, notice: InventoryNotice): string {
  return !notice ? "" : typeof notice === "string" ? translate(locale, notice)
    : translate(locale, notice.key, { status: notice.status });
}
