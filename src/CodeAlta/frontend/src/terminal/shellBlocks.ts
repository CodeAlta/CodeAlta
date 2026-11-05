// What a code block of a shell pastes in a terminal: which languages are a shell's, and the commands of a block as a person
// types them, without the prompts and the output a transcript shows. Plain functions, without the DOM.

/** The languages of a code block whose text is commands of a shell. */
const shellLanguages: ReadonlySet<string> = new Set([
  "bash", "sh", "zsh", "fish", "ksh", "shell", "console", "terminal", "shellsession", "shell-session",
  "powershell", "pwsh", "ps1", "ps", "posh", "cmd", "bat", "batch", "nu", "nushell",
]);

/** The languages whose block is a transcript: commands after a prompt, and what they printed. */
const transcriptLanguages: ReadonlySet<string> = new Set(["console", "terminal", "shellsession", "shell-session"]);

// A prompt at the start of a line: `$ `, `# ` (as root), `% ` (zsh), `> `, or `PS C:\folder> ` of PowerShell.
const prompt = /^(?:PS(?: [^>\n]*)?>|[$#%>])(?: |$)/u;

/** Whether a code block of this language holds commands of a shell. */
export function isShellLanguage(language: string | null | undefined): boolean {
  return !!language && shellLanguages.has(language.trim().toLowerCase());
}

/**
 * The commands of a block, as they are pasted in a terminal: its text without the line it ends with. A transcript (`console`) keeps
 * the lines after a prompt, the prompt taken off, the lines that go on a command (after a `\` or a `` ` `` that ends the line
 * before), and its comments; the lines it printed are left. A block of a shell whose every line but its comments starts with `$ `
 * loses that prompt.
 */
export function shellCommandOf(language: string | null | undefined, text: string): string {
  const lines = text.replace(/\r\n?/gu, "\n").replace(/\n+$/u, "").split("\n");
  const written = lines.filter(line => line.trim().length > 0);
  const transcript = transcriptLanguages.has((language ?? "").trim().toLowerCase());
  if (transcript && written.some(line => prompt.test(line))) {
    // Beside commands after a `$ `, a line that starts with `# ` is a comment, not the prompt of root: it stays one, which the shell
    // reads as nothing.
    const comments = written.some(line => /^\$(?: |$)/u.test(line));
    const commands: string[] = [];
    let continues = false;
    for (const line of lines) {
      const prompted = comments && line.startsWith("#") ? null : prompt.exec(line);
      if (comments && line.startsWith("#")) commands.push(line);
      else if (prompted) commands.push(line.slice(prompted[0].length));
      else if (continues) commands.push(line);
      else continue;
      continues = /[\\`]$/u.test(line);
    }
    return commands.join("\n");
  }

  const prompted = written.filter(line => !line.startsWith("#"));
  if (prompted.length > 0 && prompted.every(line => line.startsWith("$ "))) return lines.map(line => line.startsWith("$ ") ? line.slice(2) : line).join("\n");
  return lines.join("\n");
}

/** What a shell runs one command after another with: `&` for the Command Prompt of Windows, `;` for the others. */
export function commandSeparator(profile: string | null | undefined): string {
  return profile === "cmd" ? " & " : "; ";
}

// What goes on to the next line: a `\` of a POSIX shell or a `` ` `` of PowerShell (both taken off), a pipe, `&&` or `||`.
const continuation = /(?:\s[\\`]|\||&&|\|\|)$/u;
// What a line cannot be joined with safely: a here-document, a comment after a command (any `#` that is not a whole line,
// which `a;# note` starts as well as `a # note`), or quotes or brackets left open.
function joinable(line: string): boolean {
  if (/<</u.test(line) || line.includes("#")) return false;
  const count = (character: string) => line.split(character).length - 1;
  return count("\"") % 2 === 0 && count("'") % 2 === 0 && count("(") === count(")") && count("{") === count("}") && count("[") === count("]");
}

/**
 * The commands of several lines on one line, for a shell that would run each line of a paste: joined with the separator of the
 * shell, the lines that go on a command put back on its line, the blank lines and the comments left out. Null when they cannot
 * be joined safely (a here-document, a string or a block over several lines): they are then copied instead.
 */
export function joinCommandLines(commands: string, separator: string): string | null {
  const cmd = separator.trim() === "&";
  const joined: string[] = [];
  let pending = "";
  for (const raw of commands.split("\n")) {
    const line = raw.trim();
    if (!pending && (!line || line.startsWith("#") || /^(?:::|rem(?:\s|$))/iu.test(line))) continue;
    if (!joinable(line)) return null;
    if (continuation.test(line)) { pending += (/[\\`]$/u.test(line) ? line.slice(0, -1).trimEnd() : line) + " "; continue; }
    joined.push(pending + line);
    pending = "";
  }
  if (pending) return null;
  // The Command Prompt expands the %VARIABLES% of a line before it runs any of its commands: `set A=1 & echo %A%` would not
  // see the A it sets, so commands that name a variable stay on lines of their own.
  if (cmd && joined.length > 1 && joined.some(line => line.includes("%"))) return null;
  // Nor a remark after a command of the Command Prompt, which would take the rest of the line.
  if (cmd && joined.some(line => /[&|]\s*(?:rem\b|::)/iu.test(line))) return null;
  return joined.length ? joined.join(separator) : null;
}

// A character a terminal reads as a key, not as text: the controls but the tab and the line feed, DEL and the C1 controls
// (ESC would end a bracketed paste; Ctrl+O accepts the line in some modes of PSReadLine).
const controlCharacter = /[\u0000-\u0008\u000B-\u001F\u007F-\u009F]/u;

/** What a terminal is given for the commands of a block: the text to paste, or why they are copied instead. */
export type TerminalPaste = Readonly<{ text: string }> | Readonly<{ refused: "control" | "lines" }>;

/**
 * The text that pastes the commands of a block without running any of them. A shell that takes a paste as one block
 * (`pastesBlocks`) gets the lines as they are; another gets them on one line, its tabs made spaces (a tab completes), or
 * nothing when they cannot be joined safely. Commands that hold a control character are never pasted.
 */
export function terminalPaste(commands: string, pastesBlocks: boolean, profile: string | null | undefined): TerminalPaste {
  if (controlCharacter.test(commands)) return { refused: "control" };
  if (pastesBlocks) return { text: commands };
  const text = commands.includes("\n") ? joinCommandLines(commands, commandSeparator(profile)) : commands;
  return text === null ? { refused: "lines" } : { text: text.replace(/\t/gu, " ") };
}
