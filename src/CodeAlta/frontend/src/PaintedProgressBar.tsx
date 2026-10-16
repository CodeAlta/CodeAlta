import { Classes, type Intent } from "@blueprintjs/core";

/**
 * A progress bar drawn as Blueprint's, whose meter keeps its whole box and is cut to its value (`clip-path`). A
 * toast, a popover and a dialog are regions of the window (`app-region`): a meter whose width changes there moves a
 * region at each frame of its transition, and Windows computes where the window can be dragged again each time
 * (`doc/development-guide.md`). Without a value, the bar is full and striped: the progress is not known.
 */
export function PaintedProgressBar({ value, intent = "primary", stripes = true, animate = true, className, label }: {
  /** From 0 to 1; undefined while the progress is not known. */
  value?: number;
  intent?: Intent; stripes?: boolean; animate?: boolean; className?: string; label?: string;
}) {
  const known = value !== undefined && Number.isFinite(value);
  const percent = known ? Math.max(0, Math.min(1, value!)) * 100 : 100;
  const classes = [Classes.PROGRESS_BAR, Classes.intentClass(intent), "painted-progress", !stripes && Classes.PROGRESS_NO_STRIPES,
    !animate && Classes.PROGRESS_NO_ANIMATION, className].filter(Boolean).join(" ");
  return <div className={classes} role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={known ? Math.round(percent) : undefined} aria-label={label}>
    <div className={Classes.PROGRESS_METER} style={{ clipPath: `inset(0 ${(100 - percent).toFixed(2)}% 0 0 round 40px)` }} />
  </div>;
}
