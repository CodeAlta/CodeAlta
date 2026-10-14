import type { ClaudeCliInstallState } from "#neoastra";
import type { MessageKey } from "../localization";

/** The shell the commands to copy are written for, and the home folder they write as $HOME. */
export type CliShell = Readonly<{ windows: boolean; home: string | null }>;

/** What is after the home folder in a path, with its leading separator; null for a path outside it. */
function underHome(path: string, shell: CliShell): string | null {
  if (path === "~") return "";
  if (path.startsWith("~/") || path.startsWith("~\\")) return path.slice(1);
  const home = shell.home?.replace(/[\\/]+$/, "");
  if (!home) return null;
  const head = path.slice(0, home.length);
  if (shell.windows ? head.toLowerCase() !== home.toLowerCase() : head !== home) return null;
  const rest = path.slice(home.length);
  return rest === "" || /^[\\/]/.test(rest) ? rest : null;
}

/**
 * A path in double quotes for the shell: in a folder of the home folder it starts with $HOME, which the shell
 * expands; what follows is escaped, so that the shell expands nothing else.
 */
export function quotePath(path: string, shell: CliShell): string {
  const trimmed = path.trim();
  const rest = underHome(trimmed, shell);
  const native = (value: string) => shell.windows ? value.replace(/\//g, "\\") : value;
  // PowerShell escapes with a backtick; a POSIX shell with a backslash.
  const escape = (value: string) => shell.windows ? value.replace(/[`"$]/g, character => "`" + character) : value.replace(/[\\"$`]/g, character => "\\" + character);
  return rest === null ? `"${escape(native(trimmed))}"` : `"$HOME${escape(native(rest))}"`;
}

/**
 * The command that starts Claude Code in a terminal with the account of a provider, to sign in with /login there:
 * the executable the provider runs, and the folder of its account in CLAUDE_CONFIG_DIR when it has one.
 */
export function claudeLoginCommand(shell: CliShell, executable: string | null, configDir: string): string {
  const program = executable?.trim() ? quotePath(executable, shell) : null;
  const folder = configDir.trim() ? quotePath(configDir, shell) : null;
  if (shell.windows) {
    const run = program ? `& ${program}` : "claude";
    return folder ? `$env:CLAUDE_CONFIG_DIR=${folder}; ${run}` : run;
  }

  const run = program ?? "claude";
  return folder ? `CLAUDE_CONFIG_DIR=${folder} ${run}` : run;
}

/** Whether a folder a provider is given is one the configuration file takes: a full path, or one that starts with ~. */
export function validConfigDir(value: string): boolean {
  const folder = value.trim();
  return !folder || folder === "~" || folder.startsWith("~/") || folder.startsWith("~\\") || folder.startsWith("/") || folder.startsWith("\\") || /^[A-Za-z]:[\\/]/.test(folder);
}

/** Whether two folders a provider is given are the same setting. */
export function sameConfigDir(left: string | null | undefined, right: string | null | undefined): boolean {
  const normal = (value: string | null | undefined) => (value ?? "").trim().replace(/[\\/]+$/, "");
  return normal(left) === normal(right);
}

export type CliInstallPhase = "idle" | "downloading" | "installing" | "done" | "failed" | "canceled";

/** The download as the page shows it. */
export type CliInstallView = Readonly<{ state: CliInstallPhase; received: number; total: number; error: string | null }>;

const phases: readonly string[] = ["idle", "downloading", "installing", "done", "failed", "canceled"];
// An Int64 of the host is a string of digits.
const bytes = (value: string | null | undefined) => {
  if (!value || !/^\d+$/.test(value)) return 0;
  const number = Number(value);
  return Number.isFinite(number) ? number : 0;
};

/** What the host says of the download; a state the page does not know reads as no download. */
export function cliInstallView(wire: ClaudeCliInstallState): CliInstallView {
  return { state: (phases.includes(wire.state) ? wire.state : "idle") as CliInstallPhase, received: bytes(wire.receivedBytes), total: bytes(wire.totalBytes), error: wire.error };
}

/** A size of the host, a string of digits, as a number. */
export const cliBytes = bytes;

/** Whether the download runs. */
export const cliInstalling = (view: CliInstallView | null) => view?.state === "downloading" || view?.state === "installing";

const failures: Readonly<Record<string, MessageKey>> = Object.freeze({
  network: "Claude Code could not be downloaded: check the network connection.",
  not_found: "This version of Claude Code was not found on GitHub.",
  checksum: "The download of Claude Code does not match the version CodeAlta is tested with: download it again.",
  busy_elsewhere: "Another CodeAlta is downloading Claude Code.",
  disk: "Claude Code could not be written to the disk.",
  unsupported_platform: "Claude Code has no version for this system.",
});

/** What to say of a download that failed. */
export const cliInstallFailure = (error: string | null): MessageKey => failures[error ?? ""] ?? "Claude Code could not be installed.";
