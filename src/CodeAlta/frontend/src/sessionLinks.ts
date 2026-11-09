// The links of a text to a session, `[title](session:<id>)`, or to a message of it, `session:<id>#<offset>`: the text
// shows them as chips that say what the session is doing and open it.
import { createContext } from "react";

/** What a link to a session names: the session, and the message (the offset of its record in the journal) to show. */
export type SessionLinkTarget = Readonly<{ sessionId: string; offset: string | null }>;

/** What the window knows of a session a link names. */
export type SessionLinkInfo = Readonly<{
  title: string;
  /** The name of its project, or of the chats; null where it is not known. */
  scope: string | null;
  state: "running" | "idle" | "archived";
  /** When it was last active, as the host writes it. */
  updatedAt: string | null;
}>;

/** What finds the sessions the links of the texts of the window name, and opens one. */
export type SessionLinkHost = Readonly<{
  /** The session of a link; null when the window does not know it. */
  describe(sessionId: string): SessionLinkInfo | null;
  /** Opens the session, at the message a link names. */
  open(target: SessionLinkTarget): void;
}>;

/** The session links of the window; null where they open nothing (a window without a host, the preview of a folded row). */
export const SessionLinkHostContext = createContext<SessionLinkHost | null>(null);

/** The attribute of a link that names a session: the id of that session. */
export const sessionLinkAttribute = "data-session-link";

// An id of a session: letters, digits, `-` and `_`, as the host writes them. An offset is a number of bytes.
const sessionAddress = /^session:([A-Za-z0-9][A-Za-z0-9_-]{0,127})(?:#(\d{1,18}))?$/;

/** The session and the message a link names, or null for a link that is not one to a session. */
export function sessionLinkTarget(href: string | null | undefined): SessionLinkTarget | null {
  const match = href ? sessionAddress.exec(href) : null;
  return match ? { sessionId: match[1], offset: match[2] ?? null } : null;
}

/** The address of a link to a session, or to one of its messages. */
export function sessionLinkAddress(target: SessionLinkTarget): string {
  return `session:${target.sessionId}${target.offset ? `#${target.offset}` : ""}`;
}

const dressing = [sessionLinkAttribute, "data-session-state", "data-session-scope", "aria-label"] as const;

/**
 * Marks the links of a rendered text that name a session with what the window knows of it: its state (`running`,
 * `idle`, `archived`, or `unknown` for a session it does not know), the name of its project and a tooltip. Without
 * `describe` (a text whose links are inert), the marks are removed and the links stay inert.
 */
export function dressSessionLinks(root: HTMLElement, describe: ((sessionId: string) => SessionLinkInfo | null) | null,
  label: (target: SessionLinkTarget, info: SessionLinkInfo | null, text: string) => string) {
  for (const link of Array.from(root.querySelectorAll<HTMLAnchorElement>("a[href^='session:']"))) {
    const target = describe ? sessionLinkTarget(link.getAttribute("href")) : null;
    if (!target) {
      if (link.hasAttribute(sessionLinkAttribute)) for (const name of dressing) link.removeAttribute(name);
      continue;
    }
    const info = describe!(target.sessionId);
    const text = link.textContent?.trim() ?? "";
    link.setAttribute(sessionLinkAttribute, target.sessionId);
    link.setAttribute("data-session-state", info?.state ?? "unknown");
    if (info?.scope) link.setAttribute("data-session-scope", info.scope);
    else link.removeAttribute("data-session-scope");
    const words = label(target, info, text);
    link.setAttribute("aria-label", words);
    link.setAttribute("title", words);
  }
}

/** The link to a session that an event happened on, in a text. */
export function sessionLinkAt(target: EventTarget | null, root: HTMLElement | null): HTMLAnchorElement | null {
  const link = target instanceof Element ? target.closest<HTMLAnchorElement>(`a[${sessionLinkAttribute}]`) : null;
  return link && root?.contains(link) ? link : null;
}
