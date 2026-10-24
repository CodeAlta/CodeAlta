import { activityDuration } from "../timelineActivity/activityText";
import type { ActivityFacts } from "./contract";

/** Makes an element, with its class and its text. */
export function el<K extends keyof HTMLElementTagNameMap>(document: Document, tag: K, className?: string | null, text?: string | number | null): HTMLElementTagNameMap[K] {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== undefined && text !== null) element.textContent = String(text);
  return element;
}

// The icons of the page, drawn with the stroke of lucide (as the desktop's): constant markup of the page, never text of a session.
const icons = {
  back: '<path d="M15 18l-6-6 6-6"/>',
  chevron: '<path d="M9 6l6 6-6 6"/>',
  check: '<path d="M5 12l5 5 9-10"/>',
  archive: '<rect x="2" y="3" width="20" height="5" rx="1"/><path d="M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8M10 12h4"/>',
  warning: '<path d="M12 3l10 18H2z"/><path d="M12 10v5M12 18v.5"/>',
  spinner: '<path d="M12 3a9 9 0 1 1-9 9"/>',
  brain: '<path d="M9 4a3 3 0 0 0-3 3 3 3 0 0 0-2 5 3 3 0 0 0 2 5 3 3 0 0 0 6 1V5a3 3 0 0 0-3-1zM15 4a3 3 0 0 1 3 3 3 3 0 0 1 2 5 3 3 0 0 1-2 5 3 3 0 0 1-6 1"/>',
  tool: '<path d="M14 6a4 4 0 0 0 5 5l-8 8a2 2 0 0 1-3-3l8-8a4 4 0 0 1-2-2z"/>',
  external: '<path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>',
  close: '<path d="M6 6l12 12M18 6L6 18"/>',
  timer: '<circle cx="12" cy="13" r="8"/><path d="M12 9v4l2 2M10 2h4"/>',
  send: '<path d="M22 2L11 13M22 2l-7 20-4-9-9-4z"/>',
  steer: '<path d="M15 14l5-5-5-5"/><path d="M4 20v-7a4 4 0 0 1 4-4h12"/>',
  sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
  moon: '<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>',
} as const;
export type IconName = keyof typeof icons;

/** An icon in a span, hidden from screen readers. */
export function icon(document: Document, name: IconName, className?: string): HTMLSpanElement {
  const span = el(document, "span", `ico${className ? ` ${className}` : ""}`);
  span.setAttribute("aria-hidden", "true");
  span.innerHTML = `<svg viewBox="0 0 24 24"${name === "spinner" ? ' class="spinner"' : ""}>${icons[name]}</svg>`;
  return span;
}

/** The SVG of an icon alone, for a button that has no span around it. */
export const iconSvg = (name: IconName): string => `<svg viewBox="0 0 24 24" aria-hidden="true">${icons[name]}</svg>`;

/**
 * The line of an activity, as the desktop's TimelineActivity draws it: its mark (spinner, check or warning), its
 * headline (or "Calling tools…" and the names of the tools) and its facts (thoughts, tools, successes, failures, time).
 */
export function activityLine(document: Document, facts: ActivityFacts, live: boolean, now: number): HTMLDivElement {
  const state = live ? "running" : facts.failed > 0 ? "failed" : "done";
  const line = el(document, "div", "act-line");
  line.dataset.state = state;
  line.append(icon(document, live ? "spinner" : facts.failed > 0 ? "warning" : "check", "act-mark"));
  const headline = el(document, "span", "act-headline", facts.headline);
  if (facts.names) headline.append(el(document, "small", null, facts.names));
  line.append(headline);
  const all = el(document, "span", "act-facts");
  const fact = (name: IconName, value: string | number, label: string, className?: string) => {
    const item = el(document, "span", className);
    item.title = label;
    item.append(icon(document, name), document.createTextNode(String(value)));
    all.append(item);
  };
  if (facts.thoughts > 0) fact("brain", facts.thoughts, "Thoughts");
  if (facts.tools > 0) fact("tool", facts.tools, "Tool calls");
  if (live && facts.succeeded > 0) fact("check", facts.succeeded, "Succeeded", "ok");
  if (facts.failed > 0) fact("close", facts.failed, "Failed", "bad");
  const start = Date.parse(facts.startedAt ?? ""), end = Date.parse(facts.endedAt ?? "");
  const duration = Number.isFinite(start) ? activityDuration((live ? Math.max(now, Number.isFinite(end) ? end : 0) : end) - start) : null;
  if (duration) fact("timer", duration, "Duration", "dur");
  line.append(all);
  return line;
}
