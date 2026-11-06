import { createContext, useContext, useEffect, useLayoutEffect, useState } from "react";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { compactTokens } from "../contextUsage";
import { useShellLanguage } from "../shellLanguage";
import { cacheCountdown, cacheCountdownTick, ringDash, signInEnd } from "./promptSurface";

/**
 * How much room the prompt surface has, from its own width: `wide` shows every label, `medium` shortens them,
 * `narrow` keeps icons and numbers, `tiny` folds the meters into the `⋯` menu. A split pane is measured as it is.
 */
export type SurfaceDensity = "wide" | "medium" | "narrow" | "tiny";

/** The width under which each density starts, in CSS pixels. */
export const densityWidths = Object.freeze({ medium: 1040, narrow: 780, tiny: 540 });

export function surfaceDensity(width: number): SurfaceDensity {
  return width <= 0 || width >= densityWidths.medium ? "wide" : width >= densityWidths.narrow ? "medium" : width >= densityWidths.tiny ? "narrow" : "tiny";
}

/** The density of the surface the calling control is drawn in. */
export const SurfaceDensityContext = createContext<SurfaceDensity>("wide");
export function useSurfaceDensity() { return useContext(SurfaceDensityContext); }

/** Follows the width of `element` and gives its density. */
export function useMeasuredDensity(element: HTMLElement | null): SurfaceDensity {
  const [density, setDensity] = useState<SurfaceDensity>("wide");
  useLayoutEffect(() => {
    if (!element) return;
    const measure = () => setDensity(surfaceDensity(element.getBoundingClientRect().width));
    measure();
    const sizes = new ResizeObserver(measure);
    sizes.observe(element);
    return () => sizes.disconnect();
  }, [element]);
  return density;
}

/** A ring that fills with `percent`, the context window of the composer. */
export function ContextRing({ percent, size = 16 }: { percent: number | null; size?: number }) {
  const radius = size / 2 - 2;
  return <svg className="context-usage-ring" width={size} height={size} viewBox={`0 0 ${size} ${size}`} aria-hidden="true" focusable="false">
    <circle className="context-usage-ring-track" cx={size / 2} cy={size / 2} r={radius} />
    <circle className="context-usage-ring-value" cx={size / 2} cy={size / 2} r={radius} strokeDasharray={ringDash(percent, radius)}
      transform={`rotate(-90 ${size / 2} ${size / 2})`} />
  </svg>;
}

/**
 * The context meter of a session that has not started: nothing used yet out of the window of the selected model.
 * Without the size of that window there is nothing to say, and no meter is shown.
 */
export function DraftUsage({ contextTokens }: { contextTokens: number | null }) {
  const { t } = useShellLanguage();
  if (!contextTokens || contextTokens <= 0) return null;
  const summary = `0/${compactTokens(String(contextTokens))}`;
  const label = t("Context usage: {summary}", { summary: `0% · ${summary}` });
  return <Button variant="minimal" className="context-usage" data-intent="none" disabled aria-label={label} title={label}>
    <ContextRing percent={0} />
    <span className="context-usage-text">0%</span>
    <span className="context-usage-tokens">{summary}</span>
  </Button>;
}

/**
 * The end of the sign-in of the provider, after the prompt cache timer: a warning with the time left ("3d") in its
 * last week, in the danger colour in its last day and once it ended. Nothing is shown before, nor where the provider
 * does not say when its sign-in ends.
 */
export function SignInEnd({ expiresAt, provider }: { expiresAt: string | null | undefined; provider: string | null | undefined }) {
  const { t } = useShellLanguage();
  const [now, setNow] = useState(() => Date.now());
  const view = signInEnd(expiresAt, now);
  useEffect(() => { setNow(Date.now()); }, [expiresAt]);
  useEffect(() => {
    if (!expiresAt || view?.state === "ended") return;
    const timer = setTimeout(() => setNow(Date.now()), 60_000);
    return () => clearTimeout(timer);
  }, [expiresAt, now, view?.state]);
  if (!view) return null;
  const name = provider || t("session provider");
  const date = new Date(expiresAt!).toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
  const title = view.state === "ended"
    ? t("The sign-in of {provider} ended on {date}: its turns fail until it is signed in again.", { provider: name, date })
    : t("The sign-in of {provider} ends in {time}, on {date}. Sign in again before then: once it ends, its turns fail until it is signed in again.", { provider: name, time: view.text, date });
  return <span className="sign-in-end" data-state={view.state} role="img" aria-label={title} title={title}>
    <AppIcon name="warning" size={14} />{view.text && <span>{view.text}</span>}
  </span>;
}

/**
 * How long until the prompt cache of the provider expires, beside the context ring: "⏱ 37m", in the warning colour
 * in its last minutes and "expired" after. Nothing is shown where the provider does not say when its cache expires.
 */
export function CacheCountdown({ expiresAt }: { expiresAt: string | null | undefined }) {
  const { t } = useShellLanguage();
  const [now, setNow] = useState(() => Date.now());
  const view = cacheCountdown(expiresAt, now);
  const tick = cacheCountdownTick(view);
  useEffect(() => { setNow(Date.now()); }, [expiresAt]);
  useEffect(() => {
    if (tick === null) return;
    const timer = setTimeout(() => setNow(Date.now()), tick);
    return () => clearTimeout(timer);
  }, [tick, now]);
  if (!view) return null;
  const time = expiresAt ? new Date(expiresAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }) : "";
  const title = view.state === "expired"
    ? t("The prompt cache has expired: the next message sends the whole conversation again, at the full input price.")
    : t("The prompt cache expires in {time} (at {clock}). A message sent before then reads the conversation from the cache, at a fraction of the input price, and keeps it for longer.", { time: view.text, clock: time });
  const text = view.state === "expired" ? t("expired") : view.text;
  return <span className="prompt-cache-countdown" data-state={view.state} role="img" aria-label={`${t("Prompt cache")}: ${text}`} title={title}>
    <AppIcon name="timer" size={14} /><span>{text}</span>
  </span>;
}
