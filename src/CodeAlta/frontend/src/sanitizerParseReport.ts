/** A frame of the stack of a console entry of the DevTools protocol. */
export type ReportFrame = { functionName: string };

/**
 * Whether a security report of the console is the one Chromium 156 makes for an inline event handler as soon as
 * DOMPurify parses it into the inert document of `DOMParser`, before it removes it: the handler never runs there.
 * The report must come from that parse alone (DOMPurify's `_initDocument`, called by `DOMPurify.sanitize`, with
 * nothing above it but `parseFromString`); any other inline handler is a failure of the browser tests.
 */
export function isSanitizerParseReport(text: string, frames: readonly ReportFrame[] | undefined): boolean {
  if (!text.startsWith("Executing inline event handler") || frames === undefined) return false;
  const names = frames.map(frame => frame.functionName);
  const init = names.findIndex(name => /^_initDocument\d*$/.test(name));
  return init >= 0 && names[init + 1] === "DOMPurify.sanitize" && names.slice(0, init).every(name => name.endsWith("parseFromString"));
}
