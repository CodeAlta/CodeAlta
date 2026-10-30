import { el } from "./dom";

// The icons of the cards of the timeline, drawn with the stroke of lucide as those of dom.ts: constant markup of the page,
// never text of a session.
const icons = {
  terminal: '<path d="M4 17l6-5-6-5M12 19h8"/>',
  cog: '<path d="m9 3-1 3-3 1-2 5 2 5 3 1 1 3h6l1-3 3-1 2-5-2-5-3-1-1-3z"/><circle cx="12" cy="12" r="3"/>',
  code: '<path d="M16 18l6-6-6-6M8 6l-6 6 6 6"/>',
  eye: '<path d="M2 12s3.6-7 10-7 10 7 10 7-3.6 7-10 7S2 12 2 12z"/><circle cx="12" cy="12" r="3"/>',
  timer: '<circle cx="12" cy="13" r="8"/><path d="M12 9v4l2 2M10 2h4"/>',
  branch: '<path d="M6 3v12"/><circle cx="18" cy="6" r="3"/><circle cx="6" cy="18" r="3"/><path d="M18 9a9 9 0 0 1-9 9"/>',
  zap: '<path d="M13 2L3 14h9l-1 8 10-12h-9z"/>',
  model: '<rect x="4" y="4" width="16" height="16" rx="2"/><rect x="9" y="9" width="6" height="6"/><path d="M9 1v3M15 1v3M9 20v3M15 20v3M20 9h3M20 14h3M1 9h3M1 14h3"/>',
  plugin: '<path d="M12 22v-5M9 8V2M15 8V2M18 8v5a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4V8z"/>',
  info: '<circle cx="12" cy="12" r="10"/><path d="M12 16v-4M12 8h.01"/>',
  ask: '<path d="M7.9 20A9 9 0 1 0 4 16.1L2 22z"/><path d="M9.1 9a3 3 0 0 1 5.8 1c0 2-3 3-3 3M12 17h.01"/>',
  image: '<rect x="3" y="3" width="18" height="18" rx="2"/><circle cx="9" cy="9" r="2"/><path d="M21 15l-3.1-3.1a2 2 0 0 0-2.8 0L6 21"/>',
  check: '<path d="M5 12l5 5 9-10"/>',
  close: '<path d="M6 6l12 12M18 6L6 18"/>',
  stop: '<rect x="6" y="6" width="12" height="12" rx="1"/>',
  spinner: '<path d="M12 3a9 9 0 1 1-9 9"/>',
  chevron: '<path d="M9 6l6 6-6 6"/>',
  open: '<path d="M7 17L17 7M8 7h9v9"/>',
  pause: '<circle cx="12" cy="12" r="10"/><path d="M10 15V9M14 15V9"/>',
  circle: '<circle cx="12" cy="12" r="8"/>',
  bot: '<path d="M12 8V4H8"/><rect width="16" height="12" x="4" y="8" rx="2"/><path d="M2 14h2M20 14h2M15 13v2M9 13v2"/>',
  left: '<path d="M15 18l-6-6 6-6"/>',
  right: '<path d="M9 18l6-6-6-6"/>',
} as const;

/** An icon of the cards of the timeline. */
export type TimelineIconName = keyof typeof icons;

/** An icon in a span, hidden from screen readers. */
export function timelineIcon(document: Document, name: TimelineIconName, className?: string): HTMLSpanElement {
  const span = el(document, "span", `ico${className ? ` ${className}` : ""}`);
  span.setAttribute("aria-hidden", "true");
  span.innerHTML = `<svg viewBox="0 0 24 24"${name === "spinner" ? ' class="spinner"' : ""}>${icons[name]}</svg>`;
  return span;
}
