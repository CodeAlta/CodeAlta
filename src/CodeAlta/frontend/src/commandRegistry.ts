import type { MessageKey } from "./localization";

/**
 * The application's commands: one list drives the command palette, the shortcut help and the keyboard
 * dispatcher. Names, labels and key gestures follow the terminal UI so both heads share one key map.
 */
export type CommandId =
  | "help" | "palette" | "openProject" | "about" | "skills" | "plugins" | "settings" | "prompts" | "nextPrompt"
  | "providers" | "models" | "logs" | "mcp" | "config" | "focusSidebar" | "toggleNavigator" | "focusPrompt" | "modelSelector"
  | "usage" | "sessionInfo" | "reminders" | "messagePrevious" | "messageNext" | "messageFirst" | "messageLatest"
  | "expandPrompt" | "send" | "steer" | "abort" | "closeTab" | "previousTab" | "nextTab" | "reopenTab" | "clearQueue" | "compact"
  | "newSession" | "browseSessions" | "searchSessions" | "toggleNotes" | "previousSession" | "nextSession"
  | "previousProject" | "nextProject" | "renameProject" | "refreshStatuses" | "exit";
export type CommandCategory = "General" | "Prompt" | "Session" | "Navigation" | "Inspection";
export type CommandDefinition = Readonly<{
  id: CommandId;
  /** The slash name shown in the palette, as in the terminal UI. */
  name: string;
  label: MessageKey;
  description: MessageKey;
  category: CommandCategory;
  /** Key gestures; a two-stroke chord is written with a space ("Ctrl+G Ctrl+T"). */
  keys?: readonly string[];
  /** Extra gestures listed in help that are handled elsewhere (typed in an empty prompt, or by the composer). */
  hints?: readonly string[];
  /** Additional words the palette search matches. */
  search?: string;
  /** Gestures that only apply while focus is not in a text field (they are ordinary typing or caret keys there). */
  outsideText?: boolean;
  /** Hidden from the palette (still listed in help when it has a gesture). */
  hidden?: boolean;
}>;

export const commandCategories: readonly CommandCategory[] = ["General", "Prompt", "Session", "Navigation", "Inspection"];

