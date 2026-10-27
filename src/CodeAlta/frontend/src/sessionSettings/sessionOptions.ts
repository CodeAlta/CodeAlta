// The options of a session beside its agent prompt, model, permission mode and sandbox: what its agent may do, such as
// starting sessions of its own. Every option is on by default; null stands for the defaults.
import type { SessionOptionsChoice } from "#neoastra";

/** The options of a session that chose none: every option on. */
export const defaultSessionOptions: SessionOptionsChoice = Object.freeze({ subSessions: true });

/** The options a value stands for: null is the defaults. */
export function sessionOptionsOf(options: SessionOptionsChoice | null | undefined): SessionOptionsChoice {
  return options ?? defaultSessionOptions;
}

/** How many options differ from their default: the form shows them without being asked, the chip marks them. */
export function changedSessionOptions(options: SessionOptionsChoice | null | undefined): number {
  return sessionOptionsOf(options).subSessions ? 0 : 1;
}

/** What a choice records: null for the defaults, so that a session without a choice records nothing. */
export function storedSessionOptions(options: SessionOptionsChoice | null | undefined): SessionOptionsChoice | null {
  return options && changedSessionOptions(options) > 0 ? { subSessions: options.subSessions } : null;
}
