/** The part of a pointer press that a splitter reads. */
export type SplitterPress = Readonly<{
  isPrimary: boolean; button: number; ctrlKey: boolean; metaKey: boolean; pointerId: number;
  currentTarget: Pick<Element, "setPointerCapture">;
  preventDefault(): void;
}>;

/**
 * Starts the drag of a splitter on a press of the main button, and returns whether it did; any other press (a second
 * finger, another button, Ctrl+click that opens a menu on macOS) is left alone. The press's default is prevented:
 * without it WebKit (macOS) starts a text selection that follows the pointer into the panes on either side, even while
 * the splitter holds the pointer, as Chromium does not. The focus also stays where it was.
 */
export function startSplitterDrag(event: SplitterPress): boolean {
  if (!event.isPrimary || event.button !== 0 || event.ctrlKey || event.metaKey) return false;
  event.preventDefault();
  // Without the capture (a pointer that is gone already) the drag still follows what reaches the splitter.
  try { event.currentTarget.setPointerCapture(event.pointerId); } catch { /* The pointer cannot be captured. */ }
  return true;
}
