import { createContext } from "react";
import { isScriptPath } from "./pluginScript/scriptModule";
import type { CommandFocus, CommandKey } from "./commandRegistry";

/** A command contributed by a plugin, as the palette, the help and the keyboard use it. */
export type PluginCommandView = Readonly<{
  id: string; pluginKey: string; plugin: string; name: string; label: string; description: string; group: string | null; search: string | null;
  /** The key binding in the form the registry uses ("Ctrl+G Ctrl+Y"), or null. */
  keys: string | null; palette: boolean; help: boolean;
  needsProject: boolean; needsSession: boolean; needsIdle: boolean; needsBusy: boolean;
}>;
/** A prompt picker contributed by a plugin: typing `trigger` at a word start opens it. */
export type PluginPickerView = Readonly<{ id: string; plugin: string; trigger: string; title: string; placeholder: string | null }>;
/** What a plugin shows around a prompt: exactly one of the three forms is set. */
export type PluginRegionView = Readonly<{ id: string; pluginKey: string; region: "footer" | "bar" | "status"; html: string | null; markdown: string | null; text: string | null;
  /** The module that draws the fragment, as a path of the application's origin; null for a fragment alone. */
  script: string | null; scriptProblem: string | null }>;
export type PluginContributionsView = Readonly<{ commands: readonly PluginCommandView[]; pickers: readonly PluginPickerView[];
  /** A plugin has content for the regions around the prompt, so composers read them. */
  regions: boolean }>;
export const noPluginContributions: PluginContributionsView = Object.freeze({ commands: Object.freeze([]), pickers: Object.freeze([]), regions: false });

/** The pane a plugin command runs for. */
export type PluginPane = Readonly<{ projectId: string | null; sessionId: string | null; busy: boolean; draftText: string | null }>;

/**
 * What the shell gives the parts of the window that show plugin content: the commands and pickers of the
 * selected project, and how to run a command by its identity or by the name an HTML fragment carries.
 */
export type PluginUiValue = Readonly<{
  epoch: string | null; projectId: string | null; contributions: PluginContributionsView;
  run: (commandId: string, pane?: Partial<PluginPane>) => void;
  runNamed: (name: string, pluginKey: string | null, pane?: Partial<PluginPane>) => void;
}>;
export const PluginUiContext = createContext<PluginUiValue>({ epoch: null, projectId: null, contributions: noPluginContributions, run: () => { }, runNamed: () => { } });

/**
 * The pane the plugin content below is shown for, where the place knows it better than the focus does: the timeline of a
 * session gives its session and its project, so that a command of a card runs for the session of the card, whatever pane has
 * the keyboard. Content that is given a pane of its own keeps it.
 */
export const PluginPaneContext = createContext<Partial<PluginPane> | undefined>(undefined);

/**
 * A request the shell addresses to the composer of a session, as a `codealta:plugin` window event: to read
 * its draft and state before a plugin command runs (`state`), or to do what a plugin asked (`send`,
 * `enqueue`, `steer`, `compact`, `draft`). `sessionId` null means the composer of the focused pane. The
 * composer that takes the request sets `handled`, and `result` when it did what was asked. `agentPromptId`
 * names the agent prompt a `send` or an `enqueue` goes with, when it is not the one the composer shows.
 */
export type PluginComposerRequest = {
  kind: "state" | "send" | "enqueue" | "steer" | "compact" | "draft";
  sessionId: string | null; text: string | null; agentPromptId?: string | null;
  handled: boolean; result: boolean;
  state: { sessionId: string; draftText: string; busy: boolean } | null;
};
export const pluginComposerEvent = "codealta:plugin";
/** Raised on the window when the host started, replaced or stopped plugins, or ran one of their commands: what they show is read again. */
export const pluginsChangedEvent = "codealta:plugins-changed";

