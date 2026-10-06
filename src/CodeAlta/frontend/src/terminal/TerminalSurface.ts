// One terminal on the page: xterm.js with its addons, fed by the hub. It lives as long as a tab shows the
// terminal, whichever pane the tab is in.
import { Terminal, type ITheme } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { WebglAddon } from "@xterm/addon-webgl";
import { SearchAddon, type ISearchOptions } from "@xterm/addon-search";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { Unicode11Addon } from "@xterm/addon-unicode11";
import { ClipboardAddon } from "@xterm/addon-clipboard";
import { ImageAddon } from "@xterm/addon-image";
import { ProgressAddon } from "@xterm/addon-progress";
import type { TerminalSink } from "./terminalHub";
import type { TerminalLook } from "./terminalLook";
import { terminalKeyAction, type TerminalKeyAction } from "./terminals";

/** The family of the font the application brings for its terminals, with the icons of the Nerd Fonts. */
export const terminalFontFamily = "CaskaydiaCove Nerd Font";
const fallbackFonts = '"Cascadia Mono", "Cascadia Code", Consolas, Menlo, "DejaVu Sans Mono", "Liberation Mono", monospace';

// The height of a row, in heights of the font: the lines of a terminal breathe a little, and drawn boxes still join.
const lineHeight = 1.2;

/** How far a command is, as a program says it with the progress sequence: 0 none, 1 running, 2 failed, 3 unknown length, 4 paused. */
export type TerminalProgress = Readonly<{ state: number; value: number }>;
export type TerminalSearch = Readonly<{ text: string; caseSensitive: boolean; wholeWord: boolean; regex: boolean }>;
export type TerminalMatches = Readonly<{ index: number; count: number }>;

export type TerminalSurfaceOptions = Readonly<{
  /** The system of the host: its pseudoconsole wraps lines by its own rules on Windows. */
  platform: string;
  build: number;
  /** Whether the terminal shows the pictures a program draws (sixel, the iTerm and the kitty protocols). */
  pictures: boolean;
  look: TerminalLook;
  theme: ITheme;
  /** What the keyboard of the terminal sends to its program. */
  input(data: string): void;
  /** The screen has another size. */
  resized(columns: number, rows: number): void;
  /** A link of the terminal was chosen. */
  link(address: string): void;
  /** A key asks for something the page shows: the find bar. */
  action(action: TerminalKeyAction): void;
  progress(progress: TerminalProgress): void;
  matches(matches: TerminalMatches): void;
  /** Puts text on the clipboard. */
  copy(text: string): void;
}>;

/**
 * Whether the page may compile WebAssembly, which its content security policy decides. The decoders of the
 * pictures a terminal shows are WebAssembly: without it a terminal shows text only.
 */
export function canShowPictures(): boolean {
  try {
    // The smallest module there is: its header alone.
    return new WebAssembly.Module(new Uint8Array([0, 97, 115, 109, 1, 0, 0, 0])) instanceof WebAssembly.Module;
  } catch { return false; }
}

/** Waits for the faces of the terminal font: a terminal measures its cells once, with the font it finds. */
export function loadTerminalFont(fonts: FontFaceSet = document.fonts): Promise<void> {
  return Promise.all([fonts.load(`13px "${terminalFontFamily}"`), fonts.load(`bold 13px "${terminalFontFamily}"`)]).then(() => { }, () => { /* The fallback fonts are used. */ });
}

export class TerminalSurface implements TerminalSink {
  /** The element the terminal draws in. It is moved from one pane to another with its tab. */
  readonly element = document.createElement("div");
  private readonly terminal: Terminal;
  private readonly fitting = new FitAddon();
  private readonly searching = new SearchAddon({ highlightLimit: 2000 });
  private readonly options: TerminalSurfaceOptions;
  private readonly mac: boolean;
  private look: TerminalLook;
  private opened = false;
  private disposed = false;
  private replaying = true;
  private frame = 0;

