import { useRef, type ReactNode } from "react";

/**
 * Wait before mounting an editor, but never destroy an admitted editor on transport loss: its latest draft
 * may exist only in memory after a failed storage write. The pane makes its existing root inert while its
 * claim is unknown; programmatic admissions check the live owner, not this remembered presentation state.
 */
export function SessionRetentionGate({ available, fallback, children }: { available: boolean; fallback: ReactNode; children: ReactNode }) {
  const admitted = useRef(false);
  if (available) admitted.current = true;
  return admitted.current ? children : fallback;
}
