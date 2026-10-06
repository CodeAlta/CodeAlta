import { useLayoutEffect, useMemo, useSyncExternalStore } from "react";
import { Classes } from "@blueprintjs/core";
import { boot } from "#neoastra";
import type { AppearancePreviewStore } from "./appearancePreview";
import { schemePalette, showAppearance, type ShownAppearance } from "./colorSchemes";
import { rememberAppearance } from "./startupScreen";

const noPreview = () => null;
const neverChanges = () => () => { /* Nothing to stop listening to. */ };

/**
 * Shows the window's appearance on the document: the selected one or, while a scheme editor is open, the
 * scheme it edits. The appearance is also kept for the next start, which shows its colors before the
 * application has loaded, and given to the host, which draws the window controls for its theme.
 */
export function ShellAppearance({ appearance, preview }: { appearance: ShownAppearance; preview?: AppearancePreviewStore }) {
  const previewed = useSyncExternalStore(preview?.subscribe ?? neverChanges, preview?.get ?? noPreview);
  const shown: ShownAppearance = useMemo(() => previewed
    ? { theme: previewed.variant === "light" ? "light" : "dark", scheme: appearance.scheme, palette: schemePalette(previewed.scheme, previewed.variant) }
    : appearance, [appearance, previewed]);
  useLayoutEffect(() => {
    showAppearance(document.documentElement, Classes.DARK, shown);
    const keep = () => rememberAppearance(shown.theme, remembered => void boot.appearance({ theme: remembered.theme, background: remembered.background },
      { timeoutMilliseconds: 8_000 }).catch(() => { /* The window keeps the colors it started with. */ }));
    if (!previewed) { keep(); return; }
    // A color that is being chosen moves many times a second: it is kept once it has settled.
    const timer = window.setTimeout(keep, 300);
    return () => window.clearTimeout(timer);
  }, [shown]);
  return null;
}
