import type { CommandId } from "./commandRegistry";
import type { MessageKey } from "./localization";

/**
 * The commands that a control of the composer in front carries out. The command picker, the landing page and the
 * shortcuts offer each of them exactly when that control acts: the control is the one place that decides.
 */
export const composerControlCommands = Object.freeze(["send", "abort", "clearQueue", "compact"] as const);
export type ComposerControlCommand = typeof composerControlCommands[number];

export function isComposerControlCommand(command: CommandId): command is ComposerControlCommand {
  return (composerControlCommands as readonly string[]).includes(command);
}

/** What the control of a command says of itself; null when the composer in front shows none (it shows it only when it applies). */
export type ComposerControlState = Readonly<{ disabled: boolean; reason: string | null }> | null;

/** Reads a control as the composer renders it: `data-unavailable` holds why it is disabled, in the language of the window. */
export function composerControlState(control: Readonly<{ disabled: boolean; dataset: DOMStringMap }> | null | undefined): ComposerControlState {
  return control ? { disabled: control.disabled, reason: control.dataset.unavailable || null } : null;
}

// What a missing control means: the composer shows the Stop button while a turn runs, Clear queue while prompts wait.
const absentControl: Readonly<Record<ComposerControlCommand, MessageKey>> = Object.freeze({
  send: "Not available in this session now.",
  abort: "No turn is running.",
  clearQueue: "No prompt is queued.",
  compact: "Not available in this session now.",
});

/** Why a command of the composer cannot run now, in the words of its control; null when it runs. */
export function composerCommandBlock(command: ComposerControlCommand, control: ComposerControlState, t: (key: MessageKey) => string): string | null {
  if (control) return control.disabled ? control.reason ?? t("Not available in this session now.") : null;
  return t(absentControl[command]);
}
