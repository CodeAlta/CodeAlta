import type { MessageKey } from "./localization";

/**
 * The application's commands: one list drives the commands of the search window, the shortcut help and the
 * keyboard dispatcher. Names, labels and key gestures follow the terminal UI so both heads share one key map.
 */
export type CommandId =
  | "help" | "palette" | "openProject" | "editFile" | "projectEditor" | "externalEditor" | "fileExplorer" | "newTerminal" | "automations" | "workflows" | "workItems" | "reviews" | "issues" | "canvases" | "worktrees" | "documentation" | "bookmarks" | "about" | "skills" | "plugins" | "settings" | "prompts" | "nextPrompt"
  | "providers" | "models" | "logs" | "mcp" | "config" | "focusSidebar" | "toggleNavigator" | "focusPrompt" | "focusAskFile" | "modelSelector"
  | "usage" | "sessionInfo" | "reminders" | "messagePrevious" | "messageNext" | "messageFirst" | "messageLatest" | "bookmarkPrevious" | "bookmarkNext" | "bookmarkSession"
  | "attachFiles" | "attachFolder" | "attachIssues"
  | "expandPrompt" | "insertEquation" | "dictate" | "send" | "steer" | "abort" | "closeTab" | "previousTab" | "nextTab" | "reopenTab" | "moveTabToNewWindow" | "clearQueue" | "compact" | "fork" | "rewind"
  | "newSession" | "browseSessions" | "searchSessions" | "toggleNotes" | "previousSession" | "nextSession"
  | "previousProject" | "nextProject" | "renameProject" | "refreshStatuses" | "zoomIn" | "zoomOut" | "resetZoom" | "exit"
  | "spaces" | "newSpace" | "goToSpace" | "previousSpace" | "nextSpace" | "moveSession" | "reviewChanges"
  | "openInVsCode" | "copyResumeCommand" | "runInTerminal";
export type CommandCategory = "General" | "Prompt" | "Session" | "Navigation" | "Inspection";
export type CommandDefinition = Readonly<{
  id: CommandId;
  /** The slash name shown in the search window, as in the terminal UI. */
  name: string;
  label: MessageKey;
  description: MessageKey;
  category: CommandCategory;
  /** Key gestures; a two-stroke chord is written with a space ("Ctrl+G Ctrl+T"). */
  keys?: readonly string[];
  /** The gestures on macOS, in place of `keys`, where the system has its own for the action ("Cmd+U"). */
  macKeys?: readonly string[];
  /** Extra gestures listed in help that are handled elsewhere (typed in an empty prompt, or by the composer). */
  hints?: readonly string[];
  /** Additional words the search matches. */
  search?: string;
  /** Gestures that only apply while focus is not in a text field (they are ordinary typing or caret keys there). */
  outsideText?: boolean;
  /** Hidden from the search window (still listed in help when it has a gesture). */
  hidden?: boolean;
}>;

export const commandCategories: readonly CommandCategory[] = ["General", "Prompt", "Session", "Navigation", "Inspection"];

