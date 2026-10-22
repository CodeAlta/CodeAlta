// The provenance of a user message the host sent, nobody having typed it: a reminder that fired, the end of a
// background job, a review round, the answer to a question... The journal records it as a stable identifier
// (AgentPromptProvenance on the host); the timeline names the message by it instead of "You".
import type { IconName } from "./AppIcon";
import type { MessageKey } from "./localization";

/** The identifiers this version knows. Another well-formed one is shown by its words, with the icon of the host. */
export const promptProvenanceIds = Object.freeze({
  userAnswer: "user-answer", userReview: "user-review", reminder: "reminder", backgroundJob: "background-job",
  agentSession: "agent-session", automation: "automation", modelFallback: "model-fallback", plugin: "plugin", provider: "provider",
} as const);

// The title of a known provenance, as a key of the translations.
const titles: Readonly<Record<string, MessageKey>> = Object.freeze({
  [promptProvenanceIds.userAnswer]: "User answer",
  [promptProvenanceIds.userReview]: "User review",
  [promptProvenanceIds.reminder]: "Reminder",
  [promptProvenanceIds.backgroundJob]: "Background job",
  [promptProvenanceIds.agentSession]: "Agent message",
  [promptProvenanceIds.automation]: "Automation",
  [promptProvenanceIds.modelFallback]: "Model fallback",
  [promptProvenanceIds.plugin]: "Plugin",
  [promptProvenanceIds.provider]: "Provider",
});

const icons: Readonly<Record<string, IconName>> = Object.freeze({
  [promptProvenanceIds.userAnswer]: "ask",
  [promptProvenanceIds.userReview]: "eye",
  [promptProvenanceIds.reminder]: "timer",
  [promptProvenanceIds.backgroundJob]: "terminal",
  [promptProvenanceIds.agentSession]: "branch",
  [promptProvenanceIds.automation]: "automation",
  [promptProvenanceIds.modelFallback]: "model",
  [promptProvenanceIds.plugin]: "plugin",
  [promptProvenanceIds.provider]: "terminal",
});

/** The identifier a record names, when it is well-formed (lowercase words separated by hyphens, at most 64 characters); else null. */
export function promptProvenance(value: unknown): string | null {
  return typeof value === "string" && value.length <= 64 && /^[a-z0-9]+(?:-[a-z0-9]+)*$/u.test(value) ? value : null;
}

/** Whether this version has a title of its own for a provenance. */
export const isKnownPromptProvenance = (id: string): boolean => Object.hasOwn(titles, id);

/**
 * The title of a message by its provenance: its key of the translations for a known one, the words of the identifier
 * for another (`code-review` is `Code review`), which is shown as it is.
 */
export function promptProvenanceTitle(id: string): Readonly<{ key: MessageKey } | { text: string }> {
  if (Object.hasOwn(titles, id)) return { key: titles[id] };
  const words = id.split("-").filter(Boolean).join(" ");
  return { text: words.charAt(0).toUpperCase() + words.slice(1) };
}

/** The icon of a message by its provenance: the host's for a provenance this version does not know. */
export const promptProvenanceIcon = (id: string): IconName => Object.hasOwn(icons, id) ? icons[id] : "info";

// English source keys; translations in es, fr, de, ja, zh-CN order.
export const promptProvenanceMessages = {
  // The titles of the messages the host sends to a session, in its timeline.
  "User answer": ["Respuesta del usuario", "Réponse de l’utilisateur", "Antwort des Benutzers", "ユーザーの回答", "用户回答"],
  "User review": ["Revisión del usuario", "Revue de l’utilisateur", "Review des Benutzers", "ユーザーのレビュー", "用户审阅"],
  "Reminder": ["Recordatorio", "Rappel", "Erinnerung", "リマインダー", "提醒"],
  "Model fallback": ["Cambio de modelo tras un fallo", "Changement de modèle après un échec", "Modellwechsel nach Fehler", "失敗後のモデル切り替え", "失败后切换模型"],
} as const;
