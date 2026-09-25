export type ShortcutAction =
  | "openProject" | "focusProjects" | "focusSessions" | "focusPrompt" | "focusSearch"
  | "nextProject" | "previousProject" | "nextSession" | "previousSession"
  | "settings" | "providers" | "models" | "prompts" | "context" | "plugins"
  | "toggleNotes" | "help" | "escape" | "expandPrompt" | "renameProject" | "sessionInfo" | "reminders"
  | "messagePrevious" | "messageNext" | "messageFirst" | "messageLatest";

export type ShortcutKey = Readonly<{ key: string; ctrlKey?: boolean; altKey?: boolean; shiftKey?: boolean; metaKey?: boolean;
  isComposing?: boolean; keyCode?: number; defaultPrevented?: boolean; repeat?: boolean }>;
export type ShortcutResolution = Readonly<{ action: ShortcutAction | null; chordPending: boolean; handled: boolean }>;

export function sessionInfoPrefixFromKey(event: ShortcutKey, resolution: ShortcutResolution): boolean {
  return resolution.chordPending && event.ctrlKey === true && !event.metaKey && !event.altKey && !event.shiftKey
    && event.key.toLowerCase() === "g";
}

export function sessionInfoChordContextAllowed(context: Readonly<{
  workspaceActive: boolean; modalOpen: boolean; inWorkspace: boolean; editing: boolean;
  promptFocused: boolean; triggerReady: boolean;
}>): boolean {
  return context.workspaceActive && !context.modalOpen && context.inWorkspace && context.triggerReady
    && (!context.editing || context.promptFocused);
}

export function resolveShortcut(event: ShortcutKey, chordPending: boolean, editing: boolean, selectedProjectFocused = false,
  sessionInfoAvailable = false, remindersAvailable = false, messageAvailable = false, latestAvailable = false): ShortcutResolution {
  if (event.isComposing || event.keyCode === 229 || event.defaultPrevented || event.repeat)
    return { action: null, chordPending: false, handled: false };
  const key = event.key.toLowerCase();
  const primary = event.ctrlKey === true || event.metaKey === true;
  if (key === "f6" && !primary && !event.altKey && !event.shiftKey) return action("expandPrompt");

  if (key === "escape") return { action: "escape", chordPending: false, handled: true };
  if (chordPending) {
    // Ctrl+T is a browser shortcut: prevent it only when this exact chord can open session info.
    if (key === "t") return event.ctrlKey === true && !event.metaKey && !event.altKey && !event.shiftKey && sessionInfoAvailable
      ? action("sessionInfo") : { action: null, chordPending: false, handled: false };
    if (key === "d") return event.ctrlKey === true && !event.metaKey && !event.altKey && !event.shiftKey && remindersAvailable
      ? action("reminders") : { action: null, chordPending: false, handled: false };
    const mappedAction = ({
      s: "focusProjects", p: "focusPrompt", w: "settings", r: "providers", o: "models",
      h: "prompts", u: "context", n: "plugins", g: "toggleNotes",
    } as const)[key] ?? null;
    return { action: mappedAction, chordPending: false, handled: mappedAction !== null };
  }
  if (primary && key === "g") return { action: null, chordPending: true, handled: true };

  if (!editing) {
    if (messageAvailable && !event.metaKey && !event.altKey && !event.shiftKey) {
      if (key === "f3") return action(event.ctrlKey ? "messageFirst" : "messagePrevious");
      if (key === "f4" && !event.ctrlKey) return action("messageNext");
    }
    if (latestAvailable && key === "f4" && event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey)
      return action("messageLatest");
    if (selectedProjectFocused && key === "f2" && !primary && !event.altKey && !event.shiftKey) return action("renameProject");
    if (primary && key === "o") return action("openProject");
    if (primary && key === "f") return action("focusSearch");
    if (primary && key === ",") return action("settings");
    if (primary && event.shiftKey && key === "n") return action("toggleNotes");
    if (event.altKey && key === "arrowdown") return action("nextSession");
    if (event.altKey && key === "arrowup") return action("previousSession");
    if (event.altKey && key === "arrowright") return action("nextProject");
    if (event.altKey && key === "arrowleft") return action("previousProject");
    if (key === "f1" || key === "?") return action("help");
  }
  return { action: null, chordPending: false, handled: false };
}

function action(value: ShortcutAction): ShortcutResolution {
  return { action: value, chordPending: false, handled: true };
}
