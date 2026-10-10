import { useEffect, useRef, useState } from "react";
import { paintPixelField, pixelFieldMoves, pixelFrameMilliseconds, pixelGrid, pixelStillTime } from "./pixelField";

/** Whether the system asks for less motion; it follows the setting while the page is open. */
function useReducedMotion(): boolean {
  const [reduced, setReduced] = useState(() => typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches);
  useEffect(() => {
    if (typeof matchMedia !== "function") return;
    const media = matchMedia("(prefers-reduced-motion: reduce)");
    const change = () => setReduced(media.matches);
    change();
    media.addEventListener("change", change);
    return () => media.removeEventListener("change", change);
  }, []);
  return reduced;
}

/**
 * The accent of the hero: a field of pixels in the colors of the Alta logo, on a canvas that has one pixel for each cell and is
 * stretched without smoothing. It draws about nine frames a second, each of a few thousand pixels at most, and only while it moves:
 * when the user turned the animation off, asked the system for less motion, or the page is not on the screen, one still frame is
 * drawn and no timer runs. Nothing of React is drawn again for a frame.
 */
export function LandingAccent({ animate, visible, dark }: {
  /** The user wants the field to move. */
  animate: boolean;
  /** The tab of the page is in front. */
  visible: boolean;
  /** The theme is dark: the field is then a little stronger. */
  dark: boolean;
}) {
  const canvas = useRef<HTMLCanvasElement | null>(null);
  const reducedMotion = useReducedMotion();
  // What the loop reads at each frame, without starting again.
  const [size, setSize] = useState<Readonly<{ columns: number; rows: number }>>({ columns: 0, rows: 0 });
  const [documentHidden, setDocumentHidden] = useState(() => typeof document !== "undefined" && document.hidden);
  const [onScreen, setOnScreen] = useState(true);

  useEffect(() => {
    const change = () => setDocumentHidden(document.hidden);
    document.addEventListener("visibilitychange", change);
    return () => document.removeEventListener("visibilitychange", change);
  }, []);

  // The field has the cells its area holds, and is on the screen while the hero is not scrolled away.
  useEffect(() => {
    const element = canvas.current;
    if (!element) return;
    const measure = () => {
      const next = pixelGrid(element.clientWidth, element.clientHeight);
      setSize(current => current.columns === next.columns && current.rows === next.rows ? current : next);
    };
    measure();
    const resize = typeof ResizeObserver === "function" ? new ResizeObserver(measure) : null;
    resize?.observe(element);
    const seen = typeof IntersectionObserver === "function" ? new IntersectionObserver(entries => setOnScreen(entries.some(entry => entry.isIntersecting))) : null;
    seen?.observe(element);
    return () => { resize?.disconnect(); seen?.disconnect(); };
  }, []);

  const moves = pixelFieldMoves({ animate, reducedMotion, visible, documentHidden, onScreen });
  // Where the drift is: kept from one run of the loop to the next, so that the field goes on from where it stopped.
  const clock = useRef(pixelStillTime);
  useEffect(() => {
    const element = canvas.current;
    const { columns, rows } = size;
    if (!element || columns === 0 || rows === 0) return;
    element.width = columns;
    element.height = rows;
    const context = element.getContext("2d");
    if (!context) return;
    const image = context.createImageData(columns, rows);
    const strength = dark ? 0.5 : 0.34;
    const draw = () => { paintPixelField(image.data, columns, rows, clock.current, strength); context.putImageData(image, 0, 0); };
    draw();
    if (!moves) return;
    // A timer wakes the field for each frame, and the frame is painted when the window paints: nothing runs between two frames.
    let frame = 0, timer = 0, last = performance.now();
    const wait = () => { timer = window.setTimeout(() => { frame = requestAnimationFrame(step); }, pixelFrameMilliseconds); };
    const step = (now: number) => {
      // A long pause (the window was behind another one) does not make the field jump.
      clock.current += Math.min(Math.max(0, now - last), 4 * pixelFrameMilliseconds) / 1000;
      last = now;
      draw();
      wait();
    };
    wait();
    return () => { window.clearTimeout(timer); cancelAnimationFrame(frame); };
  }, [size, moves, dark]);

  return <canvas ref={canvas} className="landing-pixels" aria-hidden="true" data-moving={moves} />;
}
