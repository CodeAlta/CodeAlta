import type { LocalSearchHit } from "#neoastra";
import type { IconName } from "../AppIcon";

/**
 * What the search window finds in the index of the host, besides what the page ranks itself: the conversations (the
 * prompts and final answers of turns, the notes of sessions, the plans and tasks of projects), the sections of the
 * user guide, and the tools (agent tools, MCP servers, plugins).
 */
export type ContentGroup = "conversations" | "docs" | "tools";
export const contentGroups: readonly ContentGroup[] = ["conversations", "docs", "tools"];

/** The kinds of the index each group asks for. */
export const contentKinds: Readonly<Record<ContentGroup, readonly string[]>> = {
  conversations: ["message", "note", "plan", "task"],
  docs: ["doc"],
  tools: ["tool", "mcp", "plugin"],
};

export type ContentResult = Readonly<{ kind: "content"; key: string; score: number; group: ContentGroup; hit: LocalSearchHit }>;

/** The group of a kind of the index; null for the kinds the page finds itself (sessions, skills, commands). */
export function contentGroupOf(kind: string): ContentGroup | null {
  for (const group of contentGroups) if (contentKinds[group].includes(kind)) return group;
  return null;
}

/** The kinds to ask for, for a category of the window: every group for "all", one for its own category, none for the others. */
export function contentKindsFor(category: string): readonly string[] {
  if (category === "all") return contentGroups.flatMap(group => contentKinds[group]);
  return (contentGroups as readonly string[]).includes(category) ? contentKinds[category as ContentGroup] : [];
}

/**
 * The hits of the host as results of the window, in their groups and in the order the host ranked them. Their score
 * puts a group after the sessions and projects whose name starts with the words, as a match inside a text is.
 */
export function contentResults(hits: readonly LocalSearchHit[]): Readonly<Record<ContentGroup, readonly ContentResult[]>> {
  const found: Record<ContentGroup, ContentResult[]> = { conversations: [], docs: [], tools: [] };
  hits.forEach((hit, index) => {
    const group = contentGroupOf(hit.kind);
    if (group) found[group].push({ kind: "content", key: `content:${hit.ref}`, score: 2000 + index, group, hit });
  });
  return found;
}

/** The values of the `extra` of a hit, a JSON object of strings; none for anything else. */
export function hitExtra(hit: Pick<LocalSearchHit, "extra">): Readonly<Record<string, string>> {
  if (!hit.extra) return {};
  try {
    const value: unknown = JSON.parse(hit.extra);
    if (!value || typeof value !== "object" || Array.isArray(value)) return {};
    return Object.fromEntries(Object.entries(value).filter((entry): entry is [string, string] => typeof entry[1] === "string"));
  } catch { return {}; }
}

/** What Enter does with a hit. */
export type ContentTarget = Readonly<
  | { kind: "message"; ref: string; sessionId: string; projectId: string | null }
  | { kind: "session"; sessionId: string; projectId: string | null }
  | { kind: "doc"; page: string; anchor: string | null }
  | { kind: "file"; projectId: string; path: string }
  | { kind: "settings"; page: "mcp" | "plugins" }
  | { kind: "details"; title: string; text: string }>;

/** Where a hit leads: a message of a session, a page of the guide, a plan in the code editor, a page of the settings. */
export function contentTarget(hit: LocalSearchHit): ContentTarget {
  const extra = hitExtra(hit);
  switch (hit.kind) {
    case "message": return hit.sessionId ? { kind: "message", ref: hit.ref, sessionId: hit.sessionId, projectId: hit.projectId ?? null } : details(hit);
    case "note": case "session": return hit.sessionId ? { kind: "session", sessionId: hit.sessionId, projectId: hit.projectId ?? null } : details(hit);
    case "doc": return extra.page ? { kind: "doc", page: extra.page, anchor: extra.anchor ?? null } : details(hit);
    case "plan": case "task":
      return hit.projectId && extra.id ? { kind: "file", projectId: hit.projectId, path: `.alta/${hit.kind === "plan" ? "plans" : "tasks"}/${extra.id}.md` } : details(hit);
    case "mcp": return { kind: "settings", page: "mcp" };
    case "plugin": return { kind: "settings", page: "plugins" };
    default: return details(hit);
  }
}

const details = (hit: LocalSearchHit): ContentTarget => ({ kind: "details", title: hit.title, text: hit.snippet });

/** The icon of a hit. */
export function contentIcon(hit: Pick<LocalSearchHit, "kind" | "role">): IconName {
  switch (hit.kind) {
    case "message": return hit.role === "assistant" ? "assistant" : hit.role === "agent" ? "subAgents" : "user";
    case "note": return "notes";
    case "plan": return "plan";
    case "task": return "task";
    case "doc": return "documentation";
    case "mcp": return "server";
    case "plugin": return "plugin";
    default: return "tool";
  }
}

/** A snippet cut into its plain parts and the parts the query found, as the host marked them. */
export function markedParts(text: string, marks: readonly { start: number; length: number }[]): readonly Readonly<{ text: string; marked: boolean }>[] {
  const parts: { text: string; marked: boolean }[] = [];
  let at = 0;
  for (const mark of [...marks].sort((left, right) => left.start - right.start)) {
    if (mark.start < at || mark.length <= 0 || mark.start + mark.length > text.length) continue;
    if (mark.start > at) parts.push({ text: text.slice(at, mark.start), marked: false });
    parts.push({ text: text.slice(mark.start, mark.start + mark.length), marked: true });
    at = mark.start + mark.length;
  }
  if (at < text.length) parts.push({ text: text.slice(at), marked: false });
  return parts;
}
