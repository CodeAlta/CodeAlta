import type { SessionInfoSelection } from "./sessionInfo";

export type PaletteAction = "settings" | "about" | "logs" | "providers" | "models" | "prompts" | "mcp" | "reminders" | "sessionInfo" | "focusPrompt" | "focusSearch";
export type PaletteContext = Readonly<{
  workspace: boolean;
  selection: SessionInfoSelection | null;
  epoch: string | null;
  infoReady: boolean;
  promptReady: boolean;
  searchReady: boolean;
}>;
export type PaletteCommand = Readonly<{ id: PaletteAction; label: string }>;

const commands: readonly PaletteCommand[] = Object.freeze(([
  { id: "settings", label: "Settings" }, { id: "about", label: "About" }, { id: "logs", label: "Application Logs" }, { id: "providers", label: "Providers" },
  { id: "models", label: "Models" }, { id: "prompts", label: "Agent Prompts" },
  { id: "mcp", label: "MCP Servers" }, { id: "reminders", label: "Reminders" },
  { id: "sessionInfo", label: "Session Info" }, { id: "focusPrompt", label: "Focus prompt" },
  { id: "focusSearch", label: "Focus session search" },
] satisfies PaletteCommand[]).map(command => Object.freeze(command)));

export function paletteAvailable(id: PaletteAction, captured: PaletteContext, current: PaletteContext): boolean {
  const sameSelection = !!captured.selection && !!current.selection &&
    captured.selection.sessionId === current.selection.sessionId && captured.selection.projectId === current.selection.projectId;
  if (id === "sessionInfo" || id === "reminders") {
    if (!captured.workspace || !current.workspace || !sameSelection || !captured.infoReady || !current.infoReady) return false;
    return id === "sessionInfo" || !!captured.epoch && captured.epoch === current.epoch;
  }
  if (id === "focusPrompt" || id === "focusSearch")
    return captured.workspace && current.workspace && (id === "focusPrompt"
      ? sameSelection && captured.epoch === current.epoch && captured.promptReady && current.promptReady
      : captured.searchReady && current.searchReady);
  return commands.some(command => command.id === id);
}

export function paletteCommands(context: PaletteContext): readonly PaletteCommand[] {
  return commands.filter(command => paletteAvailable(command.id, context, context));
}

export function paletteShortcut(event: KeyboardEvent, modalOpen: boolean): boolean {
  const target = event.target instanceof HTMLElement ? event.target : null;
  return event.key.toLowerCase() === "p" && event.ctrlKey && !event.metaKey && !event.altKey && !event.shiftKey &&
    !event.repeat && !event.defaultPrevented && !event.isComposing && event.keyCode !== 229 && !modalOpen &&
    !target?.closest("input, textarea, select, [contenteditable], dialog[open], [role='dialog'][aria-modal='true']");
}

export function restorePaletteFocus(origin: HTMLElement | null, sameView: boolean, modalOpen: boolean): boolean {
  if (!sameView || modalOpen || !origin?.isConnected) return false;
  origin.focus();
  return true;
}

// One owner per mounted window: a deferred modal close may not override a newer
// focus move, another modal, or a navigation made before the next animation frame.
export function createPaletteFocusRestoration() {
  let generation = 0;
  let frame: number | null = null;
  function cancel() {
    generation++;
    if (frame !== null) cancelAnimationFrame(frame);
    frame = null;
  }
  return {
    cancel,
    schedule(origin: HTMLElement | null, sameView: () => boolean, modalOpen: () => boolean) {
      cancel();
      const current = generation;
      const focusedAtClose = document.activeElement;
      frame = requestAnimationFrame(() => {
        frame = null;
        if (current !== generation) return;
        const focused = document.activeElement;
        if (focused !== focusedAtClose && focused !== document.body && focused !== document.documentElement &&
          focused instanceof HTMLElement && focused.isConnected && focused !== origin) return;
        restorePaletteFocus(origin, sameView(), modalOpen());
      });
    },
  };
}
