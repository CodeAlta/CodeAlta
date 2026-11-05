import type { IssueBranchSchemeRow, IssueRow } from "#neoastra";
import type { MessageKey } from "../localization";

// The branch of a new worktree started on an issue: CodeAlta proposes one from the scheme of the project (the [branch]
// table of .alta/issues.toml and .alta/local/issues.toml, read by the host), such as `feature/12345-export-the-list`, and
// the user changes it in the field before the start.

/** How a branch is named: the template, the longest `{title}`, and what `{type}` is for each type of item. */
export type BranchScheme = Pick<IssueBranchSchemeRow, "template" | "titleLength" | "types">;

/** The scheme when the project says none, as the host has it. */
export const defaultBranchScheme: BranchScheme = {
  template: "{type}/{id}-{title}",
  titleLength: 40,
  types: [{ name: "Bug", value: "fix" }, { name: "Bugfix", value: "fix" }, { name: "Defect", value: "fix" }, { name: "Fix", value: "fix" }, { name: "*", value: "feature" }],
};

const spelledOut: Readonly<Record<string, string>> = { "ä": "ae", "ö": "oe", "ü": "ue", "ß": "ss", "Ä": "ae", "Ö": "oe", "Ü": "ue" };

/** The words of a title for a branch: lower case, ASCII letters and digits, joined by `-`, cut at a whole word. */
export function branchSlug(title: string, length = defaultBranchScheme.titleLength): string {
  const plain = title.replace(/[äöüßÄÖÜ]/g, letter => spelledOut[letter] ?? letter).normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase();
  const words = plain.split(/[^a-z0-9]+/).filter(word => word.length > 0);
  let slug = "";
  for (const word of words) {
    const next = slug ? `${slug}-${word}` : word;
    if (next.length > length) break;
    slug = next;
  }
  return slug || (words[0]?.slice(0, length) ?? "");
}

/**
 * What `{type}` is for an item: the first type of the scheme that is its type, else one of its labels (GitHub has
 * labels where Azure DevOps has types), else the one of `*`, else `feature`. Names are compared without case.
 */
export function branchType(item: Pick<IssueRow, "type" | "labels">, types: BranchScheme["types"] = defaultBranchScheme.types): string {
  const same = (a: string | null | undefined, b: string) => !!a && a.trim().toLowerCase() === b.trim().toLowerCase();
  return types.find(type => same(item.type, type.name))?.value
    ?? types.find(type => item.labels.some(label => same(label.name, type.name)))?.value
    ?? types.find(type => type.name === "*")?.value
    ?? "feature";
}

/**
 * The branch CodeAlta proposes for a new worktree on an issue: the template of the scheme with `{type}`, `{id}` and
 * `{title}` filled. A placeholder the template should not hold stays as it is written, and the field shows it.
 * What an empty part leaves behind (`feature/12345-` for a title of no words) is taken away.
 */
export function suggestBranchName(item: Pick<IssueRow, "id" | "title" | "type" | "labels">, scheme: BranchScheme = defaultBranchScheme): string {
  const values: Readonly<Record<string, string>> = {
    type: branchType(item, scheme.types),
    // A number (GitHub, Azure DevOps) or a key (Jira's PROJ-12) stays as the tracker writes it.
    id: item.id.replace(/[^A-Za-z0-9-]+/g, ""),
    title: branchSlug(item.title, scheme.titleLength),
  };
  const filled = scheme.template.replace(/\{([^{}]*)\}/g, (written, name: string) => values[name] ?? written);
  const name = filled.split("/").map(part => part.replace(/^[-_.]+|[-_]+$/g, "")).filter(part => part.length > 0).join("/");
  return name || `${values.type || "work"}/${values.id || "work"}`;
}

/** What is wrong in a scheme, as a text of the window and the values it names. */
export type SchemeProblem = Readonly<{ key: MessageKey; values?: Readonly<Record<string, string>> }>;

/** The most types a scheme holds, the row of all others included, as the host has it. */
export const maximumTypes = 64;

/** The longest title a scheme can ask for, as the host has it. */
export const maximumTitleLength = 120;

