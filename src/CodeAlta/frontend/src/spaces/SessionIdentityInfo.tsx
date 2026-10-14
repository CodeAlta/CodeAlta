import { useEffect, useState } from "react";
import { spaceIdentity, type SessionIdentityResponse } from "#neoastra";
import { useShellLanguage } from "../shellLanguage";

export type SessionIdentityApi = Pick<typeof spaceIdentity, "session">;

/** What a session works under, as its information window shows it; null when it keeps the usual settings. */
export function identityRows(reply: SessionIdentityResponse | null): readonly (readonly ["Git author" | "Commit signing" | "GitHub profile" | "Jira account", string])[] | null {
  if (reply?.status !== "ok") return null;
  const rows: ["Git author" | "Commit signing" | "GitHub profile" | "Jira account", string][] = [];
  if (reply.gitAuthor) rows.push(["Git author", reply.gitAuthor]);
  if (reply.signing || reply.signOff) rows.push(["Commit signing", [reply.signing === "off" ? "Off" : reply.signing?.toUpperCase(), reply.signOff ? "Signed-off-by" : null].filter(Boolean).join(" · ")]);
  if (reply.gitHubProfile) rows.push(["GitHub profile", reply.gitHubProfile]);
  if (reply.jiraSite || reply.jiraEmail) rows.push(["Jira account", [reply.jiraSite, reply.jiraEmail].filter(Boolean).join(" · ")]);
  return rows.length ? rows : null;
}

/**
 * The identity a session works under, in its information window: the Git author, the signing, the GitHub profile and
 * the Jira account of its space, as the session attached with them. Nothing is shown for a session that keeps the
 * usual settings, and no secret is ever shown.
 */
export function SessionIdentityInfo({ epoch, sessionId, api = spaceIdentity }: { epoch: string | null; sessionId: string; api?: SessionIdentityApi }) {
  const { t } = useShellLanguage();
  const [reply, setReply] = useState<SessionIdentityResponse | null>(null);
  useEffect(() => {
    setReply(null);
    if (!epoch || !sessionId) return;
    let current = true;
    void api.session({ expectedEpoch: epoch, sessionId }, { timeoutMilliseconds: 15_000 }).then(answer => { if (current) setReply(answer); }, () => { });
    return () => { current = false; };
  }, [api, epoch, sessionId]);
  const rows = identityRows(reply);
  if (!rows) return null;
  return <section aria-labelledby="session-info-identity"><h3 id="session-info-identity">{t("Identity")}</h3>
    <dl className="session-info-fields" tabIndex={0}>
      {rows.map(([label, value]) => <div key={label}><dt>{t(label)}</dt><dd>{label === "Commit signing" ? value.replace(/^Off/u, t("Off")) : value}</dd></div>)}
    </dl>
  </section>;
}
