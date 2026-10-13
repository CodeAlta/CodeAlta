import type { HistoryResponse } from "#neoastra";
import { compactTokens } from "./contextUsage";

type HistoryEntry = HistoryResponse["entries"][number];

/** The lines a prompt added to and removed from the one it replaced. */
export type PromptChange = Readonly<{ added: number; removed: number }>;

/**
 * What the row of a system prompt record says, as its history entry gives it: the row and the header of its
 * window are drawn from it. `change` is known when the prompt that was replaced is in the same page of history.
 */
export type PromptRow = Readonly<{
  /** The prompt replaced an earlier one of its session. */
  changed: boolean;
  /** The agent prompt, by its display name or its identifier. */
  name: string | null; sourcePath: string | null; hash: string;
  tokens: number; systemTokens: number; developerTokens: number;
  applied: boolean; lossy: boolean; change: PromptChange | null;
}>;

/** The row of a system prompt record. An entry without the facts of the host says only whether it is the first. */
export function promptRow(entry: HistoryEntry): PromptRow {
  const facts = entry.prompt;
  return {
    changed: facts ? facts.changed : entry.kind?.toLowerCase() !== "session_start",
    name: entry.name?.trim() || null, sourcePath: facts?.sourcePath || null, hash: facts?.hash ?? "",
    tokens: facts?.totalTokens ?? 0, systemTokens: facts?.systemTokens ?? 0, developerTokens: facts?.developerTokens ?? 0,
    applied: facts?.applied ?? true, lossy: facts?.lossy ?? false,
    change: facts && facts.added != null && facts.removed != null ? { added: facts.added, removed: facts.removed } : null,
  };
}

/** A token count as the application writes them: `11.2k`. */
export function promptTokens(count: number): string {
  return compactTokens(String(Math.max(0, Math.round(count))));
}

/** What went wrong between the prompt and the provider, when something did: the row and the window warn of it. */
export function promptProblem(row: Pick<PromptRow, "applied" | "lossy">): "notApplied" | "lossy" | null {
  return !row.applied ? "notApplied" : row.lossy ? "lossy" : null;
}

/** The first characters of a hash, enough to tell two prompts apart; a prefix such as `sha256:` is left out. */
export function shortHash(hash: string): string {
  return hash.slice(hash.indexOf(":") + 1).slice(0, 8).toLowerCase();
}

/**
 * The whole prompt as one Markdown text: the system message, then the developer instructions, each under a
 * title. The titles are underlined, which the headings of a prompt (`#`) are not: they stand apart from them.
 * The two sides of a comparison are built the same way, so that only the lines of the prompt differ.
 */
export function promptDocument(system: string | null, developer: string | null, titles: Readonly<{ system: string; developer: string }>): string {
  const part = (title: string, text: string | null) => `${title}\n${"=".repeat(Math.max(3, title.length))}\n\n${normalize(text)}`;
  return `${part(titles.system, system)}\n\n${part(titles.developer, developer)}\n`;
}

/** Which parts differ between two prompts: the reader is told where to look. */
export function changedParts(previous: Readonly<{ system: string | null; developer: string | null }>,
  current: Readonly<{ system: string | null; developer: string | null }>): Readonly<{ system: boolean; developer: boolean }> {
  return { system: normalize(previous.system) !== normalize(current.system), developer: normalize(previous.developer) !== normalize(current.developer) };
}

// A part as it is shown and compared: without the blank lines around it, with the line ends of the page.
function normalize(text: string | null): string {
  return (text ?? "").replaceAll("\r\n", "\n").trim();
}
