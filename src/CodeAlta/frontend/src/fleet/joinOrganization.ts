import type { MessageKey } from "../localization";

/**
 * Whose server a computer joins: the person's own (at home they are its owner, and another computer of theirs invites and
 * approves this one) or the server of an organization (an administrator gives the address and approves).
 */
export type JoinCase = "own" | "organization";

/** The words of the dialog that depend on whose server it is. */
export interface JoinWords { title: MessageKey; address: MessageKey; addressHelp: MessageKey; code: MessageKey; codeHelp: MessageKey; spaces: MessageKey; link: MessageKey }

/** The words of the dialog for a server of the person or of an organization. */
export function joinWords(kind: JoinCase): JoinWords {
  return kind === "own" ? {
    title: "Connect to my server", address: "Address of your server",
    addressHelp: "Settings > Fleet > Server shows it on the computer that created your server.",
    code: "Sign in to {server} with the enrollment code your other computer made with Invite a computer…. That computer then approves this one.",
    codeHelp: "Your other computer makes it in Settings > Fleet > Server.",
    spaces: "Which spaces show their sessions on your server? Their states and counts are published there, never their content.",
    link: "Link the spaces",
  } : {
    title: "Join an organization", address: "Address of your organization's server",
    addressHelp: "Your administrator gives it, or your company's configuration sets it.",
    code: "Sign in to {server} with the one-time enrollment code your administrator made for you. This computer then waits for their approval.",
    codeHelp: "Your administrator makes it for you.",
    spaces: "Which space works with {organization}? Its sessions are published to the server: their states and counts, never their content.",
    link: "Link the space",
  };
}

/**
 * The spaces chosen at first: on the person's own server, every space they have (their sessions are theirs to see there); on an
 * organization's, a new Work space, which keeps work apart from the rest.
 */
export function defaultJoinSpaces(kind: JoinCase, spaces: readonly { id: string }[]): string[] {
  return kind === "own" ? spaces.map(space => space.id) : ["new"];
}

/** The spaces of this computer not linked to a server yet, which Link all my spaces links: in their order. */
export function spacesToLink(spaces: readonly { id: string }[], linked: readonly string[]): string[] {
  return spaces.map(space => space.id).filter(id => !linked.includes(id));
}

/** The steps of Join an organization, in their order. */
export type JoinStep = "address" | "signin" | "space" | "approval";

/** The steps, in their order. */
export const joinSteps: readonly JoinStep[] = ["address", "signin", "space", "approval"];

/**
 * The address of a server as typed: `https://host`, `http://host:port`, or a bare host name taken as `https://`; empty
 * when it cannot be one.
 */
export function organizationAddressOf(text: string): string {
  let value = text.trim().replace(/\/+$/u, "");
  if (!value) return "";
  if (!/^https?:\/\//iu.test(value)) value = `https://${value}`;
  try {
    const url = new URL(value);
    if (!url.hostname || url.username || url.password || url.search || url.hash) return "";
    return `${url.protocol}//${url.host}${url.pathname === "/" ? "" : url.pathname}`;
  } catch {
    return "";
  }
}
