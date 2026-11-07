/** A page of the user guide, and the heading it opens at; null for its top. */
export type DocumentationTarget = Readonly<{ page: string; anchor: string | null }>;

type Listener = (target: DocumentationTarget) => void;
const listeners = new Set<Listener>();

/**
 * Lets a control anywhere in the window (the help button of a page of Settings) ask the shell to open a page of the user
 * guide in its tab, as Settings does with `settingsNavigation`.
 */
export const documentationNavigation = Object.freeze({
  open(page: string, anchor: string | null = null) {
    for (const listener of [...listeners]) listener({ page, anchor });
  },
  subscribe(listener: Listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
});