/** Sends a request to the composer it names and returns it with the composer's answer. */
export function askPluginComposer(kind: PluginComposerRequest["kind"], sessionId: string | null, text: string | null = null, agentPromptId: string | null = null): PluginComposerRequest {
  const request: PluginComposerRequest = { kind, sessionId, text, agentPromptId, handled: false, result: false, state: null };
  window.dispatchEvent(new CustomEvent(pluginComposerEvent, { detail: request }));
  return request;
}

const line = (value: unknown, maximum: number): value is string => typeof value === "string" && value.length <= maximum && !/[\u0000-\u001f\u007f]/u.test(value);
const identity = (value: unknown): value is string => line(value, 512) && value.length > 0;
const optional = (value: unknown, maximum: number): string | null => line(value, maximum) && value.length > 0 ? value : null;
const strokeKeys = new Set(["enter", "escape", "backspace", "tab", "space", "up", "down", "left", "right", "home", "end", "pageup", "pagedown", "insert", "delete",
  ...Array.from({ length: 12 }, (_, index) => `f${index + 1}`)]);

/**
 * Accepts a plugin key binding ("Ctrl+G Ctrl+Y", "F9", "Ctrl+Shift+K") of one or two strokes. A binding the
 * window cannot route (three strokes or more, Meta, a key without a name here) is dropped: the command
 * stays in the palette without a shortcut.
 */
export function pluginGesture(keys: unknown): string | null {
  if (!line(keys, 64) || !keys) return null;
  const strokes = keys.split(" ");
  if (strokes.length > 2) return null;
  for (const stroke of strokes) {
    const parts = stroke.split("+");
    const key = parts.pop()!;
    if (!key || parts.some(part => !["Ctrl", "Alt", "Shift"].includes(part)) || new Set(parts).size !== parts.length) return null;
    if ([...key].length !== 1 && !strokeKeys.has(key.toLowerCase())) return null;
  }
  // The second stroke of a chord follows the prefix the window uses for its own chords.
  if (strokes.length === 2 && strokes[0] !== "Ctrl+G") return null;
  // A single printable key without Ctrl or Alt is typing, not a shortcut.
  if (strokes.length === 1) {
    const parts = strokes[0].split("+");
    const key = parts.pop()!;
    if ([...key].length === 1 && !parts.includes("Ctrl") && !parts.includes("Alt")) return null;
  }
  return keys;
}

