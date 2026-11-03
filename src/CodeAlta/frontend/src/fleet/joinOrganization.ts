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
