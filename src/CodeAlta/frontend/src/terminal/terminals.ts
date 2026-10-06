// What the page knows of terminals without a terminal on the page: their labels, their keys, their colors.
import type { TerminalItem } from "#neoastra";

/** A key as a keyboard event describes it. */
export type TerminalKey = Readonly<{ key: string; ctrlKey: boolean; altKey: boolean; shiftKey: boolean; metaKey: boolean }>;

/** The last name of a folder, whatever separates its names. */
export function folderName(path: string): string {
  const trimmed = path.replace(/[\\/]+$/, "");
  const name = trimmed.slice(Math.max(trimmed.lastIndexOf("/"), trimmed.lastIndexOf("\\")) + 1);
  return name || path;
}

/** What the tab of a terminal shows: the title it was given, or the word for a terminal and the folder it is in. */
export function terminalTabLabel(terminal: Pick<TerminalItem, "title" | "titled" | "folder">, word: string): string {
  return terminal.titled ? terminal.title : `${word} ${folderName(terminal.folder)}`;
}

/** The terminals listed under a project, or with null those of no project, in the order they were created. */
export function terminalsOf(list: readonly TerminalItem[], projectId: string | null): TerminalItem[] {
  return list.filter(terminal => (terminal.projectId ?? null) === projectId);
}

const plain = (event: TerminalKey) => !event.altKey && !event.shiftKey && !event.metaKey;
const letter = (event: TerminalKey) => event.key.length === 1 ? event.key.toLowerCase() : event.key;

/**
 * Whether a key stays a shortcut of the application while a terminal has the keyboard. Everything else is
 * for the program in the terminal: a shell and the programs it runs use most of the keyboard, the function
 * keys and Ctrl with a letter included.
 */
export function applicationKey(event: TerminalKey): boolean {
  if (!event.ctrlKey || event.metaKey) return false;
  const key = letter(event);
  // The command palette, the prefix of the two-key shortcuts, the settings, a new terminal, and the tab before and after.
  if (plain(event)) return key === "p" || key === "g" || key === "," || key === "`" || key === "PageUp" || key === "PageDown";
  // The tabs: reopen, close, previous and next.
  if (event.shiftKey && !event.altKey) return key === "t" || key === "w" || key === "n";
  return event.altKey && !event.shiftKey && (key === "ArrowLeft" || key === "ArrowRight" || key === "b");
}

/** What a key does to the terminal itself rather than to its program. */
export type TerminalKeyAction = "copy" | "paste" | "find" | "selectAll" | "top" | "bottom";

/**
 * The action of a key on the terminal, or null when the key is for the program. Ctrl+C copies what is
 * selected and interrupts the program when nothing is; on macOS the Command key does what Ctrl does elsewhere.
 */
export function terminalKeyAction(event: TerminalKey, selection: boolean, mac: boolean): TerminalKeyAction | null {
  const key = letter(event);
  if (mac) {
    if (!event.metaKey || event.ctrlKey || event.altKey) return null;
    if (event.shiftKey) return null;
    return key === "c" ? selection ? "copy" : null : key === "v" ? "paste" : key === "f" ? "find" : key === "a" ? "selectAll"
      : key === "ArrowUp" || key === "Home" ? "top" : key === "ArrowDown" || key === "End" ? "bottom" : null;
  }
  if (event.metaKey || event.altKey) return null;
  if (event.ctrlKey && event.shiftKey) return key === "c" ? "copy" : key === "v" ? "paste" : key === "f" ? "find" : key === "a" ? "selectAll" : null;
  if (event.ctrlKey) {
    return key === "c" ? selection ? "copy" : null : key === "v" ? "paste" : key === "f" ? "find" : key === "Insert" ? "copy"
      : key === "Home" ? "top" : key === "End" ? "bottom" : null;
  }
  return event.shiftKey && key === "Insert" ? "paste" : null;
}

