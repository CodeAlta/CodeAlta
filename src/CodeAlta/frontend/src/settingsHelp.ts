import { createContext, useContext, useEffect, useId } from "react";

/**
 * What a help button of Settings needs from the window: to open a page of the user guide (in the place of Settings),
 * and whether a page of Settings holds edits that are not saved, which closing Settings would lose.
 */
export type SettingsHelp = Readonly<{
  openGuide(page: string, anchor: string | null): void;
  /** Whether a page of Settings says it has edits that are not saved. */
  unsaved(): boolean;
  /** A page says whether it has edits that are not saved; `false` once they are saved, undone or the page goes. */
  mark(owner: string, unsaved: boolean): void;
}>;

/** Creates the help of the Settings of one window: the pages it shows mark their unsaved edits in it. */
export function createSettingsHelp(openGuide: (page: string, anchor: string | null) => void): SettingsHelp {
  const owners = new Set<string>();
  return Object.freeze({
    openGuide,
    unsaved: () => owners.size > 0,
    mark(owner: string, unsaved: boolean) { if (unsaved) owners.add(owner); else owners.delete(owner); },
  });
}

/** The help of the Settings of the window; null outside Settings (a fixture), where no guide opens. */
export const SettingsHelpContext = createContext<SettingsHelp | null>(null);

/** The help of the Settings the component is shown in, or null. */
export function useSettingsHelp(): SettingsHelp | null { return useContext(SettingsHelpContext); }

/** Says while it is true that the page has edits that are not saved, so that a help button warns before they are lost. */
export function useUnsavedSettings(unsaved: boolean) {
  const help = useSettingsHelp();
  const owner = useId();
  useEffect(() => {
    help?.mark(owner, unsaved);
    return () => help?.mark(owner, false);
  }, [help, owner, unsaved]);
}
