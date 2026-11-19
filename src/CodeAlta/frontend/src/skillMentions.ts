import { createContext, type RefObject } from "react";
import type { PromptInput } from "./PromptEditor";
import type { SkillChoice } from "./search/skillResults";

/** A `/name` in a text: from `start` (the slash) to `end`. */
export type SkillMentionSpan = Readonly<{ start: number; end: number; name: string }>;

/**
 * The skills of the project in front, that a prompt names as `/name` (`skills`: null while they are being read, or
 * where no session runs), what reads them again (`refresh`: the `/` picker does when it opens), and where a prompt names
 * skills as the host reads it when the prompt is sent (`mentions`): the host reads the prompt as Markdown, and code, links
 * and HTML name none. `canBegin` says whether the `/` typed at an offset can begin a skill: not in code, also a code span
 * not closed yet, and not after a backslash.
 */
export type SkillChoices = Readonly<{
  skills: readonly SkillChoice[] | null; refresh: () => void;
  mentions: (text: string) => Promise<readonly SkillMentionSpan[]>;
  canBegin: (text: string, at: number) => Promise<boolean>;
}>;
export const SkillChoicesContext = createContext<SkillChoices | null>(null);

// A `/name` in plain text, as the host reads one (SkillPromptMentions): a slash at the start of the text, after white space
// or an opening bracket, then a name that starts and ends with a letter or a decimal digit, then white space, a
// punctuation mark or the end of the text. White space is what .NET takes for some (`char.IsWhiteSpace`), not `\s`.
const space = "\\t-\\r\\u0085\\p{Z}";
const mention = new RegExp(`(?<=^|[${space}(\\[{])/([\\p{L}\\p{Nd}](?:[\\p{L}\\p{Nd}_.:-]*[\\p{L}\\p{Nd}])?)(?=$|[${space}.,;:!?)\\]}"'])`, "gu");
const maximumNameLength = 128;

/** The names of skills, lowercase, as `skillMentionSpans` and `dressSkillMentions` compare them. */
export function skillNames(skills: readonly SkillChoice[] | null | undefined): ReadonlySet<string> {
  return new Set((skills ?? []).map(skill => skill.name.toLowerCase()));
}

/**
 * Where a plain text names one of the skills `known` (lowercase names), in order. The text is not read as Markdown: it is
 * a text of a rendered message, or a prompt before the host says which of these are outside code (`SkillChoices.mentions`).
 */
export function skillMentionSpans(text: string, known: ReadonlySet<string>): SkillMentionSpan[] {
  if (known.size === 0 || !text.includes("/")) return [];
  const spans: SkillMentionSpan[] = [];
  for (const match of text.matchAll(mention)) {
    const name = match[1];
    if (name.length <= maximumNameLength && known.has(name.toLowerCase())) spans.push({ start: match.index, end: match.index + 1 + name.length, name });
  }
  return spans;
}

/**
 * Whether the `/` typed at `at` may open the skill picker: the host says so (`SkillChoices.canBegin`), and is not asked
 * for a text that has nothing of Markdown before the `/` that could make it code, HTML or a link.
 */
