import type { Live, SessionModel, Space } from "./contract";
import type { SessionView } from "./sessionStore";

// The facts a card and the page of a session show of a session, in a few words each, without the DOM.

/** The word beside the dot of a card, only while the session works: what it does. */
export function liveWord(live: Live | null | undefined): string | null {
  switch (live?.live) {
    case "running": return live.kind === "writing" ? "Writing" : "Working";
    case "tools": return "Tools";
    case "thinking": return "Thinking";
    default: return null;
  }
}

/** The model and its effort as the composer of the desktop says them: "GPT-6.1 Sol · low"; null when the session names none. */
export function modelLabel(model: SessionModel | null | undefined): string | null {
  if (!model) return null;
  const name = model.name || model.id || model.providerName || model.provider;
  return [name, model.reasoning].filter(Boolean).join(" · ") || null;
}

/** The agent prompt, when it is not the default one. */
export function agentLabel(agent: SessionView["agent"]): string | null {
  if (!agent || agent.id.toLowerCase() === "default") return null;
  return agent.name || agent.id;
}

/** Whether a card names its space: when the phone sees more than one (no list of spaces means every space). */
export const showSpaces = (spaces: readonly Space[]): boolean => spaces.length !== 1;

/** A fact of the meta line of a session, in the order it is shown. */
export type MetaItem = Readonly<
  | { kind: "live"; text: string }
  | { kind: "space"; text: string; color: string | null }
  | { kind: "project"; text: string }
  | { kind: "worktree"; text: string }
  | { kind: "model"; text: string }
  | { kind: "agent"; text: string }
  | { kind: "child"; text: string }>;

/**
 * The facts of a session, compact: what it does now (a card only), its space (when the phone sees several, or always on the page of the
 * session), its project, its worktree, its model and effort and its agent when it is not the default one (unless the composer shows
 * them, `selection: false`), and whether it is a sub-agent.
 */
export function sessionMeta(view: SessionView, options: Readonly<{ space: boolean; live: boolean; selection?: boolean }>): MetaItem[] {
  const items: MetaItem[] = [];
  const word = options.live ? liveWord(view.live) : null;
  if (word) items.push({ kind: "live", text: word });
  if (options.space && view.space) items.push({ kind: "space", text: view.space.name, color: view.space.color });
  if (view.project) items.push({ kind: "project", text: view.project });
  if (view.worktree) items.push({ kind: "worktree", text: view.worktree });
  if (options.selection !== false) {
    const model = modelLabel(view.model);
    if (model) items.push({ kind: "model", text: model });
    const agent = agentLabel(view.agent);
    if (agent) items.push({ kind: "agent", text: agent });
  }
  if (view.parentId) items.push({ kind: "child", text: "Sub-agent" });
  return items;
}

/** A color the page may put in a style: a hex color, or nothing. */
export const safeColor = (color: string | null | undefined): string | null => color && /^#[0-9a-f]{3,8}$/iu.test(color) ? color : null;