  constructor(options: TerminalSurfaceOptions) {
    this.options = options;
    this.look = options.look;
    this.mac = options.platform === "macos";
    this.element.className = "terminal-surface";
    const windows = options.platform === "windows";
    this.terminal = new Terminal({
      allowProposedApi: true,
      cols: 120, rows: 30,
      fontFamily: `"${terminalFontFamily}", ${fallbackFonts}`,
      fontSize: options.look.fontSize, lineHeight,
      cursorStyle: options.look.cursorStyle, cursorBlink: options.look.cursorBlink, cursorInactiveStyle: "outline",
      scrollback: options.look.scrollback,
      theme: options.theme,
      // The colors a program chooses stay readable on the background of the window, whatever the scheme.
      minimumContrastRatio: 4.5,
      rescaleOverlappingGlyphs: true,
      scrollbar: { showScrollbar: true, width: 10 },
      showCursorImmediately: true,
      macOptionClickForcesSelection: true,
      // What the host says of its pseudoconsole decides whether the terminal wraps lines again when its width changes.
      windowsPty: windows ? { backend: "conpty", buildNumber: options.build } : undefined,
      linkHandler: { activate: (_event, address) => options.link(address) },
      // Nothing is typed, and no question of the program answered, while what was written before is replayed.
      disableStdin: true,
    });
    const terminal = this.terminal;
    terminal.loadAddon(new Unicode11Addon());
    terminal.unicode.activeVersion = "11";
    terminal.loadAddon(this.fitting);
    terminal.loadAddon(this.searching);
    terminal.loadAddon(new WebLinksAddon((_event, address) => options.link(address)));
    // A program can put text on the clipboard; it never reads what is there.
    terminal.loadAddon(new ClipboardAddon(undefined, { readText: () => "", writeText: (_selection, text) => options.copy(text) }));
    if (options.pictures) terminal.loadAddon(new ImageAddon({ pixelLimit: 8_388_608, storageLimit: 48, sixelSizeLimit: 16_000_000, iipSizeLimit: 16_000_000 }));
    const progress = new ProgressAddon();
    terminal.loadAddon(progress);
    progress.onChange(state => options.progress(state));
    this.searching.onDidChangeResults(results => options.matches({ index: results.resultIndex, count: results.resultCount }));
    if (windows) {
      // A pseudoconsole of Windows asks what the terminal is and waits for an answer of this form before it
      // shows anything: a terminal of the VT100 family, which shows sixel pictures (4) when it does.
      terminal.parser.registerCsiHandler({ final: "c" }, parameters => {
        if (parameters.length > 1 || parameters[0]) return false;
        if (!this.replaying) options.input(options.pictures ? "\x1b[?61;4c" : "\x1b[?61c");
        return true;
      });
    }
    terminal.onData(data => { if (!this.replaying) options.input(data); });
    terminal.onBinary(data => { if (!this.replaying) options.input(data); });
    terminal.onResize(size => { if (!this.replaying) options.resized(size.cols, size.rows); });
    terminal.onSelectionChange(() => { if (this.look.copyOnSelect && terminal.hasSelection()) options.copy(terminal.getSelection()); });
    terminal.attachCustomKeyEventHandler(event => this.key(event));
  }

  /** Shows the terminal in an element of the page. */
  mount(container: HTMLElement) {
    if (this.disposed) return;
    container.appendChild(this.element);
    if (!this.opened) {
      this.opened = true;
      this.terminal.open(this.element);
      try {
        // Drawn by the graphics card; without it the terminal draws with elements of the page.
        const webgl = new WebglAddon();
        webgl.onContextLoss(() => { webgl.dispose(); this.fit(); });
        this.terminal.loadAddon(webgl);
      } catch { /* The page draws it. */ }
    }
    this.fit();
  }

  /** Takes the terminal out of the page; it goes on reading what its program writes. */
  unmount() {
    this.element.remove();
  }