/** Accepts a well-formed `pluginUi.contributions` answer for the asked project; anything else is null. */
export function pluginContributions(reply: unknown, projectId: string | null): PluginContributionsView | null {
  if (!reply || typeof reply !== "object") return null;
  const value = reply as Record<string, unknown>;
  if (value.status !== "ok" || (value.projectId ?? null) !== projectId || !Array.isArray(value.commands) || !Array.isArray(value.pickers)) return null;
  const commands: PluginCommandView[] = [];
  for (const entry of value.commands.slice(0, 256)) {
    if (!entry || typeof entry !== "object") continue;
    const item = entry as Record<string, unknown>;
    if (!identity(item.id) || !identity(item.pluginKey) || !line(item.plugin, 200) || !line(item.name, 64) || !/^[A-Za-z0-9._-]+$/u.test(item.name)
      || !line(item.label, 200) || !line(item.description, 1024) || commands.some(known => known.id === item.id)) continue;
    commands.push({ id: item.id, pluginKey: item.pluginKey, plugin: item.plugin, name: item.name, label: item.label || item.name, description: item.description,
      group: optional(item.group, 200), search: optional(item.search, 1024), keys: pluginGesture(item.keys),
      palette: item.palette !== false, help: item.help !== false,
      needsProject: item.needsProject === true, needsSession: item.needsSession === true, needsIdle: item.needsIdle === true, needsBusy: item.needsBusy === true });
  }
  const pickers: PluginPickerView[] = [];
  for (const entry of value.pickers.slice(0, 32)) {
    if (!entry || typeof entry !== "object") continue;
    const item = entry as Record<string, unknown>;
    if (!identity(item.id) || !line(item.plugin, 200) || typeof item.trigger !== "string" || !/^[^\p{L}\p{N}\s@#/]$/u.test(item.trigger)
      || !line(item.title, 200) || pickers.some(known => known.trigger === item.trigger)) continue;
    pickers.push({ id: item.id, plugin: item.plugin, trigger: item.trigger, title: item.title, placeholder: optional(item.placeholder, 200) });
  }
  return { commands, pickers, regions: value.regions === true };
}

/** Accepts a well-formed `pluginUi.regions` answer for the asked pane; anything else is null. */
export function pluginRegions(reply: unknown, projectId: string | null, sessionId: string | null): PluginRegionView[] | null {
  if (!reply || typeof reply !== "object") return null;
  const value = reply as Record<string, unknown>;
  if (value.status !== "ok" || (value.projectId ?? null) !== projectId || (value.sessionId ?? null) !== sessionId || !Array.isArray(value.items)) return null;
  const items: PluginRegionView[] = [];
  for (const entry of value.items.slice(0, 64)) {
    if (!entry || typeof entry !== "object") continue;
    const item = entry as Record<string, unknown>;
    const html = typeof item.html === "string" && item.html.length <= 256 * 1024 ? item.html : null;
    const markdown = !html && typeof item.markdown === "string" && item.markdown.length <= 8192 ? item.markdown : null;
    const text = !html && !markdown && typeof item.text === "string" && item.text.length <= 8192 ? item.text : null;
    if (!identity(item.id) || !identity(item.pluginKey) || !["footer", "bar", "status"].includes(item.region as string)
      || !(html || markdown || text) || items.some(known => known.id === item.id)) continue;
    const script = html && typeof item.script === "string" && isScriptPath(item.script) ? item.script : null;
    const scriptProblem = html && typeof item.scriptProblem === "string" && item.scriptProblem.length <= 600 ? item.scriptProblem : null;
    items.push({ id: item.id, pluginKey: item.pluginKey, region: item.region as PluginRegionView["region"], html, markdown, text, script, scriptProblem });
  }
  return items;
}

/** Whether two region lists show the same thing, so a refresh that changed nothing keeps the rendered one. */
export function samePluginRegions(left: readonly PluginRegionView[], right: readonly PluginRegionView[]): boolean {
  return left.length === right.length && left.every((item, index) => {
    const other = right[index];
    return item.id === other.id && item.region === other.region && item.html === other.html && item.markdown === other.markdown && item.text === other.text
      && item.script === other.script && item.scriptProblem === other.scriptProblem;
  });
}

/** Whether a plugin command can run for a pane. */
export function pluginCommandAvailable(command: PluginCommandView, pane: PluginPane): boolean {
  return !(command.needsProject && !pane.projectId || command.needsSession && !pane.sessionId
    || command.needsIdle && pane.busy || command.needsBusy && !pane.busy);
}

/** The command an HTML fragment names: the one of the same plugin first, then any plugin's. */
export function findPluginCommand(commands: readonly PluginCommandView[], name: string, pluginKey: string | null): PluginCommandView | null {
  const wanted = name.toLowerCase();
  const named = commands.filter(command => command.name.toLowerCase() === wanted);
  return named.find(command => command.pluginKey === pluginKey) ?? named[0] ?? null;
}

/**
 * Palette search over plugin commands: every word of the query must match the slash name, the label, the
 * plugin, the extra search words or the description. An empty query lists them all.
 */
export function searchPluginCommands(query: string, commands: readonly PluginCommandView[]): PluginCommandView[] {
  const words = query.trim().toLowerCase().replace(/^\//u, "").split(/\s+/u).filter(Boolean);
  const shown = commands.filter(command => command.palette);
  if (!words.length) return shown;
  const score = (command: PluginCommandView) => {
    let total = 0;
    for (const word of words) {
      const name = command.name.toLowerCase();
      const rank = name === word ? 0 : name.startsWith(word) ? 1 : name.includes(word) ? 2
        : `${command.label} ${command.plugin} ${command.search ?? ""}`.toLowerCase().includes(word) ? 3
        : command.description.toLowerCase().includes(word) ? 4 : null;
      if (rank === null) return null;
      total += rank;
    }
    return total;
  };
  return shown.map(command => ({ command, rank: score(command) })).filter(item => item.rank !== null)
    .sort((left, right) => left.rank! - right.rank!).map(item => item.command);
}

const keyNames: Readonly<Record<string, string>> = { left: "arrowleft", right: "arrowright", up: "arrowup", down: "arrowdown", space: " " };
function normalize(stroke: string): string {
  const parts = stroke.toLowerCase().split("+");
  const key = parts.pop()!;
  const has = (modifier: string) => parts.includes(modifier);
  return `${has("ctrl") ? "ctrl+" : ""}${has("alt") ? "alt+" : ""}${has("shift") ? "shift+" : ""}${keyNames[key] ?? key}`;
}

export type PluginKeymap = Readonly<{ single: ReadonlyMap<string, PluginCommandView>; chords: ReadonlyMap<string, PluginCommandView> }>;

/** The key bindings of plugin commands; the first command to claim a gesture keeps it. */
export function pluginKeymap(commands: readonly PluginCommandView[]): PluginKeymap {
  const single = new Map<string, PluginCommandView>();
  const chords = new Map<string, PluginCommandView>();
  for (const command of commands) {
    if (!command.keys) continue;
    const strokes = command.keys.split(" ").map(normalize);
    if (strokes.length === 1) { if (!single.has(strokes[0])) single.set(strokes[0], command); }
    else { const key = strokes[1].replace(/^ctrl\+/u, ""); if (!chords.has(key)) chords.set(key, command); }
  }
  return { single, chords };
}

/**
 * The plugin command of a key press that no command of the window took. `chord` is whether Ctrl+G was
 * pressed just before. A gesture that is ordinary typing in a text field (no Ctrl or Alt) only applies
 * outside text.
 */
export function resolvePluginKey(event: CommandKey, chord: boolean, focus: CommandFocus, keymap: PluginKeymap): PluginCommandView | null {
  if (event.isComposing || event.keyCode === 229 || event.metaKey || event.repeat) return null;
  const key = event.key.toLowerCase();
  if (["control", "shift", "alt", "meta"].includes(key)) return null;
  if (chord) return event.altKey || event.shiftKey ? null : keymap.chords.get(key) ?? null;
  const command = keymap.single.get(`${event.ctrlKey ? "ctrl+" : ""}${event.altKey ? "alt+" : ""}${event.shiftKey ? "shift+" : ""}${key}`);
  return command && (event.ctrlKey || event.altKey || focus === "none" || /^f\d{1,2}$/u.test(key)) ? command : null;
}

/**
 * The token at the caret that opens a plugin picker: its trigger character at the start of the prompt or
 * after white space or an opening bracket, followed by the query typed so far.
 */
export function activePluginReference(trigger: string, text: string, caret: number): { start: number; end: number; query: string } | null {
  const before = text.slice(0, caret);
  const at = before.lastIndexOf(trigger);
  if (at < 0 || at > 0 && !/[\s(\[{]/u.test(before[at - 1])) return null;
  const query = before.slice(at + trigger.length);
  if (query.length > 256 || /[\s()\[\]{}]/u.test(query) || query.includes(trigger) || text.startsWith(trigger, caret)) return null;
  let end = caret;
  while (end < text.length && !/[\s,:;!?()\[\]{}]/u.test(text[end])) end++;
  return { start: at, end, query };
}

/** Replaces the trigger token with the text of the chosen picker item. */
export function insertPluginReference(text: string, start: number, end: number, insert: string): { text: string; caret: number } | null {
  if (!insert || insert.length > 4096) return null;
  const value = text.slice(0, start) + insert + text.slice(end);
  return value.length <= 32768 ? { text: value, caret: start + insert.length } : null;
}
