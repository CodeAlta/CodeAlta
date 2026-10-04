type Listener = (page: string) => void;
const listeners = new Set<Listener>();

/**
 * Lets a control anywhere in the window (a composer status item, for example) ask the shell to open a
 * Settings page. The shell decides whether the page exists; an unknown page opens nothing.
 */
export const settingsNavigation = Object.freeze({
  open(page: string) { for (const listener of [...listeners]) listener(page); },
  subscribe(listener: Listener) { listeners.add(listener); return () => { listeners.delete(listener); }; },
});
