import type { GlobalConfigSaveResponse, GlobalConfigValidationResponse } from "#neoastra";
import type { MessageKey } from "./localization";

/** What the editor last read from the host: the text and the revision a save must still match. */
export type ConfigBaseline = Readonly<{ content: string; revision: string }>;

export type ConfigNotice = Readonly<{ key: MessageKey; parameters?: Readonly<Record<string, string | number>>; intent: "success" | "warning" | "danger" }>;

/** Largest text the host accepts; the editor refuses to send more. */
export const maximumConfigLength = 256 * 1024;

/** A save is offered only for a changed, valid, in-limit text while nothing else is in flight. */
export function canSaveConfig(baseline: ConfigBaseline | null, content: string, validation: GlobalConfigValidationResponse | null, busy: boolean): boolean {
  return !!baseline && !busy && content !== baseline.content && content.length <= maximumConfigLength && validation?.valid === true;
}

/** The message shown after a save attempt; the host's diagnostic text is displayed separately. */
export function configSaveNotice(result: Pick<GlobalConfigSaveResponse, "status" | "providersApplied">, applied: boolean): ConfigNotice {
  switch (result.status) {
    case "ok": return applied
      ? { key: "Saved. {count} providers applied.", parameters: { count: result.providersApplied }, intent: "success" }
      : { key: "Saved. Providers are unchanged until they are applied or the app restarts.", intent: "success" };
    case "apply_failed": return { key: "Saved, but the providers could not be applied. Restart CodeAlta to load them.", intent: "warning" };
    case "invalid": return { key: "The configuration is invalid; nothing was saved.", intent: "danger" };
    case "conflict": return { key: "The file changed on disk after it was read; nothing was saved. Reload it, then reapply your edit.", intent: "danger" };
    case "too_large": return { key: "The configuration is too large for this editor.", intent: "danger" };
    case "stale_epoch": return { key: "The host changed. Reload the window before editing the configuration.", intent: "danger" };
    case "unavailable": return { key: "Configuration editing requires an owned host.", intent: "warning" };
    default: return { key: "The configuration could not be written; nothing was saved.", intent: "danger" };
  }
}

/** The message for a failed read, or null when the read succeeded. */
export function configReadNotice(status: string): ConfigNotice | null {
  switch (status) {
    case "ok": return null;
    case "unavailable": return { key: "Configuration editing requires an owned host.", intent: "warning" };
    case "stale_epoch": return { key: "The host changed. Reload the window before editing the configuration.", intent: "danger" };
    case "too_large": return { key: "The configuration is too large for this editor.", intent: "danger" };
    default: return { key: "The configuration could not be read.", intent: "danger" };
  }
}
