import type { DocumentationMenuItem, DocumentationPageInfo } from "#neoastra";
import type { IconName } from "../AppIcon";

/** A page of the guide and a heading in it: where the Documentation tab is, or where a link leads. */
export type DocumentationTarget = Readonly<{ page: string; anchor: string | null }>;

const anchorPattern = /^[\p{L}\p{N}_-]{1,200}$/u;

/**
 * The address of a heading, from its text: lower case, a dash for each space, and nothing else but letters, digits,
 * dashes and underscores. It is how the site of the guide names its headings, so a link written for the site
 * (`workspace.md#code-editor`) finds its heading here.
 */
export function headingAnchor(text: string): string {
  return text.trim().toLowerCase().replace(/[^\p{L}\p{N}\-_ ]/gu, "").replace(/ /g, "-");
}

/** The addresses of the headings of a page, in their order: a text that comes again takes `-1`, `-2`, as the site does. */
export function headingAnchors(texts: readonly string[]): string[] {
  const seen = new Map<string, number>();
  return texts.map(text => {
    const anchor = headingAnchor(text), count = seen.get(anchor) ?? 0;
    seen.set(anchor, count + 1);
    return count ? `${anchor}-${count}` : anchor;
  });
}

/** The address of a heading as a link or a request wrote it, when it can name one; null for anything else. */
export function validAnchor(anchor: string | null | undefined): string | null {
  if (!anchor) return null;
  let text = anchor.startsWith("#") ? anchor.slice(1) : anchor;
  try { text = decodeURIComponent(text); } catch { /* Not escapes: the text is as it is written. */ }
  return anchorPattern.test(text) ? text : null;
}

/**
 * The page of the guide a link of a page names. The host writes every link to a page from the folder of the guide
 * (`plugins/git.md#sign-in`), so the target is looked up among the pages, never resolved against a folder: a path
 * that is no page of the guide (a full path, a path with `..`, an address, a file of a project) names nothing.
 */
export function resolveDocumentationLink(address: string, pages: readonly DocumentationPageInfo[]): DocumentationTarget | null {
  const hash = address.indexOf("#");
  let path = hash < 0 ? address : address.slice(0, hash);
  try { path = decodeURIComponent(path); } catch { /* Not escapes: the path is as it is written. */ }
  if (!path || path.length > 200 || path.includes("\\") || path.includes(":") || path.split("/").some(segment => segment === "" || segment === "." || segment === "..")) return null;
  const lower = path.toLowerCase();
  const found = pages.find(page => page.path.toLowerCase() === lower);
  return found ? { page: found.path, anchor: hash < 0 ? null : validAnchor(address.slice(hash + 1)) } : null;
}

/** One entry of the navigation with the entries of its folder. */
export type DocumentationMenuNode = Readonly<{ item: DocumentationMenuItem; children: readonly DocumentationMenuItem[] }>;

/** The navigation as a list of entries, each with the entries of its folder. An entry whose parent is not listed is an entry of its own. */
export function menuNodes(items: readonly DocumentationMenuItem[]): DocumentationMenuNode[] {
  const nodes: { item: DocumentationMenuItem; children: DocumentationMenuItem[] }[] = [];
  for (const item of items) {
    const parent = item.depth > 0 && item.parent ? nodes.find(node => node.item.path === item.parent) : undefined;
    if (parent) parent.children.push(item); else nodes.push({ item, children: [] });
  }
  return nodes;
}

/** The entry of the navigation that holds a page: the page itself, or the folder the page is in. */
export function menuOwner(items: readonly DocumentationMenuItem[], page: string | null): string | null {
  const item = page === null ? undefined : items.find(candidate => candidate.path === page);
  return item ? item.depth > 0 && item.parent ? item.parent : item.path : null;
}

/** The titles that lead to a page in the navigation: the folder it is in, then the page. Empty for a page the navigation does not name. */
export function menuTrail(items: readonly DocumentationMenuItem[], page: string | null): DocumentationMenuItem[] {
  const item = page === null ? undefined : items.find(candidate => candidate.path === page);
  if (!item) return [];
  const parent = item.depth > 0 && item.parent ? items.find(candidate => candidate.path === item.parent) : undefined;
  return parent ? [parent, item] : [item];
}

/** The pages before and after a page, in the order of the navigation. */
export function menuNeighbors(items: readonly DocumentationMenuItem[], page: string | null): Readonly<{ previous: DocumentationMenuItem | null; next: DocumentationMenuItem | null }> {
  const index = page === null ? -1 : items.findIndex(candidate => candidate.path === page);
  return index < 0 ? { previous: null, next: null } : { previous: items[index - 1] ?? null, next: items[index + 1] ?? null };
}

// The menu of the site names its icons after another set: the ones the window has, for the names the guide uses.
const menuIcons: Readonly<Record<string, IconName>> = Object.freeze({
  book: "documentation", "rocket-takeoff": "play", "window-split": "columns", compass: "locate", cpu: "model", "chat-square-text": "chat", display: "themeSystem",
  collection: "space", "diagram-3": "tree", tree: "worktree", "lightning-charge": "automation", "list-check": "task", "record-circle": "issueOpen", github: "branch",
  cursor: "hand", stars: "prompt", puzzle: "plugin", "life-preserver": "ask", gear: "settings", terminal: "terminal", search: "search", "bar-chart": "usage",
});

/** The icon of an entry of the navigation; a name the window has no icon for is shown as a page. */
export function menuIcon(name: string | null | undefined): IconName {
  return name && Object.hasOwn(menuIcons, name) ? menuIcons[name] : "fileText";
}

/** The pages the tab went through, and where it is among them. */
export type DocumentationHistory = Readonly<{ entries: readonly DocumentationTarget[]; index: number }>;
export const emptyHistory: DocumentationHistory = Object.freeze({ entries: [], index: -1 });
const historyLimit = 100;

const sameTarget = (a: DocumentationTarget | undefined, b: DocumentationTarget) => !!a && a.page === b.page && a.anchor === b.anchor;

/** Goes to a place: what was ahead is dropped, and going where the tab already is adds nothing. */
export function pushHistory(history: DocumentationHistory, target: DocumentationTarget): DocumentationHistory {
  if (sameTarget(history.entries[history.index], target)) return history;
  const entries = [...history.entries.slice(0, history.index + 1), target].slice(-historyLimit);
  return { entries, index: entries.length - 1 };
}

/** Moves back (-1) or forward (1); the same history at either end. */
export function moveHistory(history: DocumentationHistory, delta: 1 | -1): DocumentationHistory {
  const index = history.index + delta;
  return index < 0 || index >= history.entries.length ? history : { entries: history.entries, index };
}

/** The parts of a line around a text that was searched for, whatever its case: the text itself is marked. */
export function highlightParts(line: string, query: string): Readonly<{ text: string; match: boolean }>[] {
  const needle = query.trim().toLowerCase();
  if (!needle) return [{ text: line, match: false }];
  const parts: { text: string; match: boolean }[] = [];
  const lower = line.toLowerCase();
  let position = 0;
  // A lower-cased text can differ in length from the text for a few letters: such a line is shown without a mark.
  if (lower.length !== line.length) return [{ text: line, match: false }];
  for (let at = lower.indexOf(needle); at >= 0 && parts.length < 40; at = lower.indexOf(needle, position)) {
    if (at > position) parts.push({ text: line.slice(position, at), match: false });
    parts.push({ text: line.slice(at, at + needle.length), match: true });
    position = at + needle.length;
  }
  if (position < line.length) parts.push({ text: line.slice(position), match: false });
  return parts;
}
