import { translate, type Locale, type MessageKey } from "./localization";

// Strings originate in controllers/backends and remain literal. Only explicit local
// presentation keys are translated; language is never part of request identity.
export type WorkflowNotice = string | Readonly<{ key: MessageKey; parameters?: Readonly<Record<string, string | number>> }> | Readonly<{ parts: readonly WorkflowNotice[] }>;
export function workflowNotice(locale: Locale, notice: WorkflowNotice): string {
  return typeof notice === "string" ? notice : "parts" in notice ? notice.parts.map(part => workflowNotice(locale, part)).join(" ")
    : translate(locale, notice.key, notice.parameters);
}
