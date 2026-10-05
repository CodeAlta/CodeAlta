/** One plugin status item shown at the end of the composer's status line. */
export type ComposerStatusTone = "info" | "success" | "warning" | "error" | "muted";
export type ComposerStatusView = Readonly<{ key: string; pluginId: string; label: string; text: string; tone: ComposerStatusTone;
  /** The Settings page the item opens, or null when it opens nothing. */
  settingsPage: string | null;
  /** The plugin command the item runs, or null. */
  commandId: string | null }>;

const tones: readonly string[] = ["info", "success", "warning", "error", "muted"];
const maximumItems = 8, maximumText = 160;
const line = (value: unknown): value is string => typeof value === "string" && value.length <= maximumText && !/[\u0000-\u001f\u007f]/.test(value);
const name = (value: unknown): value is string => line(value) && /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(value);

/**
 * Accepts a well-formed `composerStatus.read` answer for the asked project. A refusal or a malformed answer
 * is null (the composer keeps what it showed); a malformed item is left out.
 */
export function composerStatusItems(reply: unknown, projectId: string | null): ComposerStatusView[] | null {
  if (!reply || typeof reply !== "object") return null;
  const value = reply as Record<string, unknown>;
  if (value.status !== "ok" || (value.projectId ?? null) !== projectId || !Array.isArray(value.items)) return null;
  const items: ComposerStatusView[] = [];
  for (const entry of value.items.slice(0, maximumItems)) {
    if (!entry || typeof entry !== "object") continue;
    const item = entry as Record<string, unknown>;
    if (!name(item.pluginId) || !name(item.name) || !line(item.label) || !line(item.text) || !(item.label || item.text)) continue;
    const key = `${item.pluginId}/${item.name}`;
    if (items.some(known => known.key === key)) continue;
    items.push({ key, pluginId: item.pluginId, label: item.label, text: item.text,
      tone: tones.includes(item.tone as string) ? item.tone as ComposerStatusTone : "info",
      settingsPage: name(item.settingsPage) ? item.settingsPage : null,
      commandId: typeof item.commandId === "string" && item.commandId.length > 0 && item.commandId.length <= 512 ? item.commandId : null });
  }
  return items;
}

/** Whether two item lists show the same thing, so a refresh that changed nothing keeps the rendered one. */
export function sameComposerStatus(left: readonly ComposerStatusView[], right: readonly ComposerStatusView[]): boolean {
  return left.length === right.length && left.every((item, index) => {
    const other = right[index];
    return item.key === other.key && item.label === other.label && item.text === other.text && item.tone === other.tone && item.settingsPage === other.settingsPage && item.commandId === other.commandId;
  });
}
