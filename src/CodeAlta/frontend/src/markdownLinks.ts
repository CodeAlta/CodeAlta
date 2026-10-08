/** The same bounded HTTP(S) policy used by the sanitizer and the host opener. */
export function safeMarkdownHref(value: string): boolean {
  if (!value || value.length > 2048 || !/^https?:\/\/[^/?#]+/i.test(value)
    || /[\s\u0000-\u001f\u007f-\u009f\\]/u.test(value) || /%(?![0-9a-f]{2})/i.test(value)
    || /^https?:\/\/[^/?#]*@/i.test(value)) return false;
  try {
    const url = new URL(value);
    return !url.username && !url.password && !!url.hostname && url.href.length <= 2048;
  } catch { return false; }
}

/** A trusted activation, not a right click, repeated/composing Enter, or an already handled event. */
export function markdownLinkActivation(event: { type: string; isTrusted: boolean; defaultPrevented: boolean; altKey?: boolean;
  button?: number; key?: string; repeat?: boolean; isComposing?: boolean; keyCode?: number }): boolean {
  if (!event.isTrusted || event.defaultPrevented || event.altKey) return false;
  return event.type === "click" && event.button === 0 || event.type === "auxclick" && event.button === 1
    || event.type === "keydown" && event.key === "Enter" && !event.repeat && !event.isComposing && event.keyCode !== 229;
}

/** The narrowly granted host action; never a WebView navigation or window.open fallback. */
export async function openMarkdownLink(open: (request: { expectedHostEpoch: string; address: string }) => Promise<{ status: string; hostEpoch: string }>,
  epoch: string | null, address: string, current: () => boolean,
  observe?: (result: { status: string; epoch: string | null }) => void): Promise<"ok" | "failed" | "stale_epoch" | "invalid_request" | "unavailable"> {
  if (!epoch) return "unavailable";
  if (!current()) return "stale_epoch";
  if (!safeMarkdownHref(address)) return "invalid_request";
  try {
    const reply = await open({ expectedHostEpoch: epoch, address });
    observe?.({ status: reply.status, epoch: reply.hostEpoch }); // The shared capability's envelope uses `epoch`, not `hostEpoch`.
    if (!current() || reply.hostEpoch !== epoch || reply.status === "stale_epoch") return "stale_epoch";
    return reply.status === "ok" ? "ok" : "failed";
  } catch { return current() ? "failed" : "stale_epoch"; }
}
