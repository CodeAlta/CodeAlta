import type { MessageKey } from "./localization";

/**
 * How CodeAlta titles a session nobody named: the values of the `[sessions] title` setting, in the order the
 * settings list them, each with the line that says what it does.
 */
export const sessionTitleModes = Object.freeze([
  { value: "first_prompt", label: "First prompt", help: "The session keeps the title of its first prompt." },
  { value: "last_message", label: "Last message", help: "The title follows the first line of the last answer." },
  { value: "model", label: "Chosen by the model", help: "The model titles the session, and changes the title when the subject changes." },
] as const satisfies readonly { value: string; label: MessageKey; help: MessageKey }[]);

export type SessionTitleMode = typeof sessionTitleModes[number]["value"];

/** The choice the mode of the host stands for; null for a mode this page does not know. */
export function sessionTitleChoice(mode: string | null | undefined) {
  return sessionTitleModes.find(choice => choice.value === mode) ?? null;
}
