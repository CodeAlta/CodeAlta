import { useCallback, useEffect, useRef, useState } from "react";
import { Button, Tag } from "@blueprintjs/core";
import { providerUsage, type ProviderUsageResponse } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import type { MessageKey } from "./localization";
import { useShellLanguage } from "./shellLanguage";
import { elapsedPercent, limitIntent, limitLabel, planLabel, providerLimits, resetMoment, timeLeft, unitCount, type UsageLimit } from "./subscriptionUsage";

type Api = Pick<typeof providerUsage, "read">;
const units: Record<string, MessageKey> = { requests: "requests", credits: "credits" };

/**
 * The limits of a subscription, one meter each: how much is used, how far the period has gone (the mark on the
 * meter), what is counted when the provider counts it, and when the limit starts over.
 */
export function UsageLimitList({ limits, now = Date.now() }: { limits: readonly UsageLimit[]; now?: number }) {
  const { t, locale } = useShellLanguage();
  return <div className="subscription-limits">{limits.map(limit => {
    const { key, scope } = limitLabel(limit);
    const percent = limit.usedPercent === null ? null : Math.round(limit.usedPercent);
    const elapsed = elapsedPercent(limit, now);
    const until = timeLeft(limit.resetsAt, now);
    const moment = until ? resetMoment(limit.resetsAt, now, locale) : null;
    const unit = limit.unit && units[limit.unit] ? t(units[limit.unit]) : limit.unit ?? "";
    const counted = limit.used !== null && limit.total !== null
      ? t("{used} of {total} {unit}", { used: unitCount(limit.used, locale), total: unitCount(limit.total, locale), unit }).trim() : null;
    // A balance has no total to be a share of: what is left is its figure.
    const left = limit.remaining !== null
      ? t("{count} {unit} left", { count: unitCount(limit.remaining, locale), unit: limit.id === "credits" ? "" : unit }).replace(/\s+/g, " ").trim() : null;
    const headline = limit.unlimited ? t("Unlimited") : percent !== null ? t("{percent}% used", { percent }) : left;
    return <div key={limit.id} className="subscription-limit" data-intent={limit.unlimited ? "none" : limitIntent(percent)}>
      <div className="subscription-limit-head">
        <span className="subscription-limit-name">{t(key)}{scope && <small>{scope}</small>}</span>
        {headline && <strong>{headline}</strong>}
      </div>
      {!limit.unlimited && percent !== null && <div className="subscription-limit-bar" role="meter" aria-valuemin={0} aria-valuemax={100}
        aria-valuenow={Math.min(100, percent)} aria-label={`${t(key)}${scope ? ` ${scope}` : ""}`}>
        <span style={{ width: `${Math.min(100, percent)}%` }} />
        {elapsed !== null && <i style={{ left: `${elapsed}%` }} title={t("Time elapsed in this period: {percent}%", { percent: Math.round(elapsed) })} />}
      </div>}
      {(counted || until) && <div className="subscription-limit-foot">
        <span>{counted}</span>
        {until && <span>{t("Resets in {time}", { time: until })}{moment && <small>{moment}</small>}</span>}
      </div>}
    </div>;
  })}</div>;
}

/** Reads the usage of a provider when asked: on the first render that is `active`, and again on `refresh`. */
export function useProviderUsage(epoch: string | null, providerKey: string | null, active: boolean, api: Api = providerUsage) {
  const [response, setResponse] = useState<ProviderUsageResponse | null>(null);
  const [loading, setLoading] = useState(false);
  const [request, setRequest] = useState(0);
  const refreshing = useRef(false);
  useEffect(() => { setResponse(null); }, [epoch, providerKey]);
  useEffect(() => {
    if (!active || !epoch || !providerKey) return;
    const abort = new AbortController();
    const refresh = refreshing.current;
    refreshing.current = false;
    setLoading(true);
    void api.read({ expectedEpoch: epoch, key: providerKey, refresh }, { signal: abort.signal, timeoutMilliseconds: 60000 })
      .then(value => { if (!abort.signal.aborted) setResponse(value.key === providerKey || value.key === providerKey.toLowerCase() ? value : { ...value, status: "failed" }); },
        () => { if (!abort.signal.aborted) setResponse({ status: "failed", key: providerKey, supported: true, plan: null, limits: [], observedAt: null, tool: null }); })
      .finally(() => { if (!abort.signal.aborted) setLoading(false); });
    return () => abort.abort();
  }, [api, epoch, providerKey, active, request]);
  const refresh = useCallback(() => { refreshing.current = true; setRequest(value => value + 1); }, []);
  return { response, loading, refresh };
}

/**
 * Why a provider that can report its usage shows none, as a sentence; null while there is usage or nothing to say.
 * Codex and Claude Code are asked through their own command-line program, which `tool` names when it is what is
 * missing.
 */
export function usageNote(response: ProviderUsageResponse | null): { key: MessageKey; tool?: string } | null {
  switch (response?.status) {
    case undefined: case "ok": case "unavailable": case "stale_epoch": return null;
    case "signed_out": return { key: "Sign in to see the usage." };
    case "tool_missing": return response.tool ? { key: "{tool} is not installed on this computer. Install it to see the usage.", tool: response.tool } : { key: "No usage is reported for this account." };
    case "tool_signed_out": return response.tool ? { key: "Sign in to {tool} with this account to see the usage.", tool: response.tool } : { key: "No usage is reported for this account." };
    case "not_available": return { key: "No usage is reported for this account." };
    default: return { key: "The usage could not be read." };
  }
}

/** The Usage section of a subscription provider on the Providers page: its plan, its limits, and a way to read them again. */
export function ProviderUsagePanel({ epoch, providerKey, api }: { epoch: string; providerKey: string; api?: Api }) {
  const { t, locale } = useShellLanguage();
  const { response, loading, refresh } = useProviderUsage(epoch, providerKey, true, api);
  if (response && !response.supported) return null;
  const note = usageNote(response);
  const observed = response?.observedAt && !Number.isNaN(new Date(response.observedAt).getTime())
    ? new Date(response.observedAt).toLocaleTimeString(locale, { hour: "2-digit", minute: "2-digit" }) : null;
  return <section className="provider-usage provider-settings-wide" aria-label={t("Usage")} aria-busy={loading}>
    <header>
      <h4><AppIcon name="usage" size={15} />{t("Usage")}</h4>
      {planLabel(response?.plan) && <Tag minimal round>{planLabel(response?.plan)}</Tag>}
      <span className="provider-usage-spacer" />
      {loading ? <ActivitySpinner size={14} /> : observed && <span className="bp6-text-muted">{t("updated {time}", { time: observed })}</span>}
      <Button variant="minimal" size="small" icon={<AppIcon name="refresh" size={14} />} disabled={loading} aria-label={t("Refresh usage")} title={t("Refresh usage")} onClick={refresh} />
    </header>
    {response?.status === "ok" && response.limits.length > 0 ? <UsageLimitList limits={providerLimits(response.limits)} />
      : note ? <p className="provider-usage-note">{t(note.key, note.tool ? { tool: note.tool } : undefined)}</p>
      : !response && <p className="provider-usage-note">{t("Reading the usage…")}</p>}
  </section>;
}