export const commandDefinitions: readonly CommandDefinition[] = Object.freeze([
  { id: "help", name: "help", label: "Help", description: "Show the commands and their shortcuts.", category: "General", keys: ["F1"], hints: ["?"], search: "? commands shortcuts keyboard" },
  { id: "palette", name: "command_palette", label: "Command Palette", description: "Search and run a command.", category: "General", keys: ["Ctrl+P"], hints: ["/"], search: "/ palette commands" },
  { id: "openProject", name: "open", label: "Open", description: "Open a project by name or folder.", category: "General", keys: ["Ctrl+O"], search: "project folder open_project open_folder" },
  { id: "newSession", name: "new_session", label: "New Session", description: "Start a new session in the selected project.", category: "General", search: "create session draft" },
  { id: "about", name: "about", label: "About", description: "Show the version of CodeAlta.", category: "General", keys: ["Ctrl+G Ctrl+A"] },
  { id: "exit", name: "exit", label: "Exit", description: "Close CodeAlta.", category: "General", keys: ["Ctrl+Q"], search: "quit close" },
  { id: "skills", name: "skills", label: "Skills", description: "Enable, disable and create skills.", category: "General", keys: ["Ctrl+G Ctrl+K"], search: "skill" },
  { id: "plugins", name: "plugins", label: "Plugins", description: "Enable or disable plugins.", category: "General", keys: ["Ctrl+G Ctrl+N"], search: "plugin" },
  { id: "mcp", name: "mcp", label: "MCP Servers", description: "Add and edit Model Context Protocol servers.", category: "General", keys: ["Ctrl+G Ctrl+Y"], search: "model context protocol servers tools" },
  { id: "settings", name: "settings", label: "Workspace Settings", description: "Theme, language and navigator preferences.", category: "General", keys: ["Ctrl+G Ctrl+W", "Ctrl+,"], search: "workspace_settings appearance" },
  { id: "config", name: "config", label: "Configuration File", description: "Edit the global configuration file.", category: "General", search: "config.toml" },
  { id: "prompts", name: "prompt", label: "Agent Prompts", description: "Create and edit agent and system prompts.", category: "General", keys: ["Ctrl+G Ctrl+H"], search: "prompts agent_prompt instructions system_prompt" },
  { id: "providers", name: "model_providers", label: "Model Providers", description: "Configure and test model providers.", category: "General", keys: ["Ctrl+G Ctrl+R"], search: "providers" },
  { id: "focusPrompt", name: "go_to_prompt", label: "Go to Prompt", description: "Move focus to the prompt editor.", category: "General", keys: ["Ctrl+G Ctrl+P"], search: "prompt" },
  { id: "focusSidebar", name: "go_to_sidebar", label: "Go to Sidebar", description: "Move focus to the project explorer.", category: "General", keys: ["Ctrl+G Ctrl+S"], search: "sidebar explorer" },
  { id: "modelSelector", name: "model", label: "Model", description: "Choose the agent, model and reasoning for the next send.", category: "General", search: "model_selector provider selector reasoning" },
  { id: "toggleNotes", name: "notes", label: "Toggle Notes", description: "Show or hide the session notes.", category: "General", keys: ["Ctrl+Shift+N"], search: "alta notes" },

  { id: "expandPrompt", name: "full_prompt", label: "Full Prompt", description: "Edit the prompt in a large window.", category: "Prompt", keys: ["F6"] },
  { id: "nextPrompt", name: "next_prompt", label: "Next Prompt", description: "Switch to the next agent prompt.", category: "Prompt", keys: ["Ctrl+T"], search: "agent prompt cycle switch next_agent_prompt" },
  { id: "send", name: "send", label: "Send", description: "Send the current prompt.", category: "Prompt", hints: ["Enter"] },
  { id: "steer", name: "steer", label: "Steer", description: "Send the prompt to the running turn.", category: "Prompt", hints: ["Ctrl+Enter"], hidden: true },

  { id: "abort", name: "abort", label: "Abort", description: "Stop the running turn.", category: "Session", keys: ["F8"], search: "stop cancel" },
  { id: "clearQueue", name: "clear_queue", label: "Clear Queue", description: "Remove every queued prompt.", category: "Session", keys: ["F10"] },
  { id: "compact", name: "compact", label: "Compact", description: "Compact the conversation of the idle session.", category: "Session", keys: ["Ctrl+F11"] },
  { id: "closeTab", name: "close_tab", label: "Close Tab", description: "Close the current session tab.", category: "Session", keys: ["Ctrl+W"], search: "close" },
  { id: "reopenTab", name: "reopen_tab", label: "Reopen Tab", description: "Reopen the last closed session tab.", category: "Session", keys: ["Ctrl+Shift+T"] },
  { id: "previousTab", name: "tab_left", label: "Tab Left", description: "Switch to the previous tab.", category: "Session", keys: ["Ctrl+Alt+Left", "Ctrl+PageUp"] },
  { id: "nextTab", name: "tab_right", label: "Tab Right", description: "Switch to the next tab.", category: "Session", keys: ["Ctrl+Alt+Right", "Ctrl+PageDown"] },
  { id: "reminders", name: "reminder", label: "Reminders", description: "Schedule prompts for this session.", category: "Session", keys: ["Ctrl+G Ctrl+D"], search: "reminders delayed_prompt" },
  { id: "browseSessions", name: "sessions", label: "Browse Sessions", description: "Find and open a saved session.", category: "Session", keys: ["Ctrl+Alt+B"], search: "saved sessions history" },
  { id: "searchSessions", name: "search_sessions", label: "Search Sessions", description: "Filter the sessions of the selected project.", category: "Session", keys: ["Ctrl+F"], outsideText: true },

  { id: "messagePrevious", name: "msg_prev", label: "Previous Message", description: "Scroll to the previous message.", category: "Navigation", keys: ["F3"] },
  { id: "messageNext", name: "msg_next", label: "Next Message", description: "Scroll to the next message.", category: "Navigation", keys: ["F4"] },
  { id: "messageFirst", name: "msg_first", label: "First Message", description: "Scroll to the first message.", category: "Navigation", keys: ["Ctrl+F3"] },
  { id: "messageLatest", name: "msg_last", label: "Last Message", description: "Scroll to the latest message.", category: "Navigation", keys: ["Ctrl+F4"] },
  { id: "toggleNavigator", name: "toggle_navigator", label: "Toggle Navigator", description: "Collapse or expand the project explorer.", category: "Navigation", keys: ["Ctrl+G Ctrl+G"] },
  { id: "previousSession", name: "session_prev", label: "Previous Session", description: "Select the previous session of the project.", category: "Navigation", keys: ["Alt+Up"], outsideText: true },
  { id: "nextSession", name: "session_next", label: "Next Session", description: "Select the next session of the project.", category: "Navigation", keys: ["Alt+Down"], outsideText: true },
  { id: "previousProject", name: "project_prev", label: "Previous Project", description: "Select the previous project.", category: "Navigation", keys: ["Alt+Left"], outsideText: true },
  { id: "nextProject", name: "project_next", label: "Next Project", description: "Select the next project.", category: "Navigation", keys: ["Alt+Right"], outsideText: true },
  { id: "renameProject", name: "rename_project", label: "Rename Project", description: "Rename the focused project.", category: "Navigation", keys: ["F2"], outsideText: true, hidden: true },

  { id: "usage", name: "context_usage", label: "Context Usage", description: "Show context and token usage.", category: "Inspection", keys: ["Ctrl+G Ctrl+U"] },
  { id: "models", name: "models", label: "Models", description: "Browse the models of every provider.", category: "Inspection", keys: ["Ctrl+G Ctrl+O"], search: "model_list" },
  { id: "sessionInfo", name: "session_info", label: "Session Info", description: "Show details of the selected session.", category: "Inspection", keys: ["Ctrl+G Ctrl+T"] },
  { id: "logs", name: "logs", label: "Show Logs", description: "Show the application logs.", category: "Inspection", keys: ["Ctrl+G Ctrl+L"], search: "show_logs" },
  { id: "refreshStatuses", name: "refresh_statuses", label: "Refresh Statuses", description: "Refresh the running state of open sessions.", category: "Inspection" },
] satisfies CommandDefinition[]);