  /** Gives the terminal the size of the element it is in, at the next frame. */
  fit() {
    if (this.frame || this.disposed) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      const parent = this.element.parentElement;
      // An element that is not shown has no size: the terminal keeps the one it has.
      if (this.disposed || !this.opened || !parent || parent.clientWidth < 20 || parent.clientHeight < 20) return;
      // While what was written before is replayed, the terminal has the sizes it was written for.
      if (this.replaying) return;
      const size = this.fitting.proposeDimensions();
      if (size && Number.isFinite(size.cols) && Number.isFinite(size.rows) && (size.cols !== this.terminal.cols || size.rows !== this.terminal.rows)) {
        this.terminal.resize(Math.max(2, size.cols), Math.max(2, size.rows));
      }
    });
  }

  focus() { this.terminal.focus(); }
  get focused() { return this.element.contains(document.activeElement); }
  get selection() { return this.terminal.getSelection(); }
  copySelection() { if (this.terminal.hasSelection()) { this.options.copy(this.terminal.getSelection()); this.terminal.clearSelection(); } }
  paste(text: string) { if (!this.replaying) this.terminal.paste(text); }
  selectAll() { this.terminal.selectAll(); }
  clear() { this.terminal.clear(); }
  scrollToBottom() { this.terminal.scrollToBottom(); }

  /** Finds the next or the previous place a text is at, and marks them all. */
  find(search: TerminalSearch, forward: boolean, colors: Readonly<{ match: string; active: string }>, incremental = false) {
    if (!search.text) { this.clearFind(); return false; }
    const options: ISearchOptions = { caseSensitive: search.caseSensitive, wholeWord: search.wholeWord, regex: search.regex, incremental,
      decorations: { matchBackground: colors.match, activeMatchBackground: colors.active, matchOverviewRuler: colors.match, activeMatchColorOverviewRuler: colors.active } };
    return forward ? this.searching.findNext(search.text, options) : this.searching.findPrevious(search.text, options);
  }
  clearFind() { this.searching.clearDecorations(); this.terminal.clearSelection(); }

  setTheme(theme: ITheme) { this.terminal.options.theme = theme; }
  setLook(look: TerminalLook) {
    this.look = look;
    const options = this.terminal.options;
    options.fontSize = look.fontSize;
    options.cursorStyle = look.cursorStyle; options.cursorBlink = look.cursorBlink; options.scrollback = look.scrollback;
    this.fit();
  }

  start(columns: number, rows: number, modes: string, replayed: boolean) {
    if (this.disposed) return;
    this.replaying = true;
    this.terminal.options.disableStdin = true;
    this.terminal.reset();
    if (columns > 1 && rows > 0) this.terminal.resize(columns, rows);
    if (modes) this.terminal.write(modes);
    if (replayed) this.terminal.write("", () => this.live());
  }

  write(data: string, columns: number, rows: number, replayed: boolean, done: () => void) {
    if (this.disposed) { done(); return; }
    if (columns > 1 && rows > 0 && this.replaying) this.terminal.resize(columns, rows);
    this.terminal.write(data, () => {
      done();
      if (replayed) this.live();
    });
  }

  dispose() {
    if (this.disposed) return;
    this.disposed = true;
    cancelAnimationFrame(this.frame);
    this.element.remove();
    this.terminal.dispose();
  }

  // Everything written before the page asked for the terminal is on its screen: the keyboard works from now
  // on, and the terminal takes the size of its element, which the host is told.
  private live() {
    if (this.disposed || !this.replaying) return;
    this.replaying = false;
    this.terminal.options.disableStdin = false;
    this.fit();
  }

  private key(event: KeyboardEvent): boolean {
    if (event.isComposing || event.keyCode === 229) return true;
    const action = terminalKeyAction(event, this.terminal.hasSelection(), this.mac);
    if (!action) return true;
    // Pasting is the browser's own gesture: the terminal receives what it pastes.
    if (action === "paste") return false;
    if (event.type === "keydown") {
      event.preventDefault();
      if (action === "copy") this.copySelection();
      else if (action === "selectAll") this.terminal.selectAll();
      else if (action === "top") this.terminal.scrollToTop();
      else if (action === "bottom") this.terminal.scrollToBottom();
      else this.options.action(action);
    }
    return false;
  }
}
