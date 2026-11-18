/**
 * The `#token` at the caret that opens the issue picker: `#` at the start of the prompt or after
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

/** The characters that end a `#123` typed in the picker: a space, or what ends a sentence or closes a bracket. */
export function endsTypedIssueNumber(key: string) {
  return key.length === 1 && /[\s.,;:!?)\]}]/u.test(key);
}

/**
 * Keeps a `#123` typed in the picker as plain text: the number of an issue the user already knows, ended by `ending`
 * (a key of `endsTypedIssueNumber`), replaces the `#token` and the caret goes after the ending. Only digits count;
 * another query may be a real search. A closing bracket that the editor already added is not doubled, and no second
 * space is added before one.
 */
export function typedIssueNumber(text: string, start: number, end: number, query: string, ending = " ") {
  if (!/^\d{1,18}$/u.test(query) || !endsTypedIssueNumber(ending)) return null;
  const reference = `#${query}`;
  const space = /\s/u.test(ending);
  const next = text[end] ?? "";
  const present = space ? /\s/u.test(next) : next === ending;
  const value = text.slice(0, start) + reference + (present ? "" : ending) + text.slice(end);
  return value.length <= 32768 ? { text: value, caret: start + reference.length + 1 } : null;
}

/**
 * Replaces the `#token` with a Markdown link to the issue, as the terminal UI does: `[#123](url)`. The host
 * only hands out links to the repository's own provider; here the link must be plain `https` text that
 * cannot end the Markdown link early.
 */
export function insertIssueReference(text: string, start: number, end: number, issue: number, url: string) {
  if (!Number.isSafeInteger(issue) || issue <= 0 || !/^https:\/\/[a-z0-9][a-z0-9.-]*\/[^\s()<>"\[\]]+$/iu.test(url) || url.length > 1024) return null;
  const reference = `[#${issue}](${url})`;
  const value = text.slice(0, start) + reference + text.slice(end);
  return value.length <= 32768 ? { text: value, caret: start + reference.length } : null;
}

/**
 * Replaces the `#token`, or inserts at the caret, the links of several issues, separated by spaces: the issues chosen
 * together in the picker. An insertion right after a word starts with a space.
 */
export function insertIssueReferences(text: string, start: number, end: number, issues: readonly Readonly<{ number: number; url: string }>[]) {
  if (issues.length === 0) return null;
  let value = text.slice(0, start);
  let caret = start;
  const separated = start === 0 || /[\s(\[{]/u.test(text[start - 1]);
  for (const [index, issue] of issues.entries()) {
    const single = insertIssueReference("", 0, 0, issue.number, issue.url);
    if (!single) return null;
    const piece = (index > 0 || !separated ? " " : "") + single.text;
    value += piece; caret += piece.length;
  }
  value += text.slice(end);
  return value.length <= 32768 ? { text: value, caret } : null;
}
