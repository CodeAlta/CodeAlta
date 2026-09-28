import type { MouseEvent } from "react";

// Padding is inside the dialog, not its backdrop. Content clicks must not dismiss it.
export function isDialogBackdrop(event: MouseEvent<HTMLDialogElement>): boolean {
  if (event.target !== event.currentTarget) return false;
  const bounds = event.currentTarget.getBoundingClientRect();
  return event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom;
}