export function skillTokenAllowed(choices: SkillChoices | null, text: string, at: number): boolean | Promise<boolean> {
  if (!choices) return false;
  return /[`~<\\\t]| {4}|\]\($/u.test(text.slice(0, at)) ? choices.canBegin(text, at) : true;
}

/**
 * The `/token` at the caret that opens the skill picker: `/` at the start of the prompt or after white space or an
 * opening bracket, followed by the query typed so far. `src/app` and `//` are ordinary text. A `/` in an empty prompt
 * never gets here: it opens the search of the window.
 */
export function activeSkillReference(text: string, caret: number) {
  const match = /(?:^|[\s(\[{])\/([^\s/()\[\]{}]*)$/u.exec(text.slice(0, caret));
  if (!match || match[1].length > maximumNameLength || text[caret] === "/") return null;
  // A `/` typed before a word starts a token of its own: the word stays.
  let end = caret;
  while (match[1] && end < text.length && !/[\s/,;!?()\[\]{}"']/u.test(text[end])) end++;
  // The punctuation that ends a sentence is not of the token (`/tdd.`): a skill written in its place keeps it.
  while (end > caret && /[._:-]/u.test(text[end - 1])) end--;
  return { start: caret - match[1].length - 1, end, query: match[1] };
}

/**
 * Writes back in place of the `/token` from `start` to `end` what was typed in the picker after the `/` (`typed`): the
 * picker closed without a skill, or the text is not one (a path, words). The caret goes after it.
 */
export function typedSkillQuery(text: string, start: number, end: number, typed: string) {
  if (start < 0 || end < start || end > text.length) return null;
  const value = text.slice(0, start) + "/" + typed + text.slice(end);
  return value.length <= 32768 ? { text: value, caret: start + 1 + typed.length } : null;
}

/**
 * Writes `/name` in place of the text from `start` to `end`, with what goes with the skill (`args`): separated by a space
 * from the word before it, and followed by a space where the user goes on typing, unless punctuation follows it.
 */
export function insertSkillCall(text: string, start: number, end: number, name: string, args = "") {
  if (!name || /\s/u.test(name) || start < 0 || end < start || end > text.length) return null;
  const before = start > 0 && !/[\s(\[{]/u.test(text[start - 1]) ? " " : "";
  const call = `${before}/${name}${args.trim() ? ` ${args.trim()}` : ""}`;
  // No space before the punctuation that follows the call (`/tdd.`): it ends the name as white space does.
  const closed = text[end] !== undefined && /[.,;:!?)\]}"']/u.test(text[end]);
  const after = closed || text[end] !== undefined && /\s/u.test(text[end]) ? "" : " ";
  const value = text.slice(0, start) + call + after + text.slice(end);
  // The caret goes after the space that follows the call, the one it adds or the one that was there; else after the call.
  return value.length <= 32768 ? { text: value, caret: start + call.length + (closed ? 0 : 1) } : null;
}

/**
 * What a prompt becomes when a skill chosen in the search of the window (`/name` and what goes with it, `call`) is written
 * at its caret: the text and where the caret goes, or null for a call that is not one. Without the editor (`input`), or
 * when it holds another text, the call goes at the end.
 */
export function skillCallAtCaret(input: PromptInput | null, current: string, call: string) {
  const parts = /^\/(\S+)\s*([\s\S]*)$/u.exec(call);
  const caret = input?.isConnected && input.value === current ? Math.min(input.selectionEnd, current.length) : current.length;
  return parts && insertSkillCall(current, caret, caret, parts[1], parts[2]);
}

/** Puts the caret of the prompt at `caret` once its editor has taken the new text, where the user goes on typing. */
export function caretAfterEdit(input: RefObject<PromptInput | null>, caret: number) {
  requestAnimationFrame(() => {
    const element = input.current;
    if (!element?.isConnected || element.disabled) return;
    element.focus(); element.setSelectionRange(caret, caret);
  });
}

const mentionAttribute = "data-skill-mention";

/**
 * Marks in a rendered message the `/name` of the skills `known` (lowercase names): a span the style shows as a skill.
 * Code and links keep their text. Called again, it first removes the marks it left.
 */
export function dressSkillMentions(root: HTMLElement, known: ReadonlySet<string>) {
  for (const marked of Array.from(root.querySelectorAll(`span[${mentionAttribute}]`))) marked.replaceWith(...Array.from(marked.childNodes));
  root.normalize();
  if (known.size === 0) return;
  const walker = root.ownerDocument.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
    acceptNode: node => node.parentElement?.closest("code, pre, a, button") ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT,
  });
  const texts: Text[] = [];
  for (let node = walker.nextNode(); node; node = walker.nextNode()) texts.push(node as Text);
  for (const node of texts) {
    const spans = skillMentionSpans(node.data, known);
    // From the last one: splitting a text node keeps the offsets of what comes before.
    for (const span of spans.reverse()) {
      const rest = node.splitText(span.start);
      rest.splitText(span.end - span.start);
      const mark = root.ownerDocument.createElement("span");
      mark.setAttribute(mentionAttribute, span.name);
      mark.className = "skill-mention";
      rest.replaceWith(mark);
      mark.append(rest);
    }
  }
}
