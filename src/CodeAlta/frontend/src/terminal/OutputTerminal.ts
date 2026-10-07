// A terminal that only shows text: what a command of a tool call wrote. Nothing is typed into it and no program
// reads from it; it draws the text as a terminal does (colors, carriage returns, wide characters) and keeps it
// selectable and scrollable.
import { Terminal, type ITheme } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { Unicode11Addon } from "@xterm/addon-unicode11";
import { terminalFontFamily } from "./TerminalSurface";

const fallbackFonts = '"Cascadia Mono", "Cascadia Code", Consolas, Menlo, "DejaVu Sans Mono", "Liberation Mono", monospace';
// The cursor of a program that runs elsewhere is not shown.
const hideCursor = "\x1b[?25l";

export type OutputTerminalOptions = Readonly<{ fontSize: number; theme: ITheme; mac: boolean;
  /** Puts text on the clipboard. */
  copy(text: string): void }>;

export class OutputTerminal {
  /** The element the terminal draws in. */
  readonly element = document.createElement("div");
  private readonly terminal: Terminal;
  private readonly fitting = new FitAddon();
  private readonly options: OutputTerminalOptions;
  private opened = false;
  private disposed = false;
  private frame = 0;

  constructor(options: OutputTerminalOptions) {
    this.options = options;
    this.element.className = "output-terminal";
    this.terminal = new Terminal({
      allowProposedApi: true,
      cols: 120, rows: 24,
      fontFamily: `"${terminalFontFamily}", ${fallbackFonts}`,
      fontSize: options.fontSize, lineHeight: 1.2,
      cursorStyle: "bar", cursorBlink: false, cursorInactiveStyle: "none",
      scrollback: 100_000,
      // No line is drawn beside the scroll bar.
      theme: { ...options.theme, overviewRulerBorder: "#00000000" },
      minimumContrastRatio: 4.5,
      rescaleOverlappingGlyphs: true,
      scrollbar: { showScrollbar: true, width: 10 },
      // The text of a tool has line feeds only: each one starts a new line.
      convertEol: true,
      disableStdin: true,
    });
    this.terminal.loadAddon(new Unicode11Addon());
    this.terminal.unicode.activeVersion = "11";
    this.terminal.loadAddon(this.fitting);
    this.terminal.attachCustomKeyEventHandler(event => this.key(event));
    this.terminal.write(hideCursor);
    // Rows that left the screen can be scrolled back to: the page shows the scroll bar then.
    const scrollable = () => this.element.toggleAttribute("data-scrollable", this.terminal.buffer.active.baseY > 0);
    this.terminal.onWriteParsed(scrollable);
    this.terminal.onResize(scrollable);
  }

  /** Shows the terminal in an element of the page. */
  mount(container: HTMLElement) {
    if (this.disposed) return;
    container.appendChild(this.element);
    if (!this.opened) {
      this.opened = true;
      this.terminal.open(this.element);
    }
    this.fit();
  }

  /** Gives the terminal the size of the element it is in, at the next frame. */
  fit() {
    if (this.frame || this.disposed) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      const parent = this.element.parentElement;
      if (this.disposed || !this.opened || !parent || parent.clientWidth < 20 || parent.clientHeight < 20) return;
      const size = this.fitting.proposeDimensions();
      if (size && Number.isFinite(size.cols) && Number.isFinite(size.rows) && (size.cols !== this.terminal.cols || size.rows !== this.terminal.rows)) {
        this.terminal.resize(Math.max(2, size.cols), Math.max(2, size.rows));
      }
    });
  }

  /** Adds text after what the terminal shows. */
  write(text: string) {
    if (!this.disposed && text) this.terminal.write(text);
  }

  /** Empties the terminal: what follows starts a new output. */
  reset() {
    if (this.disposed) return;
    this.terminal.reset();
    this.terminal.write(hideCursor);
    this.element.removeAttribute("data-scrollable");
  }

  setTheme(theme: ITheme) { if (!this.disposed) this.terminal.options.theme = { ...theme, overviewRulerBorder: "#00000000" }; }
  focus() { this.terminal.focus(); }
  scrollToBottom() { this.terminal.scrollToBottom(); }
  /** What the terminal holds, as text: the lines of its screen and of what left it. */
  get text(): string {
    const buffer = this.terminal.buffer.active;
    const lines: string[] = [];
    for (let index = 0; index < buffer.length; index++) {
      const line = buffer.getLine(index);
      if (!line) continue;
      const value = line.translateToString(true);
      // A line too long for the width continues on the next row: it is one line of text.
      if (line.isWrapped && lines.length) lines[lines.length - 1] += value; else lines.push(value);
    }
    while (lines.length && !lines[lines.length - 1]) lines.pop();
    return lines.join("\n");
  }

  dispose() {
    if (this.disposed) return;
    this.disposed = true;
    cancelAnimationFrame(this.frame);
    this.element.remove();
    this.terminal.dispose();
  }

  // The keys of a text that is read: copy, select all and scrolling. Any other key is left to the page, so that
  // Escape closes the window the terminal is in.
  private key(event: KeyboardEvent): boolean {
    if (event.type !== "keydown" || event.isComposing || event.keyCode === 229) return false;
    const command = this.options.mac ? event.metaKey && !event.ctrlKey : event.ctrlKey && !event.metaKey;
    const key = event.key.length === 1 ? event.key.toLowerCase() : event.key;
    if (command && !event.altKey && (key === "c" || key === "Insert")) {
      if (this.terminal.hasSelection()) { event.preventDefault(); this.options.copy(this.terminal.getSelection()); }
    } else if (command && !event.altKey && key === "a") { event.preventDefault(); this.terminal.selectAll(); }
    else if (!command && !event.altKey && !event.shiftKey) {
      if (key === "PageUp") this.terminal.scrollPages(-1);
      else if (key === "PageDown") this.terminal.scrollPages(1);
      else if (key === "Home") this.terminal.scrollToTop();
      else if (key === "End") this.terminal.scrollToBottom();
      else if (key === "ArrowUp") this.terminal.scrollLines(-1);
      else if (key === "ArrowDown") this.terminal.scrollLines(1);
      else return false;
      event.preventDefault();
    }
    return false;
  }
}
