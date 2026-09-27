import type { SessionInfoSelection } from "./sessionInfo";
import type { MessageKey } from "./localization";

export type PaletteAction = "chooseModel" | "skills" | "usage" | "openProject" | "help" | "settings" | "about" | "logs" | "providers" | "models" | "prompts" | "mcp" | "reminders" | "sessionInfo" | "focusPrompt" | "focusSearch" | "browseSessions" | "refreshStatuses" | "nextTab" | "previousTab" | "closeTab" | "reopenTab";
export type PaletteContext = Readonly<{
  workspace: boolean;
  selection: SessionInfoSelection | null;
  epoch: string | null;
  infoReady: boolean;
  promptReady: boolean;
  localDraftScope?: string;
  searchReady: boolean;
  browserScope?: string;
  runtimeScope?: string;
  tabSelection?: string | null;
  tabsReady?: boolean;
  reopenReady?: boolean;
  commandGeneration?: number;
  accessScope?: string;
  skillsReady?: boolean;
  usageReady?: boolean;
  usageTrigger?: HTMLButtonElement | null;
  shellReady?: boolean;
  modelChooserReady?: boolean;
  modelChooserTrigger?: HTMLButtonElement | null;
}>;
export type PaletteCommand = Readonly<{ id: PaletteAction; label: MessageKey; aliases?: string; shortcut?: string; chord?: "k" | "l" }>;

const commands: readonly PaletteCommand[] = Object.freeze(([
  { id: "settings", label: "Settings" }, { id: "about", label: "About" }, { id: "logs", label: "Application Logs", aliases: "logs show_logs", shortcut: "Ctrl+G, Ctrl+L", chord: "l" }, { id: "providers", label: "Providers" },
  { id: "models", label: "Models" }, { id: "prompts", label: "Agent Prompts" },
  { id: "chooseModel", label: "Next Send model selection", aliases: "model reasoning effort cached chooser" },
  { id: "mcp", label: "MCP Servers" }, { id: "reminders", label: "Reminders" },
  { id: "sessionInfo", label: "Session Info" }, { id: "focusPrompt", label: "Focus prompt" },
  { id: "focusSearch", label: "Focus session search" },
  { id: "browseSessions", label: "Browse saved sessions (Ctrl+Alt+B outside text)" },
  { id: "refreshStatuses", label: "Refresh statuses of open session tabs (observed, not live)" },
  { id: "nextTab", label: "Next session tab (Ctrl+PageDown outside text)" },
  { id: "previousTab", label: "Previous session tab (Ctrl+PageUp outside text)" },
  { id: "closeTab", label: "Close session tab (Ctrl+W outside text; does not stop session)" },
  { id: "reopenTab", label: "Reopen closed session tab (Ctrl+Shift+T outside text)" },
  { id: "skills", label: "Skills", aliases: "skills skill inspection", shortcut: "Ctrl+G, Ctrl+K", chord: "k" },
  { id: "usage", label: "Inspect last-observed session usage", aliases: "usage context_usage" },
  { id: "openProject", label: "Open project", aliases: "open_project", shortcut: "Ctrl+O" },
  { id: "help", label: "Keyboard shortcuts", aliases: "help shortcuts", shortcut: "F1 / ?" },
] satisfies PaletteCommand[]).map(command => Object.freeze(command)));

export function paletteAvailable(id: PaletteAction, captured: PaletteContext, current: PaletteContext): boolean {
  if (captured.commandGeneration !== current.commandGeneration) return false;
  // App captures are exact, including for global destinations: an old selection must
  // not execute after a capability changes without a catalog render/generation bump.
  if (captured.commandGeneration !== undefined && (captured.workspace !== current.workspace || captured.epoch !== current.epoch
    || captured.selection?.sessionId !== current.selection?.sessionId || captured.selection?.projectId !== current.selection?.projectId
    || captured.accessScope !== current.accessScope)) return false;
  if (id === "openProject" || id === "help") return !!captured.shellReady && !!current.shellReady;
  if (id === "skills" || id === "usage") return captured.workspace && current.workspace && !!captured.epoch && captured.epoch === current.epoch
    && !!captured.accessScope && captured.accessScope === current.accessScope
    && !!captured.selection && captured.selection.sessionId === current.selection?.sessionId && captured.selection.projectId === current.selection.projectId
    && (id === "skills" ? !!captured.skillsReady && !!current.skillsReady : !!captured.usageReady && !!current.usageReady && captured.usageTrigger === current.usageTrigger);
  if (id === "refreshStatuses") return captured.workspace && current.workspace && !!captured.runtimeScope && captured.runtimeScope === current.runtimeScope;
  if (id === "browseSessions") return captured.workspace && current.workspace && !!captured.browserScope && captured.browserScope === current.browserScope;
  if (["nextTab", "previousTab", "closeTab", "reopenTab"].includes(id))
    return captured.workspace && current.workspace && captured.tabSelection === current.tabSelection &&
      (id === "reopenTab" ? !!captured.reopenReady && !!current.reopenReady : !!captured.tabsReady && !!current.tabsReady &&
        (id !== "closeTab" || !!current.tabSelection));
  const sameSelection = !!captured.selection && !!current.selection &&
    captured.selection.sessionId === current.selection.sessionId && captured.selection.projectId === current.selection.projectId;
  if (id === "chooseModel") return captured.workspace && current.workspace && sameSelection
    && !!captured.epoch && captured.epoch === current.epoch && !!captured.modelChooserReady && !!current.modelChooserReady
    && captured.modelChooserTrigger === current.modelChooserTrigger;
  if (id === "sessionInfo" || id === "reminders") {
    if (!captured.workspace || !current.workspace || !sameSelection || !captured.infoReady || !current.infoReady) return false;
    return id === "sessionInfo" || !!captured.epoch && captured.epoch === current.epoch;
  }
  if (id === "focusPrompt" || id === "focusSearch")
    return captured.workspace && current.workspace && (id === "focusPrompt"
      ? (sameSelection || !captured.selection && !current.selection && !!captured.localDraftScope && captured.localDraftScope === current.localDraftScope)
        && captured.epoch === current.epoch && captured.promptReady && current.promptReady
      : captured.searchReady && current.searchReady);
  return commands.some(command => command.id === id);
}

// Shared presentation metadata; availability remains a separate, revalidated contract.
export const commandAccessHelp = Object.freeze(commands.filter(command => ["skills", "logs", "usage", "openProject", "help"].includes(command.id)));
export function commandAccessChord(key: string): PaletteAction | null {
  return commandAccessHelp.find(command => command.chord === key)?.id ?? null;
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
