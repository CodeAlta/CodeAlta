import type { CSSProperties } from "react";

const dots = [0, 1, 2, 3, 4, 5, 6, 7];

/** Dot-matrix activity indicator: eight dots around a 3×3 grid with a rotating trail, like the TUI's dots spinner. */
export function ActivitySpinner({ size = 14, label, className }: { size?: number; label?: string; className?: string }) {
  return <span className={`activity-spinner${className ? ` ${className}` : ""}`} style={{ "--activity-size": `${size}px` } as CSSProperties}
    role={label ? "img" : undefined} aria-label={label} aria-hidden={label ? undefined : true} title={label}>
    {dots.map(dot => <i key={dot} />)}
  </span>;
}
