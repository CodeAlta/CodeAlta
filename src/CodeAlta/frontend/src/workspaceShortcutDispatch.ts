import { resolveShortcut, sessionInfoChordContextAllowed, sessionInfoPrefixFromKey, type ShortcutAction } from "./shortcuts";

export type ShortcutSession = Readonly<{ epoch: string; sessionId: string; projectId: string | null }>;
export type WorkspaceShortcutState = { chordPending: boolean; sessionInfoPrefix: boolean; reminderPrefix: ShortcutSession | null };

// The mounted workspace and the production app share this keyboard dispatcher. Capture the
// selection on the prefix so a subsequent session/host switch cannot redirect the chord.
export function dispatchWorkspaceShortcut(event: KeyboardEvent, state: WorkspaceShortcutState, context: Readonly<{
  workspaceActive: boolean;
  workspaceShell: HTMLElement | null;
  modalOpen: boolean;
  selectedProjectFocused: boolean;
  infoTrigger: HTMLButtonElement | null;
  reminderTrigger: HTMLButtonElement | null;
  selection: ShortcutSession | null;
  run: (action: ShortcutAction) => void;
}>): void {
  const target = event.target instanceof HTMLElement ? event.target : null;
  if (target?.closest("dialog[open]")) {
    state.chordPending = false; state.sessionInfoPrefix = false; state.reminderPrefix = null;
    return;
  }
  if (context.modalOpen || target?.closest('[role="dialog"][aria-modal="true"]')) {
    state.chordPending = false; state.sessionInfoPrefix = false; state.reminderPrefix = null;
    // App-owned non-native dialogs still use the existing Escape handler.
    if (event.key === "Escape" && !event.isComposing && event.keyCode !== 229 && !event.defaultPrevented && !event.repeat) {
      event.preventDefault(); context.run("escape");
    }
    return;
  }
  const editing = target?.matches("input, textarea, select, [contenteditable='true']") === true;
  const common = { workspaceActive: context.workspaceActive, modalOpen: false,
    inWorkspace: !!target && context.workspaceShell?.contains(target) === true,
    editing, promptFocused: target?.matches("#session-prompt, #catalog-prompt") === true };
  const ready = (trigger: HTMLButtonElement | null) => !!trigger?.isConnected && !trigger.disabled &&
    context.workspaceShell?.contains(trigger) === true;
  const infoAvailable = state.sessionInfoPrefix && event.key.toLowerCase() === "t" && !!context.selection &&
    sessionInfoChordContextAllowed({ ...common, triggerReady: ready(context.infoTrigger) }) &&
    context.infoTrigger?.getAttribute("aria-expanded") === "false";
  const reminderAvailable = event.key.toLowerCase() === "d" && !!context.selection && !!state.reminderPrefix &&
    state.reminderPrefix.epoch === context.selection.epoch &&
    state.reminderPrefix.sessionId === context.selection.sessionId &&
    state.reminderPrefix.projectId === context.selection.projectId &&
    sessionInfoChordContextAllowed({ ...common, triggerReady: ready(context.reminderTrigger) });
  const resolved = resolveShortcut(event, state.chordPending, editing, context.selectedProjectFocused,
    infoAvailable, reminderAvailable);
  state.chordPending = resolved.chordPending;
  state.sessionInfoPrefix = sessionInfoPrefixFromKey(event, resolved);
  state.reminderPrefix = state.sessionInfoPrefix && context.selection &&
    sessionInfoChordContextAllowed({ ...common, triggerReady: ready(context.reminderTrigger) })
    ? context.selection : null;
  if (!resolved.handled) return;
  event.preventDefault();
  if (resolved.action) context.run(resolved.action);
}
