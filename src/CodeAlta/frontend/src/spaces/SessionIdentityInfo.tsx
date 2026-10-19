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

/** The value of an identity row in the language of the window: "Off" is the one word of a row that is translated. */
export function identityValue(label: string, value: string, t: (key: "Off") => string): string {
  return label === "Commit signing" ? value.replace(/^Off(?= |$)/u, t("Off")) : value;
}

/**
 * The CLI profiles a session works under, as its information window shows them: the name of each, and its variables
 * as they are written (`${env:NAME}` stays a reference). A variable left out, because the variable it refers to is not
 * set, and a profile the project gives or changes are said so.
 */
export function cliRows(reply: SessionIdentityResponse | null): readonly Readonly<{ name: string; variables: string; project: boolean; unset: readonly string[] }>[] {
  if (reply?.status !== "ok") return [];
  return (reply.cli ?? []).map(profile => ({ name: profile.name, project: profile.source === "project", unset: profile.unset,
    variables: profile.env.map(variable => `${variable.name}=${variable.value}`).join(" · ") }));
}

/**
 * The identity a session works under, in its information window: the Git author, the signing, the GitHub profile,
 * the Jira account and the CLI profiles of its space and its project, as the session attached with them. Nothing is
 * shown for a session that keeps the usual settings, and no secret is ever shown.
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
  const rows = identityRows(reply) ?? [];
  const profiles = cliRows(reply);
  if (rows.length === 0 && profiles.length === 0) return null;
  return <section aria-labelledby="session-info-identity"><h3 id="session-info-identity">{t("Identity")}</h3>
    <dl className="session-info-fields" tabIndex={0}>
      {rows.map(([label, value]) => <div key={label}><dt>{t(label)}</dt><dd>{identityValue(label, value, t)}</dd></div>)}
      {profiles.map(profile => <div key={`cli-${profile.name}`} data-cli-profile={profile.name}>
        <dt>{t("CLI {name}", { name: profile.name })}</dt>
        <dd>{[profile.variables, ...profile.project ? [t("from the project")] : [],
          ...profile.unset.map(name => t("{variable} left out: a variable it refers to is not set", { variable: name }))].join(" · ")}</dd>
      </div>)}
    </dl>
  </section>;
}
