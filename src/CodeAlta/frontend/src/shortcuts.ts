export type ShortcutAction =
  | "openProject" | "focusProjects" | "focusSessions" | "focusPrompt" | "focusSearch"
  | "nextProject" | "previousProject" | "nextSession" | "previousSession"
  | "settings" | "providers" | "models" | "prompts" | "context" | "plugins"
  | "toggleNotes" | "help" | "escape";

export type ShortcutKey = Readonly<{ key: string; ctrlKey?: boolean; altKey?: boolean; shiftKey?: boolean; metaKey?: boolean }>;
export type ShortcutResolution = Readonly<{ action: ShortcutAction | null; chordPending: boolean; handled: boolean }>;

export function resolveShortcut(event: ShortcutKey, chordPending: boolean, editing: boolean): ShortcutResolution {
  const key = event.key.toLowerCase();
  const primary = event.ctrlKey === true || event.metaKey === true;

  if (key === "escape") return { action: "escape", chordPending: false, handled: true };
  if (chordPending) {
    const action = ({
      s: "focusProjects", p: "focusPrompt", w: "settings", r: "providers", o: "models",
      h: "prompts", u: "context", n: "plugins", g: "toggleNotes",
    } as const)[key] ?? null;
    return { action, chordPending: false, handled: action !== null };
  }
  if (primary && key === "g") return { action: null, chordPending: true, handled: true };

  if (!editing) {
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