/** What is wrong in a template: braces that do not close, or a placeholder other than `{type}`, `{id}` and `{title}`; null when nothing is. */
export function templateProblem(template: string): SchemeProblem | null {
  if (template.trim().length === 0) return { key: "The template is empty." };
  if (template.length > 200) return { key: "The template is longer than 200 characters." };
  let index = 0;
  while (index < template.length) {
    const character = template[index];
    if (character === "}") return { key: "A brace of the template is not opened." };
    if (character === "{") {
      const end = template.indexOf("}", index + 1);
      if (end < 0) return { key: "A brace of the template is not closed." };
      const name = template.slice(index + 1, end);
      if (name !== "type" && name !== "id" && name !== "title") return { key: "The template has an unknown placeholder: {name}. It can hold type, id and title.", values: { name: `{${name}}` } };
      index = end;
    }
    index++;
  }
  return null;
}

/** Whether a text can be what `{type}` stands for: letters, digits, `- _ . /`, nothing that starts a part with a dot; empty for a template that does without it. */
export function isTypeText(text: string): boolean {
  return text.length <= 64 && /^[A-Za-z0-9._/-]*$/.test(text) && !text.includes("..") && !text.startsWith(".") && !text.startsWith("/") && !text.endsWith("/");
}

/** What keeps a scheme from being saved, as the host checks it again; null when it can be. */
export function branchSchemeProblem(scheme: BranchScheme): SchemeProblem | null {
  const template = templateProblem(scheme.template);
  if (template) return template;
  if (!Number.isInteger(scheme.titleLength) || scheme.titleLength < 1 || scheme.titleLength > maximumTitleLength) return { key: "The longest title is a number from 1 to 120." };
  // The same limits as the host, the row of all others included.
  if (scheme.types.length > maximumTypes) return { key: "There are more than 64 types." };
  const names = new Set<string>();
  for (const type of scheme.types) {
    const name = type.name.trim();
    if (name.length === 0) return { key: "A type has no name." };
    if (type.name.length > 64 || /[\u0000-\u001f\u007f-\u009f]/.test(type.name)) return { key: "The name of a type is longer than 64 characters, or holds a control character." };
    if (names.has(name.toLowerCase())) return { key: "The type {type} is there twice.", values: { type: name } };
    names.add(name.toLowerCase());
    if (!isTypeText(type.value.trim())) return { key: "The text for {type} is not a part of a branch name.", values: { type: name === "*" ? "*" : name } };
  }
  return null;
}

/**
 * Whether a text can be the name of a new branch, by the rules of git that the host checks again: no space, none of
 * `~ ^ : ? * [ \`, no `..`, `@{` or `//`, no part that starts with `.` or ends with `.lock`, nothing at either end that
 * git refuses. Braces are refused as well: in a proposed name they are a placeholder the template should not hold.
 */
export function isBranchName(text: string): boolean {
  if (text.length === 0 || text.length > 200 || text === "@" || text === "HEAD" || text.startsWith("-") || text.startsWith("refs/")) return false;
  if (/[\u0000- \u007f~^:?*[\\{}]/.test(text) || /\.\.|\/\//.test(text)) return false;
  return text.split("/").every(part => part.length > 0 && !part.startsWith(".") && !part.endsWith(".lock") && !part.endsWith("."));
}

const ownBranchStorage = "codealta.desktop.issue-own-branch.v1";

/**
 * Whether the work on an issue of a project starts on a branch of its own, as the user chose it the last time; null when
 * the user never chose, and the project decides (on when it has a scheme of its own).
 */
export function readOwnBranch(projectId: string, storage: Pick<Storage, "getItem"> | null = globalThis.localStorage ?? null): boolean | null {
  try {
    const all = JSON.parse(storage?.getItem(ownBranchStorage) ?? "{}") as unknown;
    const found = all && typeof all === "object" ? (all as Record<string, unknown>)[projectId] : null;
    return typeof found === "boolean" ? found : null;
  } catch {
    return null;
  }
}

/** Keeps the choice of a branch of its own for the next issues of a project. */
export function writeOwnBranch(projectId: string, value: boolean, storage: Pick<Storage, "getItem" | "setItem"> | null = globalThis.localStorage ?? null): void {
  try {
    const parsed = JSON.parse(storage?.getItem(ownBranchStorage) ?? "{}") as unknown;
    const all: Record<string, unknown> = parsed && typeof parsed === "object" ? { ...(parsed as Record<string, unknown>) } : {};
    all[projectId] = value;
    storage?.setItem(ownBranchStorage, JSON.stringify(all));
  } catch { /* The choice lasts as long as the window. */ }
}
