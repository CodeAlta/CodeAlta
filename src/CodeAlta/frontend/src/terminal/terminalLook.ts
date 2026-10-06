// How the terminals of this window look and behave: a preference of the window, kept in its local storage.

export type TerminalCursor = "bar" | "block" | "underline";
export type TerminalLook = Readonly<{
  fontSize: number;
  cursorStyle: TerminalCursor;
  cursorBlink: boolean;
  /** Whether what is selected with the mouse is copied at once. */
  copyOnSelect: boolean;
  /** How many lines that left the screen a terminal of the window keeps. */
  scrollback: number;
  /** Whether a new terminal starts its shell with the script that makes it report its prompts and commands. */
  shellIntegration: boolean;
  /** The shell a new terminal starts, as the host names it; null for the default one of the system. */
  shell: string | null;
}>;

export const terminalLookKey = "codealta.desktop.terminal.v1";
export const terminalCursors: readonly TerminalCursor[] = ["bar", "block", "underline"];
export const terminalScrollbacks: readonly number[] = [1_000, 10_000, 100_000];
/** The sizes of text a terminal takes, in pixels. */
export const minimumTerminalFontSize = 8, maximumTerminalFontSize = 24;
export const defaultTerminalLook: TerminalLook = { fontSize: 13, cursorStyle: "bar", cursorBlink: true, copyOnSelect: false, scrollback: 10_000, shellIntegration: true, shell: null };

/** The look that was stored, with the default for anything that is missing or is not a value the window offers. */
export function restoreTerminalLook(read: () => string | null): TerminalLook {
  try {
    const raw = read();
    if (!raw || raw.length > 1024) return defaultTerminalLook;
    const stored: unknown = JSON.parse(raw);
    if (!stored || typeof stored !== "object") return defaultTerminalLook;
    const value = stored as Record<string, unknown>;
    return {
      fontSize: Number.isInteger(value.fontSize) && value.fontSize as number >= minimumTerminalFontSize && value.fontSize as number <= maximumTerminalFontSize
        ? value.fontSize as number : defaultTerminalLook.fontSize,
      cursorStyle: terminalCursors.includes(value.cursorStyle as TerminalCursor) ? value.cursorStyle as TerminalCursor : defaultTerminalLook.cursorStyle,
      cursorBlink: typeof value.cursorBlink === "boolean" ? value.cursorBlink : defaultTerminalLook.cursorBlink,
      copyOnSelect: typeof value.copyOnSelect === "boolean" ? value.copyOnSelect : defaultTerminalLook.copyOnSelect,
      scrollback: terminalScrollbacks.includes(value.scrollback as number) ? value.scrollback as number : defaultTerminalLook.scrollback,
      shellIntegration: typeof value.shellIntegration === "boolean" ? value.shellIntegration : defaultTerminalLook.shellIntegration,
      shell: typeof value.shell === "string" && value.shell.length > 0 && value.shell.length <= 128 ? value.shell : null,
    };
  } catch { return defaultTerminalLook; }
}

export function persistTerminalLook(write: (value: string) => void, look: TerminalLook): boolean {
  try { write(JSON.stringify(look)); return true; }
  catch { return false; }
}
