import type { ColorVariant, CustomColorScheme } from "./colorSchemes";

/** What a scheme editor shows in place of the window's own appearance while it edits: an unsaved scheme, in one of its themes. */
export type AppearancePreview = Readonly<{ scheme: CustomColorScheme; variant: ColorVariant }>;

/**
 * Holds the appearance a scheme editor is showing. A color moves many times a second while it is chosen:
 * the editor writes here, and only what shows the appearance listens, instead of the whole window rendering
 * again for every move.
 */
export type AppearancePreviewStore = Readonly<{
  get: () => AppearancePreview | null;
  set: (preview: AppearancePreview | null) => void;
  subscribe: (listener: () => void) => () => void;
}>;

export function createAppearancePreview(): AppearancePreviewStore {
  let current: AppearancePreview | null = null;
  const listeners = new Set<() => void>();
  return {
    get: () => current,
    set: preview => {
      if (preview === current) return;
      current = preview;
      for (const listener of [...listeners]) listener();
    },
    subscribe: listener => { listeners.add(listener); return () => { listeners.delete(listener); }; },
  };
}
