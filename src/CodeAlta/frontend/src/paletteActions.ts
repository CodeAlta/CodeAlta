function restorePaletteFocus(origin: HTMLElement | null, sameView: boolean, modalOpen: boolean): boolean {
  if (!sameView || modalOpen || !origin?.isConnected) return false;
  origin.focus();
  return true;
}

// One owner per mounted window: a deferred modal close may not override a newer
// focus move, another modal, or a navigation made before the next animation frame.
export function createPaletteFocusRestoration() {
  let generation = 0;
  let frame: number | null = null;
  function cancel() {
    generation++;
    if (frame !== null) cancelAnimationFrame(frame);
    frame = null;
  }
  return {
    cancel,
    schedule(origin: HTMLElement | null, sameView: () => boolean, modalOpen: () => boolean) {
      cancel();
      const current = generation;
      const focusedAtClose = document.activeElement;
      frame = requestAnimationFrame(() => {
        frame = null;
        if (current !== generation) return;
        const focused = document.activeElement;
        if (focused !== focusedAtClose && focused !== document.body && focused !== document.documentElement &&
          focused instanceof HTMLElement && focused.isConnected && focused !== origin) return;
        restorePaletteFocus(origin, sameView(), modalOpen());
      });
    },
  };
}
