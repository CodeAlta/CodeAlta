// What an agent prompt inherits, as the editor of Settings changes it: the ids of other agent prompts, in the order
// their text comes before the text of the prompt.
import type { AgentPromptEntry } from "#neoastra";

/** Largest number of prompts one prompt inherits, as the host checks it. */
export const maximumInherits = 16;

/** One agent prompt that can be inherited: its id, and the name and scope of the file in effect for it. */
export type InheritCandidate = Readonly<{ id: string; name: string; description: string | null; scope: string; entry: AgentPromptEntry }>;

/**
 * The file in effect for an agent prompt id among the files the page lists: the one no other file replaces, the one
 * that replaces rather than adds, and one of CodeAlta before a custom agent of GitHub Copilot.
 */
export function promptInEffect(prompts: readonly AgentPromptEntry[], id: string): AgentPromptEntry | null {
  const files = prompts.filter(prompt => prompt.kind === "Agent" && prompt.id.toLowerCase() === id.toLowerCase());
  return files.find(prompt => !prompt.shadowed && !prompt.append) ?? files.find(prompt => !prompt.shadowed) ?? files[0] ?? null;
}

/** The agent prompts a prompt can inherit: each id once, except its own and those it inherits already. */
export function inheritCandidates(prompts: readonly AgentPromptEntry[], self: string, inherits: readonly string[]): readonly InheritCandidate[] {
  const taken = new Set([self, ...inherits].map(id => id.toLowerCase()));
  const candidates: InheritCandidate[] = [];
  for (const prompt of prompts) {
    if (prompt.kind !== "Agent" || taken.has(prompt.id.toLowerCase())) continue;
    taken.add(prompt.id.toLowerCase());
    const entry = promptInEffect(prompts, prompt.id) ?? prompt;
    candidates.push({ id: entry.id, name: entry.name || entry.id, description: entry.description, scope: entry.scope, entry });
  }
  return candidates;
}

/** The list once a prompt is inherited too, last: each id once, at most {@link maximumInherits}. */
export function withInherited(inherits: readonly string[], id: string): readonly string[] {
  const trimmed = id.trim();
  return !trimmed || inherits.length >= maximumInherits || inherits.some(other => other.toLowerCase() === trimmed.toLowerCase()) ? inherits : [...inherits, trimmed];
}

/** The list once the prompt at an index comes one place earlier; the same list for the first one. */
export function movedEarlier(inherits: readonly string[], index: number): readonly string[] {
  if (index <= 0 || index >= inherits.length) return inherits;
  const next = [...inherits];
  [next[index - 1], next[index]] = [next[index], next[index - 1]];
  return next;
}

/** An approximate number of tokens as the page shows it: 950, 1.2k, 12k. */
export function tokenCount(tokens: number): string {
  if (tokens < 1000) return String(Math.max(0, Math.round(tokens)));
  const thousands = tokens / 1000;
  return `${thousands < 10 ? thousands.toFixed(1).replace(/\.0$/, "") : Math.round(thousands)}k`;
}