/** The colors of a terminal: the sixteen a program names, and those of the terminal itself. */
export type TerminalTheme = Readonly<Record<"background" | "foreground" | "cursor" | "cursorAccent" | "selectionBackground" | "selectionInactiveBackground"
  | "scrollbarSliderBackground" | "scrollbarSliderHoverBackground" | "scrollbarSliderActiveBackground"
  | "black" | "red" | "green" | "yellow" | "blue" | "magenta" | "cyan" | "white"
  | "brightBlack" | "brightRed" | "brightGreen" | "brightYellow" | "brightBlue" | "brightMagenta" | "brightCyan" | "brightWhite", string>>;

// The families of the window's palette that stand for the six colors a program names.
const families = { red: "red", green: "green", yellow: "gold", blue: "blue", magenta: "violet", cyan: "turquoise" } as const;
// What a color is when the window gives none: the palette of the application before any scheme changes it.
const fallback = {
  dark: { background: "#1c2127", foreground: "#f6f7f9", accent: "#4c90f0", gray: ["#5f6b7c", "#738091", "#8f99a8", "#abb3bf", "#c5cbd3"], dim: "#404854",
    red: ["#cd4246", "#e76a6e"], green: ["#32a467", "#72ca9b"], gold: ["#f0b726", "#fbd065"], blue: ["#4c90f0", "#8abbff"], violet: ["#9881f3", "#bdadff"], turquoise: ["#13c9ba", "#7ae1d8"] },
  light: { background: "#ffffff", foreground: "#1c2127", accent: "#2d72d2", gray: ["#5f6b7c", "#738091", "#8f99a8", "#abb3bf", "#c5cbd3"], dim: "#404854",
    red: ["#ac2f33", "#cd4246"], green: ["#1c6e42", "#238551"], gold: ["#935610", "#c87619"], blue: ["#215db0", "#2d72d2"], violet: ["#5642a6", "#7961db"], turquoise: ["#007067", "#00a396"] },
} as const;

/**
 * The colors of a terminal from those of the window, so that a terminal follows the theme and the color scheme.
 * @param color Gives a color of the window (`--text`, `--bp-palette-red-4`) as #rrggbb, or nothing.
 * @param dark Whether the window is dark.
 */
export function terminalTheme(color: (property: string) => string | undefined, dark: boolean): TerminalTheme {
  const base = dark ? fallback.dark : fallback.light;
  const palette = (family: string, step: number, otherwise: string) => color(`--bp-palette-${family}-${step}`) ?? otherwise;
  const foreground = color("--text") ?? base.foreground;
  const accent = color("--accent") ?? base.accent;
  // A dark window takes the light steps of a family, a light one the dark steps: the color is read on the background.
  const pair = (name: keyof typeof families) => dark
    ? [palette(families[name], 4, base[families[name]][0]), palette(families[name], 5, base[families[name]][1])]
    : [palette(families[name], 2, base[families[name]][0]), palette(families[name], 3, base[families[name]][1])];
  const [red, brightRed] = pair("red"), [green, brightGreen] = pair("green"), [yellow, brightYellow] = pair("yellow");
  const [blue, brightBlue] = pair("blue"), [magenta, brightMagenta] = pair("magenta"), [cyan, brightCyan] = pair("cyan");
  return {
    background: color("--bg") ?? base.background, foreground, cursor: accent, cursorAccent: color("--bg") ?? base.background,
    selectionBackground: accent + "55", selectionInactiveBackground: accent + "30",
    scrollbarSliderBackground: foreground + "26", scrollbarSliderHoverBackground: foreground + "40", scrollbarSliderActiveBackground: foreground + "59",
    black: dark ? palette("dark-gray", 5, base.dim) : palette("dark-gray", 1, base.foreground),
    red, green, yellow, blue, magenta, cyan,
    white: dark ? palette("light-gray", 1, base.gray[4]) : palette("gray", 2, base.gray[1]),
    brightBlack: dark ? palette("gray", 3, base.gray[2]) : palette("gray", 1, base.gray[0]),
    brightRed, brightGreen, brightYellow, brightBlue, brightMagenta, brightCyan,
    brightWhite: dark ? palette("light-gray", 5, base.foreground) : palette("dark-gray", 5, base.dim),
  };
}
