/** The longest target that is opened: `DesktopLinks.MaximumLength` and `DesktopFileLink.MaximumLength` of the host. */
export const maximumMarkdownHrefLength = 2048;

// The length of the address of a page of the web, as it is written or as it is opened; null for anything else.
function webHrefLength(value: string): number | null {
  if (!value || !/^https?:\/\/[^/?#]+/i.test(value)
    || /[\s\u0000-\u001f\u007f-\u009f\\]/u.test(value) || /%(?![0-9a-f]{2})/i.test(value)
    || /^https?:\/\/[^/?#]*@/i.test(value)) return null;
  try {
    const url = new URL(value);
    return !url.username && !url.password && url.hostname ? Math.max(value.length, url.href.length) : null;
  } catch { return null; }
}

/** The same bounded HTTP(S) policy used by the sanitizer and the host opener. */
export function safeMarkdownHref(value: string): boolean {
  const length = webHrefLength(value);
  return length !== null && length <= maximumMarkdownHrefLength;
}

// The place written after a path: ":10", ":10:5", ":10-20".
const placeAfterPath = /:\d{1,7}(?::\d{1,7})?(?:-\d{1,7}(?::\d{1,7})?)?$/;

/**
 * Whether a target is written as a file of this computer: a relative or a full path, with the place in the
 * file that may follow it (`#L10`, `:10`), or a `file:` address without a host. The host reads the target again
 * and finds the file; this only tells which links are shown as links. A path of another computer (`//server`),
 * another scheme and a fragment alone are none.
 */
export function fileMarkdownHref(value: string): boolean {
  return value.length <= maximumMarkdownHrefLength && writtenAsFile(value);
}

function writtenAsFile(value: string): boolean {
  if (!value || value !== value.trim() || /[\u0000-\u001f\u007f-\u009f]/u.test(value)) return false;
  if (/^file:/i.test(value)) return /^file:\/\/(?:localhost)?\/[^/\\]/i.test(value);
  let path = value.split("#", 1)[0];
  try { path = decodeURIComponent(path); } catch { /* Not escapes: the path is as it is written. */ }
  path = path.replaceAll("\\", "/");
  if (!path || path.startsWith("//") || /[\u0000-\u001f\u007f-\u009f]/u.test(path)) return false;
  if (!value.includes("#")) path = path.replace(placeAfterPath, "");
  const colon = path.indexOf(":");
  if (colon < 0) return path.length > 0;
  // A drive is the one colon of a path of Windows; elsewhere a name may have one, after a folder.
  return /^\/?[a-z]:(?:\/[^:]*)?$/i.test(path) || path.slice(0, colon).includes("/");
}

/** What a link of rendered Markdown opens: a page of the web, a file of this computer, or nothing. */
export function markdownHrefKind(value: string): "web" | "file" | null {
  return safeMarkdownHref(value) ? "web" : fileMarkdownHref(value) ? "file" : null;
}

/** What a link of rendered Markdown names, whatever its length: a link too long to open is still shown as a link. */
export function markdownHrefTarget(value: string): "web" | "file" | null {
  return webHrefLength(value) !== null ? "web" : writtenAsFile(value) ? "file" : null;
}

/** The length of a link that names a page or a file but is too long to open, as the host refuses it; null for any other. */
export function tooLongMarkdownHref(value: string): number | null {
  if (!markdownHrefTarget(value) || markdownHrefKind(value)) return null;
  return webHrefLength(value) ?? value.length;
}

/** A trusted activation, not a right click, repeated/composing Enter, or an already handled event. */
export function markdownLinkActivation(event: { type: string; isTrusted: boolean; defaultPrevented: boolean; altKey?: boolean;
  button?: number; key?: string; repeat?: boolean; isComposing?: boolean; keyCode?: number }): boolean {
  if (!event.isTrusted || event.defaultPrevented || event.altKey) return false;
  return event.type === "click" && event.button === 0 || event.type === "auxclick" && event.button === 1
    || event.type === "keydown" && event.key === "Enter" && !event.repeat && !event.isComposing && event.keyCode !== 229;
}

/**
 * Where a relative link of a text starts from: the folder the session of a message works in, or the folder of
 * a document inside a project (or inside a folder the code editor is open on).
 */
export type MarkdownLinkScope = Readonly<{ sessionId?: string; projectId?: string; directory?: string }>;

/** What following a link gave: `not_found` and `binary` are answers for a file. */
export type MarkdownLinkResult = "ok" | "failed" | "stale_epoch" | "invalid_request" | "unavailable" | "not_found" | "binary";

/** The narrowly granted host action; never a WebView navigation or window.open fallback. */
export async function openMarkdownLink(open: (request: { expectedHostEpoch: string; address: string; sessionId: string | null; projectId: string | null; directory: string | null }) => Promise<{ status: string; hostEpoch: string }>,
  epoch: string | null, address: string, current: () => boolean,
  observe?: (result: { status: string; epoch: string | null }) => void, scope?: MarkdownLinkScope | null): Promise<MarkdownLinkResult> {
  if (!epoch) return "unavailable";
  if (!current()) return "stale_epoch";
  if (!markdownHrefKind(address)) return "invalid_request";
  try {
    const reply = await open({ expectedHostEpoch: epoch, address, sessionId: scope?.sessionId ?? null, projectId: scope?.projectId ?? null, directory: scope?.directory ?? null });
    observe?.({ status: reply.status, epoch: reply.hostEpoch }); // The shared capability's envelope uses `epoch`, not `hostEpoch`.
    if (!current() || reply.hostEpoch !== epoch || reply.status === "stale_epoch") return "stale_epoch";
    return reply.status === "ok" || reply.status === "not_found" || reply.status === "binary" ? reply.status : "failed";
  } catch { return current() ? "failed" : "stale_epoch"; }
}