export type CommandKey = Readonly<{ key: string; ctrlKey?: boolean; altKey?: boolean; shiftKey?: boolean; metaKey?: boolean;
  isComposing?: boolean; keyCode?: number; defaultPrevented?: boolean; repeat?: boolean }>;
/** Where keyboard focus is: the prompt editor, another text field, or not in text at all. */
export type CommandFocus = "prompt" | "text" | "none";
export type CommandResolution = Readonly<{ command: CommandId | null; chord: boolean; handled: boolean }>;

const chordPrefix = "ctrl+g";
const keyNames: Readonly<Record<string, string>> = { left: "arrowleft", right: "arrowright", up: "arrowup", down: "arrowdown" };
// "Ctrl+Alt+Left" -> "ctrl+alt+arrowleft": modifiers in a fixed order, then the lower-case key.
function normalize(gesture: string): string {
  const parts = gesture.toLowerCase().split("+");
  const key = parts.pop()!;
  const has = (modifier: string) => parts.includes(modifier);
  return `${has("ctrl") ? "ctrl+" : ""}${has("alt") ? "alt+" : ""}${has("shift") ? "shift+" : ""}${keyNames[key] ?? key}`;
}
function stroke(event: CommandKey): string {
  return `${event.ctrlKey ? "ctrl+" : ""}${event.altKey ? "alt+" : ""}${event.shiftKey ? "shift+" : ""}${event.key.toLowerCase()}`;
}
const single = new Map<string, CommandDefinition>();
const chords = new Map<string, CommandDefinition>();
for (const command of commandDefinitions) for (const gesture of command.keys ?? []) {
  const strokes = gesture.split(" ").map(normalize);
  if (strokes.length === 1) single.set(strokes[0], command);
  else if (strokes[0] === chordPrefix) chords.set(strokes[1].replace(/^ctrl\+/u, ""), command);
}

