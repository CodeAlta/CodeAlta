/** Validate bounded host UTF-16 spans only; this does not parse or resolve references. */
export function validReferenceSpans(text: string, spans: readonly { start: number; length: number; status: string }[]): boolean {
  if (!Array.isArray(spans) || spans.length > 256) return false;
  let end = 0;
  return spans.every(span => {
    if (!span || !Number.isSafeInteger(span.start) || !Number.isSafeInteger(span.length) || span.start < end || span.length <= 0 || span.start + span.length > text.length
      || !["resolved", "unresolved", "escaped"].includes(span.status)) return false;
    end = span.start + span.length; return true;
  });
}
// Only the host parser/resolver confers reference meaning. These helpers find an
// editable trigger and format the TUI's basename/project-relative Markdown link;
// no filesystem inference or change to host reference authority.
export function activeProjectReference(text: string, caret: number) {
  const before = text.slice(0, caret);
  const match = /(?:^|[\s(\[{"'<])@("[^"\r\n]*|[^@\s":\r\n]*)$/u.exec(before);
  if (!match) return null;
  const query = match[1].replace(/^"/u, "");
  if (query.length > 256) return null;
  let end = caret;
  if (match[1].startsWith('"')) {
    const quote = text.indexOf('"', caret);
    if (quote >= 0) end = quote + 1;
  } else while (end < text.length && !/[\s,:;!?)\]}>]/u.test(text[end])) end++;
  return { start: caret - match[1].length - 1, end, query };
}

export function insertProjectReference(text: string, start: number, end: number, path: string, directory: boolean) {
  if (!path || path.length > 1024 || /[\u0000-\u001f\u007f"\\:<>|*?]/u.test(path) || path.startsWith("/")
    || path.split("/").some(part => !part || part === "." || part === "..")) return null;
  const range = !directory ? /^:[1-9]\d*(?:-[1-9]\d*)?(?=$|[\s,;!?)\]}>])/u.exec(text.slice(end))?.[0] ?? "" : "";
  const label = path.slice(path.lastIndexOf("/") + 1).replaceAll("]", "\\]");
  const reference = `[${label}](${path}${range})`;
  end += range.length;
  const value = text.slice(0, start) + reference + text.slice(end);
  return value.length <= 32768 ? { text: value, caret: start + reference.length } : null;
}
