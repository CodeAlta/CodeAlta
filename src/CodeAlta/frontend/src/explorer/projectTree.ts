/** What the Explorer remembers between starts: the projects left open and the favorite ones. */
export type ProjectTree = Readonly<{
  /** The open scopes: project ids, and `globalScope` for the global sessions. */
  expanded: readonly string[];
  /** The favorite projects, by id, in the order they were added. */
  favorites: readonly string[];
}>;

export const emptyProjectTree: ProjectTree = Object.freeze({ expanded: [], favorites: [] });
export const projectTreeKey = "codealta.desktop.projectTree.v1";
/** The name of the global sessions among the open scopes; no project has an empty id. */
export const globalScope = "";
/** Most ids kept in each list, and the longest id kept. */
export const projectTreeLimit = 512;
const longestId = 128;

/** The name of a scope among the open ones. */
export const scopeKey = (projectId: string | null): string => projectId ?? globalScope;

function ids(value: unknown, global: boolean): string[] {
  if (!Array.isArray(value)) return [];
  const kept = new Set<string>();
  for (const item of value) {
    if (typeof item !== "string" || item.length > longestId || !item && !global || kept.size >= projectTreeLimit) continue;
    kept.add(item);
  }
  return [...kept];
}

/** The stored tree, or null when nothing is stored or it cannot be read: the selected project is then the one open. */
export function restoreProjectTree(read: () => string | null): ProjectTree | null {
  try {
    const raw = read();
    if (raw === null) return null;
    const value = JSON.parse(raw) as unknown;
    if (!value || typeof value !== "object" || Array.isArray(value)) return null;
    const stored = value as Record<string, unknown>;
    return { expanded: ids(stored.expanded, true), favorites: ids(stored.favorites, false) };
  } catch { return null; }
}

export function persistProjectTree(write: (value: string) => void, tree: ProjectTree): boolean {
  try { write(JSON.stringify({ expanded: tree.expanded, favorites: tree.favorites })); return true; }
  catch { return false; }
}

// The newest ids are kept when a list is full.
const added = (list: readonly string[], id: string) => [...list, id].slice(-projectTreeLimit);

export const isExpanded = (tree: ProjectTree, projectId: string | null): boolean => tree.expanded.includes(scopeKey(projectId));

/** Opens a scope; the same tree is returned when it is open already. */
export function expandScope(tree: ProjectTree, projectId: string | null): ProjectTree {
  return isExpanded(tree, projectId) ? tree : { ...tree, expanded: added(tree.expanded, scopeKey(projectId)) };
}

/** Opens a closed scope and closes an open one. */
export function toggleScope(tree: ProjectTree, projectId: string | null): ProjectTree {
  const key = scopeKey(projectId);
  return isExpanded(tree, projectId) ? { ...tree, expanded: tree.expanded.filter(value => value !== key) } : expandScope(tree, projectId);
}

/** Closes every scope; the same tree is returned when none is open. */
export function collapseAllScopes(tree: ProjectTree): ProjectTree {
  return tree.expanded.length ? { ...tree, expanded: [] } : tree;
}

export const isFavorite = (tree: ProjectTree, projectId: string): boolean => tree.favorites.includes(projectId);

/** Makes a project a favorite, or no longer one; the same tree is returned when nothing changes. */
export function setFavorite(tree: ProjectTree, projectId: string, favorite: boolean): ProjectTree {
  if (!projectId || projectId.length > longestId || isFavorite(tree, projectId) === favorite) return tree;
  return { ...tree, favorites: favorite ? added(tree.favorites, projectId) : tree.favorites.filter(value => value !== projectId) };
}