/**
 * Maps a key press to a command. `chord` is true after Ctrl+G, when the next key (with or without
 * Ctrl held) completes a two-stroke gesture. A handled key must not reach the page or the browser.
 */
export function resolveCommandKey(event: CommandKey, chord: boolean, focus: CommandFocus): CommandResolution {
  const none: CommandResolution = { command: null, chord: false, handled: false };
  if (event.isComposing || event.keyCode === 229 || event.defaultPrevented || event.metaKey) return none;
  const key = event.key.toLowerCase();
  if (["control", "shift", "alt", "meta"].includes(key)) return { command: null, chord, handled: false };
  if (chord) {
    if (event.altKey || event.shiftKey) return none;
    const command = chords.get(key);
    // An unknown second stroke ends the chord and is swallowed, so it is not typed into the prompt.
    return { command: command?.id ?? null, chord: false, handled: true };
  }
  const pressed = stroke(event);
  if (pressed === chordPrefix) return event.repeat ? none : { command: null, chord: true, handled: true };
  const command = single.get(pressed);
  if (!command || command.outsideText && focus !== "none") return none;
  // Holding a key down does not run a command repeatedly, but the key stays claimed.
  return { command: event.repeat ? null : command.id, chord: false, handled: true };
}

/** The gestures of a command as shown to the user (its key bindings, then the typed hints). */
export function commandKeys(command: CommandDefinition): readonly string[] {
  return [...(command.keys ?? []), ...(command.hints ?? [])];
}

export type RankedCommand = Readonly<{ command: CommandDefinition; score: number }>;

/**
 * Palette search with the terminal UI's ranking: per field, an exact match beats a prefix, a word
 * start and then any substring; the slash name outranks the label, extra search words and description.
 * Every word of the query must match somewhere. An empty query lists everything in registry order.
 */
export function searchCommands(query: string, label: (command: CommandDefinition) => string,
  description: (command: CommandDefinition) => string): CommandDefinition[] {
  const visible = commandDefinitions.filter(command => !command.hidden);
  const words = query.trim().toLowerCase().replace(/^\//u, "").split(/\s+/u).filter(Boolean);
  if (!words.length) return visible;
  const fieldScore = (text: string, word: string): number | null => {
    const value = text.toLowerCase();
    if (value === word) return 0;
    if (value.startsWith(word)) return 1000;
    const index = value.indexOf(word);
    if (index < 0) return null;
    return /[\s_/-]/u.test(value[index - 1]) ? 2000 + index : 3000 + index;
  };
  const ranked: RankedCommand[] = [];
  visible.forEach(command => {
    const fields: [string, number][] = [[command.name, 0], [label(command), 10], [command.label, 10], [command.search ?? "", 20], [description(command), 30]];
    let total = 0;
    for (const word of words) {
      let best: number | null = null;
      for (const [text, bias] of fields) {
        const score = fieldScore(text, word);
        if (score !== null && (best === null || score + bias < best)) best = score + bias;
      }
      if (best === null) return;
      total += best;
    }
    ranked.push({ command, score: total });
  });
  return ranked.sort((left, right) => left.score - right.score).map(item => item.command);
}
