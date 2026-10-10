import { useMemo } from "react";
import { useShellLanguage } from "../shellLanguage";
import type { Translate } from "./labels";

/**
 * The translation of the canvas: `t` keeps its identity while the language does not change, so a chart option built with it is
 * not built again at every render (the shell's own `t` is a new function each time).
 */
export function useText(): Readonly<{ t: Translate; locale: string }> {
  const { t, locale } = useShellLanguage();
  // `t` closes over the locale only; keeping the first one for a locale is the same function.
  return useMemo(() => ({ t: t as Translate, locale }), [locale]); // eslint-disable-line react-hooks/exhaustive-deps
}