export const commandDefinitions: readonly CommandDefinition[] = Object.freeze([
  { id: "help", name: "help", label: "Help", description: "Show the commands and their shortcuts.", category: "General", keys: ["F1"], hints: ["?"], search: "? commands shortcuts keyboard" },
  { id: "palette", name: "search", label: "Search", description: "Search projects, sessions, files and commands.", category: "General", keys: ["Ctrl+P"], hints: ["/"], search: "/ palette commands command_palette find go to" },
  { id: "openProject", name: "open", label: "Open", description: "Open a project by name or folder.", category: "General", keys: ["Ctrl+O"], search: "project folder open_project open_folder" },
  { id: "editFile", name: "edit", label: "Edit File", description: "Open a project file in the code editor.", category: "General", keys: ["Ctrl+E"], search: "open_file open file editor view" },
  { id: "projectEditor", name: "editor", label: "Project Editor", description: "Open the code editor of the project with its files.", category: "General", hints: ["Ctrl+E Ctrl+E"], search: "code editor files explorer tree folders project_editor" },
  { id: "externalEditor", name: "external_editor", label: "External Editor", description: "Open the folder of the session or of the project in the external editor chosen in Settings.", category: "General", search: "vscode visual studio code cursor windsurf zed sublime open_in" },
  { id: "fileExplorer", name: "file_explorer", label: "File Manager", description: "Show the folder of the session or of the project in the file manager of the system.", category: "General", search: "explorer finder folder reveal show_in_folder files" },
  { id: "newTerminal", name: "terminal", label: "New Terminal", description: "Open a terminal in the folder of the session or of the project.", category: "General", keys: ["Ctrl+G Ctrl+J", "Ctrl+`"], search: "shell console command line powershell bash new_terminal" },
  { id: "automations", name: "automations", label: "Automations", description: "Open the automations: prompts that start a session by themselves.", category: "General", keys: ["Ctrl+G Ctrl+M"], search: "schedule cron trigger daily hourly weekly task routine recurring" },
  { id: "workflows", name: "workflows", label: "Workflows", description: "Open the workflows: the scripts of the library and their runs.", category: "General", search: "code mode scripts script library workflow runs run steps exec" },
  { id: "workItems", name: "work_items", label: "Work items", description: "Open the work items: the tasks agents proposed and the plans of the projects.", category: "General", keys: ["Ctrl+G Ctrl+I"], search: "tasks plans todo backlog follow-up proposals pending work_items" },
  { id: "issues", name: "issues", label: "Issues", description: "Open the issues and the pull requests of the projects.", category: "General", keys: ["Ctrl+G Ctrl+B"], search: "issues pull requests merge requests prs bugs tickets github gitlab azure devops bitbucket jira" },
  { id: "canvases", name: "canvases", label: "Canvases", description: "Open the canvases that plugins provide: boards, checklists, dashboards.", category: "General", search: "plugin tabs board checklist dashboard canvas" },
  { id: "worktrees", name: "worktree", label: "Worktrees", description: "See the worktrees of the project, and remove the ones that are no longer needed.", category: "General", search: "worktrees git checkout checkouts branch remove delete clean cleanup disk" },
  { id: "documentation", name: "documentation", label: "Documentation", description: "Open the user guide of CodeAlta.", category: "General", search: "docs doc guide help manual user guide readme how to" },
  { id: "reviews", name: "reviews", label: "Reviews", description: "Open the reviews in flight: the plans, files and changes that wait for you or for an agent.", category: "General", search: "review reviews annotate annotations comments diff changes plan approve verdict waiting" },
  { id: "bookmarks", name: "bookmarks", label: "Bookmarks", description: "Open the bookmarks of every project.", category: "General", keys: ["Ctrl+G Ctrl+F"], search: "bookmark saved marked favorites starred pinned keep find again" },
  { id: "spaces", name: "spaces", label: "Spaces", description: "Create the spaces and choose the projects of each.", category: "General", search: "space group projects organize workspaces" },
  { id: "newSpace", name: "new_space", label: "New Space", description: "Create a space: a group of projects shown together.", category: "General", search: "space create group projects workspace" },
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
  { id: "focusAskFile", name: "go_to_ask_file", label: "Go to Ask File", description: "Move focus to the file under review.", category: "General", keys: ["Ctrl+G Ctrl+E"], search: "plan review ask file" },
  { id: "focusSidebar", name: "go_to_sidebar", label: "Go to Sidebar", description: "Move focus to the project explorer.", category: "General", keys: ["Ctrl+G Ctrl+S"], search: "sidebar explorer" },
  { id: "modelSelector", name: "model", label: "Model", description: "Choose the agent, model and reasoning for the next send.", category: "General", search: "model_selector provider selector reasoning" },
  { id: "zoomIn", name: "zoom_in", label: "Zoom In", description: "Make the text and the controls of the window larger.", category: "General", keys: ["Ctrl+=", "Ctrl+Plus"], search: "zoom larger bigger font text size scale" },
  { id: "zoomOut", name: "zoom_out", label: "Zoom Out", description: "Make the text and the controls of the window smaller.", category: "General", keys: ["Ctrl+-"], search: "zoom smaller font text size scale" },
  { id: "resetZoom", name: "reset_zoom", label: "Reset Zoom", description: "Show the window at 100% again.", category: "General", keys: ["Ctrl+0"], search: "zoom actual size font text size scale" },
  { id: "toggleNotes", name: "notes", label: "Toggle Notes", description: "Show or hide the session notes.", category: "General", keys: ["Ctrl+Shift+N"], search: "alta notes" },

  { id: "attachFiles", name: "attach_files", label: "Attach Files", description: "Add files or photos to the prompt.", category: "Prompt", keys: ["Ctrl+U"], macKeys: ["Cmd+U"], search: "attachment upload image photo pdf document add_files" },
  { id: "attachFolder", name: "attach_folder", label: "Attach Folder", description: "Add a folder to the prompt.", category: "Prompt", keys: ["Ctrl+Alt+F"], search: "attachment directory add_folder" },
  { id: "attachIssues", name: "attach_issues", label: "Attach Issues", description: "Add issues of the project to the prompt.", category: "Prompt", keys: ["Ctrl+Alt+I"], search: "attachment github gitlab azure devops work items add_issues" },
  { id: "expandPrompt", name: "full_prompt", label: "Full Prompt", description: "Edit the prompt in a large window.", category: "Prompt", keys: ["F6"] },
  // Word's key for an equation; Option+= types ≠ on macOS, where Ctrl+Alt+= takes its place.
  { id: "insertEquation", name: "equation", label: "Insert equation", description: "Write an equation visually and insert its LaTeX in the prompt.", category: "Prompt",
    keys: ["Alt+="], macKeys: ["Ctrl+Alt+="], search: "math maths formula latex katex mathlive tex equation editor symbol" },
  { id: "dictate", name: "dictate", label: "Dictate", description: "Dictate the prompt with the microphone, or stop the dictation.", category: "Prompt", keys: ["F9"], search: "speech voice microphone speak talk transcribe stt" },
  { id: "nextPrompt", name: "next_prompt", label: "Next Prompt", description: "Switch to the next agent prompt.", category: "Prompt", keys: ["Ctrl+T"], search: "agent prompt cycle switch next_agent_prompt" },
  { id: "send", name: "send", label: "Send", description: "Send the current prompt.", category: "Prompt", hints: ["Enter"] },
  { id: "steer", name: "steer", label: "Steer", description: "Send the prompt to the running turn.", category: "Prompt", hints: ["Ctrl+Enter"], hidden: true },

  { id: "abort", name: "abort", label: "Abort", description: "Stop the running turn.", category: "Session", keys: ["F8"], search: "stop cancel" },
  { id: "clearQueue", name: "clear_queue", label: "Clear Queue", description: "Remove every queued prompt.", category: "Session", keys: ["F10"] },
  { id: "compact", name: "compact", label: "Compact", description: "Compact the conversation of the idle session.", category: "Session", keys: ["Ctrl+F11"] },
  { id: "fork", name: "fork", label: "Fork Session", description: "Continue from a point of the session in a new session.", category: "Session", search: "branch duplicate copy clone" },
  { id: "rewind", name: "rewind", label: "Rewind Session", description: "Take the session back to an earlier turn.", category: "Session", search: "undo roll back revert" },
  { id: "reviewChanges", name: "review_changes", label: "Review Changes", description: "Comment on the changes of the session's checkout, then submit the review.", category: "Session",
    search: "review comment diff changes approve request" },
  { id: "bookmarkSession", name: "bookmark", label: "Bookmark Session", description: "Bookmark or remove the bookmark of this session.", category: "Session", search: "bookmark mark save favorite star pin keep" },
  { id: "moveSession", name: "move_to_project", label: "Move to Project", description: "Move the selected session to another project.", category: "Session",
    search: "move session project worktree attach transfer teleport" },
  { id: "openInVsCode", name: "open_in_vscode", label: "Open in VS Code", description: "Open the Claude Code conversation of this session in VS Code.", category: "Session",
    search: "vscode visual studio code editor extension claude code continue elsewhere" },
  { id: "runInTerminal", name: "run_in_terminal", label: "Run in terminal", description: "Continue the Claude Code conversation of this session in a new terminal.", category: "Session",
    search: "claude cli resume terminal shell console continue elsewhere" },
  { id: "copyResumeCommand", name: "copy_resume_command", label: "Copy resume command", description: "Copy the command that continues this session in a terminal: claude --resume.", category: "Session",
    search: "claude cli resume command clipboard terminal continue elsewhere" },
  { id: "closeTab", name: "close_tab", label: "Close Tab", description: "Close the current tab.", category: "Session", keys: ["Ctrl+W", "Ctrl+Shift+W"], search: "close" },
  { id: "reopenTab", name: "reopen_tab", label: "Reopen Tab", description: "Reopen the last closed tab.", category: "Session", keys: ["Ctrl+Shift+T"] },
  { id: "moveTabToNewWindow", name: "move_tab_to_window", label: "Move tab to new window", description: "Move the tab in front to a window of its own.", category: "Session",
    search: "window detach tear out new window move tab monitor screen" },
  { id: "previousTab", name: "tab_left", label: "Tab Left", description: "Switch to the previous tab.", category: "Session", keys: ["Ctrl+Shift+Tab", "Ctrl+PageUp", "Ctrl+Alt+Left"] },
  { id: "nextTab", name: "tab_right", label: "Tab Right", description: "Switch to the next tab.", category: "Session", keys: ["Ctrl+Tab", "Ctrl+PageDown", "Ctrl+Alt+Right"] },
  { id: "reminders", name: "reminder", label: "Reminders", description: "Schedule prompts for this session.", category: "Session", keys: ["Ctrl+G Ctrl+D"], search: "reminders delayed_prompt" },
  { id: "browseSessions", name: "sessions", label: "Browse Sessions", description: "Find and open a saved session.", category: "Session", keys: ["Ctrl+Alt+B"], search: "saved sessions history" },
  { id: "searchSessions", name: "search_sessions", label: "Search Sessions", description: "Search the sessions of every project.", category: "Session", keys: ["Ctrl+F"], outsideText: true, search: "find session chat" },

  { id: "messagePrevious", name: "msg_prev", label: "Previous Message", description: "Scroll to the previous message.", category: "Navigation", keys: ["F3"] },
  { id: "messageNext", name: "msg_next", label: "Next Message", description: "Scroll to the next message.", category: "Navigation", keys: ["F4"] },
  { id: "messageFirst", name: "msg_first", label: "First Message", description: "Scroll to the first message.", category: "Navigation", keys: ["Ctrl+F3"] },
  { id: "messageLatest", name: "msg_last", label: "Last Message", description: "Scroll to the latest message.", category: "Navigation", keys: ["Ctrl+F4"] },
  { id: "bookmarkPrevious", name: "bookmark_prev", label: "Previous Bookmark", description: "Go to the previous bookmarked message of this session.", category: "Navigation", keys: ["Shift+F3"], search: "bookmark previous up" },
  { id: "bookmarkNext", name: "bookmark_next", label: "Next Bookmark", description: "Go to the next bookmarked message of this session.", category: "Navigation", keys: ["Shift+F4"], search: "bookmark next down" },
  { id: "toggleNavigator", name: "toggle_navigator", label: "Toggle Navigator", description: "Collapse or expand the project explorer.", category: "Navigation", keys: ["Ctrl+G Ctrl+G"] },
  { id: "previousSession", name: "session_prev", label: "Previous Session", description: "Select the previous session of the project.", category: "Navigation", keys: ["Alt+Up"], outsideText: true },
  { id: "nextSession", name: "session_next", label: "Next Session", description: "Select the next session of the project.", category: "Navigation", keys: ["Alt+Down"], outsideText: true },
  { id: "previousProject", name: "project_prev", label: "Previous Project", description: "Select the previous project.", category: "Navigation", keys: ["Alt+Left"], outsideText: true },
  { id: "nextProject", name: "project_next", label: "Next Project", description: "Select the next project.", category: "Navigation", keys: ["Alt+Right"], outsideText: true },
  { id: "goToSpace", name: "space", label: "Go to Space", description: "Show another space.", category: "Navigation", keys: ["Ctrl+G Ctrl+V"], hints: ["Ctrl+G 1-9"], search: "space switch workspace" },
  { id: "previousSpace", name: "space_prev", label: "Previous Space", description: "Show the previous space.", category: "Navigation", keys: ["Ctrl+Alt+PageUp"], search: "space switch workspace" },
  { id: "nextSpace", name: "space_next", label: "Next Space", description: "Show the next space.", category: "Navigation", keys: ["Ctrl+Alt+PageDown"], search: "space switch workspace" },
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
// "Plus" names the "+" key, which cannot be written in a gesture whose parts "+" separates.
const keyNames: Readonly<Record<string, string>> = { left: "arrowleft", right: "arrowright", up: "arrowup", down: "arrowdown", plus: "+" };
// "Ctrl+Alt+Left" -> "ctrl+alt+arrowleft": modifiers in a fixed order, then the lower-case key. Cmd is the Command key of macOS.
function normalize(gesture: string): string {
  const parts = gesture.toLowerCase().split("+");
  const key = parts.pop()!;
  const has = (modifier: string) => parts.includes(modifier);
  return `${has("ctrl") ? "ctrl+" : ""}${has("alt") ? "alt+" : ""}${has("shift") ? "shift+" : ""}${has("cmd") ? "cmd+" : ""}${keyNames[key] ?? key}`;
}
function stroke(event: CommandKey): string {
  // Shift is how "+" is typed on most layouts (Shift+=), so it is part of the key rather than a modifier.
  const shift = event.shiftKey && event.key !== "+";
  return `${event.ctrlKey ? "ctrl+" : ""}${event.altKey ? "alt+" : ""}${shift ? "shift+" : ""}${event.metaKey ? "cmd+" : ""}${event.key.toLowerCase()}`;
}
/** Whether the page runs on macOS, where a command may have gestures of its own (`macKeys`). */
export function isMacPlatform(): boolean {
  return typeof navigator !== "undefined" && /^Mac/u.test(navigator.platform ?? "");
}
// The gestures of each platform: macOS takes `macKeys` where a command has them.
function keymap(mac: boolean) {
  const single = new Map<string, CommandDefinition>();
  const chords = new Map<string, CommandDefinition>();
  for (const command of commandDefinitions) for (const gesture of (mac ? command.macKeys ?? command.keys : command.keys) ?? []) {
    const strokes = gesture.split(" ").map(normalize);
    if (strokes.length === 1) single.set(strokes[0], command);
    else if (strokes[0] === chordPrefix) chords.set(strokes[1].replace(/^ctrl\+/u, ""), command);
  }
  return { single, chords };
}
const keymaps = Object.freeze({ other: keymap(false), mac: keymap(true) });

/**
 * Maps a key press to a command. `chord` is true after Ctrl+G, when the next key (with or without
 * Ctrl held) completes a two-stroke gesture. A handled key must not reach the page or the browser.
 */
export function resolveCommandKey(event: CommandKey, chord: boolean, focus: CommandFocus, mac = isMacPlatform()): CommandResolution {
  const none: CommandResolution = { command: null, chord: false, handled: false };
  if (event.isComposing || event.keyCode === 229 || event.defaultPrevented) return none;
  const { single, chords } = mac ? keymaps.mac : keymaps.other;
  // The Command key only takes the gestures that name it (`macKeys`): everything else it does stays the system's.
  if (event.metaKey) {
    const command = !chord && !event.repeat ? single.get(stroke(event)) : undefined;
    return command && (!command.outsideText || focus === "none") ? { command: command.id, chord: false, handled: true } : none;
  }
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
export function commandKeys(command: CommandDefinition, mac = isMacPlatform()): readonly string[] {
  return [...((mac ? command.macKeys ?? command.keys : command.keys) ?? []), ...(command.hints ?? [])];
}

export type RankedCommand = Readonly<{ command: CommandDefinition; score: number }>;

/**
 * Command search with the terminal UI's ranking: per field, an exact match beats a prefix, a word
 * start and then any substring; the slash name outranks the label, extra search words and description.
 * Every word of the query must match somewhere. An empty query lists everything in registry order.
 */
export function searchCommands(query: string, label: (command: CommandDefinition) => string,
  description: (command: CommandDefinition) => string): CommandDefinition[] {
  return rankCommands(query, label, description).map(item => item.command);
}

/** The commands {@link searchCommands} finds, each with its score: lower is better, and 0 when nothing is typed. */
export function rankCommands(query: string, label: (command: CommandDefinition) => string,
  description: (command: CommandDefinition) => string): RankedCommand[] {
  const visible = commandDefinitions.filter(command => !command.hidden);
  const words = query.trim().toLowerCase().replace(/^\//u, "").split(/\s+/u).filter(Boolean);
  if (!words.length) return visible.map(command => ({ command, score: 0 }));
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
  return ranked.sort((left, right) => left.score - right.score);
}
