/**
 * The `#token` at the caret that opens the GitHub issue picker: `#` at the start of the prompt or after
 * white space or an opening bracket, followed by the query typed so far. `##` (a Markdown heading) and
 * `word#1` are ordinary text.
 */
export function activeIssueReference(text: string, caret: number) {
  const match = /(?:^|[\s(\[{])#([^\s#()\[\]{}]*)$/u.exec(text.slice(0, caret));
  if (!match || match[1].length > 256 || text[caret] === "#") return null;
  let end = caret;
  while (end < text.length && !/[\s#,:;!?()\[\]{}]/u.test(text[end])) end++;
  return { start: caret - match[1].length - 1, end, query: match[1] };
}

/** Replaces the `#token` with a Markdown link to the issue, as the terminal UI does: `[#123](url)`. */
export function insertIssueReference(text: string, start: number, end: number, issue: number, url: string) {
  if (!Number.isSafeInteger(issue) || issue <= 0 || !/^https:\/\/github\.com\/[^\s()<>"]+$/u.test(url) || url.length > 1024) return null;
  const reference = `[#${issue}](${url})`;
  const value = text.slice(0, start) + reference + text.slice(end);
  return value.length <= 32768 ? { text: value, caret: start + reference.length } : null;
}
