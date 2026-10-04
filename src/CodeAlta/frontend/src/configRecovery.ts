import type { StartupConfigDocument, StartupConfigValidation } from "#neoastra";
import { maximumConfigLength } from "./configEditor";
import type { MessageKey } from "./localization";

/** What the line under the recovery editor says: one state, with the host's own words as detail. */
export type RecoveryStatus = Readonly<{
  intent: "success" | "warning" | "danger"; key: MessageKey; parameters?: Readonly<Record<string, string | number>>;
  /** The parser's or the file system's message, shown as it is. */
  detail: string | null;
  /** Where the editor can take the caret, 1-based. */
  position: Readonly<{ line: number; column: number }> | null;
}>;

/**
 * The state of the configuration being repaired. `validation` is the check of `content` (null until it
 * arrives); `failure` is why the last save did not go through.
 */
export function recoveryStatus(document: Pick<StartupConfigDocument, "status" | "failure"> | null, content: string,
  validation: StartupConfigValidation | null, failure: string | null): RecoveryStatus {
  const plain = (intent: RecoveryStatus["intent"], key: MessageKey, detail: string | null = null): RecoveryStatus => ({ intent, key, detail, position: null });
  if (!document) return plain("warning", "Reading configuration…");
  if (document.status === "too_large" || content.length > maximumConfigLength) return plain("danger", "The configuration is too large for this editor.");
  if (document.status !== "ok") return plain("danger", "The configuration file could not be read.", document.failure);
  if (failure) return plain("danger", "The configuration was not saved.", failure);
  if (!validation) return plain("warning", "Checking the configuration…");
  if (validation.valid) return plain("success", "The configuration is valid.");
  return validation.line
    ? { intent: "danger", key: "Line {line}, column {column}", parameters: { line: validation.line, column: validation.column ?? 1 },
      detail: recoveryDetail(validation.message), position: { line: validation.line, column: validation.column ?? 1 } }
    : plain("danger", "Invalid configuration", recoveryDetail(validation.message));
}

/**
 * The parser's diagnostics without what the screen already shows. Each is reported as
 * `path(line,column) : error : text`: the path is above the editor and the first position is the status
 * itself, so the first diagnostic keeps its text and the following ones their position and text.
 */
export function recoveryDetail(message: string | null): string | null {
  if (!message) return null;
  const diagnostics: string[] = [];
  const lines = message.split(/\r?\n/).map(line => line.trim()).filter(line => line.length > 0);
  for (const [index, line] of lines.entries()) {
    const match = /\((\d+),(\d+)\)\s*:\s*\w+\s*:\s*(.+)$/.exec(line);
    // A last line cut by the length limit says nothing useful once others were understood.
    if (!match) { if (diagnostics.length === 0 || index < lines.length - 1) diagnostics.push(line); continue; }
    diagnostics.push(diagnostics.length === 0 ? match[3] : `${match[1]}:${match[2]} ${match[3]}`);
  }
  return diagnostics.length > 0 ? diagnostics.join("\n") : null;
}

/** Saving needs a file that was read, text that is valid as it stands, and no save under way. */
export function canSaveRecovery(document: Pick<StartupConfigDocument, "status"> | null, content: string,
  validation: StartupConfigValidation | null, busy: boolean): boolean {
  return !busy && document?.status === "ok" && content.length <= maximumConfigLength && validation?.valid === true;
}
