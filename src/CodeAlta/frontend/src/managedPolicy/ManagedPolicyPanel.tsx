import { Button } from "@blueprintjs/core";
import type { ManagedPolicyResponse } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { permissionModeName } from "../permissionModes";
import { useShellLanguage } from "../shellLanguage";
import { downloadSize, managedPolicyLines, readingUnderWay, useManagedPolicy, type ManagedPolicyApi } from "./managedPolicy";

/**
 * The policy of the organization on the page of a Copilot provider: what it enforces in every session, where its
 * reading stands (the download of the Copilot runtime the first time), and a way to read it again.
 */
export function ManagedPolicyPanel({ epoch, api }: { epoch: string; api: ManagedPolicyApi }) {
  const { t, locale } = useShellLanguage();
  const { policy, refreshing, refresh } = useManagedPolicy(epoch, api);
  if (policy && policy.status !== "ok") return null;
  const reading = policy?.copilot ?? null;
  const size = downloadSize(reading);
  const lines = managedPolicyLines(policy);
  const readAt = reading?.readAt ?? policy?.readAt ?? null;
  const time = readAt && !Number.isNaN(new Date(readAt).getTime())
    ? new Date(readAt).toLocaleTimeString(locale, { hour: "2-digit", minute: "2-digit" }) : null;
  return <section className="provider-usage provider-settings-wide managed-policy" aria-label={t("Policy of your organization")} aria-busy={refreshing}>
    <header>
      <h4><AppIcon name="lock" size={15} />{t("Policy of your organization")}</h4>
      <span className="provider-usage-spacer" />
      {refreshing ? <ActivitySpinner size={14} /> : time && <span className="bp6-text-muted">{t("read {time}", { time })}</span>}
      <Button variant="minimal" size="small" icon={<AppIcon name="refresh" size={14} />} disabled={refreshing} aria-label={t("Refresh the policy")}
        title={t("Refresh the policy")} onClick={refresh} />
    </header>
    {reading?.state === "installing" ? <p className="provider-usage-note">{t("Downloading the Copilot runtime…")}{size && <> {t("{received} of {total} MB", size)}</>}</p>
      : reading?.state === "reading" ? <p className="provider-usage-note">{t("Reading the policy of your organization…")}</p>
      : reading?.state === "failed" ? <div className="provider-usage-note managed-policy-failure">
          <p>{t("The policy of your organization could not be read.")}</p>
          {reading.message && <p className="bp6-text-muted">{reading.message}</p>}
          <Button size="small" icon={<AppIcon name="refresh" size={13} />} onClick={refresh}>{t("Try again")}</Button>
        </div>
      : null}
    {lines.length > 0 ? <ul className="managed-policy-lines">{lines.map(line => <li key={line.key}>
        {t(line.key, line.mode ? { mode: permissionModeName(line.mode) ? t(permissionModeName(line.mode)!) : line.mode } : undefined)}</li>)}</ul>
      : policy && !readingUnderWay(reading) && reading?.state !== "failed" && <p className="provider-usage-note">{t("No policy of your organization applies.")}</p>}
    {policy?.supportUrl && <p className="managed-policy-support"><a href={policy.supportUrl} target="_blank" rel="noreferrer">{t("Who to ask")}</a></p>}
  </section>;
}

/**
 * The mark beside a setting the policy of the organization decides: a lock, the words that say so, and where to ask
 * when the organization says where.
 */
export function ManagedPolicyLock({ policy }: { policy: Pick<ManagedPolicyResponse, "supportUrl" | "source"> }) {
  const { t } = useShellLanguage();
  return <p className="settings-field-help managed-policy-lock" data-managed-policy>
    <AppIcon name="lock" size={13} />
    <span title={policy.source ?? undefined}>{t("Set by your organization")}</span>
    {policy.supportUrl && <a href={policy.supportUrl} target="_blank" rel="noreferrer">{t("Who to ask")}</a>}
  </p>;
}
