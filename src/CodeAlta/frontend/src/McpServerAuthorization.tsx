import { useEffect, useRef, useState } from "react";
import { Button, Callout, Tag } from "@blueprintjs/core";
import type { mcpServers as McpServersClient, McpServerEntry } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { Copyable } from "./ProviderAccount";
import { authorizationAddress, authorizationFailure } from "./mcpAuthorization";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

type Api = Pick<typeof McpServersClient, "login" | "logout">;

/**
 * Browser authorization (OAuth) of a saved HTTP MCP server: whether tokens are stored, a button that starts the
 * authorization, and one that removes the tokens. While an authorization runs, the page shows the address the
 * host opened in the browser. Mount it with a key per server, so that leaving a server cancels its authorization.
 */
export function McpServerAuthorization({ epoch, projectId, server, api, blocked, onChanged }: {
  epoch: string | null; projectId: string | null; server: Pick<McpServerEntry, "key" | "scope" | "authorized" | "authorizationExpiresAt">; api: Api;
  /** Why an authorization is not offered right now (unsaved changes, a disabled server), or null. */
  blocked: MessageKey | null;
  /** Called after an authorization or a sign-out changed what the host stores. */
  onChanged: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const [running, setRunning] = useState<{ prompted: boolean; url: string | null } | null>(null);
  const [notice, setNotice] = useState<{ key: MessageKey; intent: "success" | "warning" | "danger"; values?: Record<string, string | number> } | null>(null);
  const [signingOut, setSigningOut] = useState(false);
  const work = useRef<AbortController | null>(null);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; work.current?.abort(); }; }, []);
  const request = { expectedEpoch: epoch, projectId, scope: server.scope, key: server.key };

  async function authorize() {
    if (work.current || blocked || signingOut) return;
    const abort = new AbortController();
    work.current = abort;
    setRunning({ prompted: false, url: null }); setNotice(null);
    try {
      const events = await api.login(request, { signal: abort.signal });
      for await (const event of events) {
        if (abort.signal.aborted || !alive.current) return;
        if (event.kind === "prompt") setRunning({ prompted: true, url: authorizationAddress(event) });
        else if (event.kind === "completed") setNotice({ key: "Authorized. The server lists {count} tools.", intent: "success", values: { count: event.tools } });
        else if (event.kind === "failed") { const failure = authorizationFailure(event.code, event.detail); setNotice({ ...failure, values: { detail: failure.detail ?? "" } }); }
      }
    } catch {
      if (alive.current && !abort.signal.aborted) setNotice({ key: "The authorization did not complete.", intent: "danger" });
    } finally {
      if (work.current === abort) work.current = null;
      // A failed authorization can still have replaced or removed the stored tokens.
      if (alive.current && !abort.signal.aborted) { setRunning(null); onChanged(); }
    }
  }
  function cancel() { work.current?.abort(); work.current = null; setRunning(null); setNotice({ key: "Authorization canceled.", intent: "warning" }); onChanged(); }
  async function signOut() {
    if (work.current || signingOut) return;
    setSigningOut(true); setNotice(null);
    try {
      const reply = await api.logout(request, { timeoutMilliseconds: 30000 });
      if (!alive.current) return;
      setNotice(reply.status !== "ok" ? { key: "The sign-out did not complete.", intent: "danger" }
        : { key: reply.removed ? "The stored tokens were removed." : "No tokens were stored.", intent: "success" });
      if (reply.status === "ok") onChanged();
    } catch { if (alive.current) setNotice({ key: "The sign-out did not complete.", intent: "danger" }); }
    finally { if (alive.current) setSigningOut(false); }
  }

  const expires = server.authorized && server.authorizationExpiresAt && Number.isFinite(Date.parse(server.authorizationExpiresAt))
    ? new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(server.authorizationExpiresAt)) : null;
  return <div className="provider-account">
    <div className="provider-account-status">
      <AppIcon name="user" size={16} />
      <Tag minimal intent={server.authorized ? "success" : "none"}>{t(server.authorized ? "Authorized" : "Not authorized")}</Tag>
      {expires && <span className="bp6-text-muted">{t("Token valid until {time}", { time: expires })}</span>}
      <span className="provider-settings-spacer" />
      <Button size="small" intent={server.authorized ? "none" : "primary"} disabled={!!blocked || !!running || signingOut}
        title={blocked ? t(blocked) : undefined} onClick={() => void authorize()}>{t(server.authorized ? "Authorize again" : "Authorize")}</Button>
      {server.authorized && <Button size="small" disabled={!!running || signingOut} onClick={() => void signOut()}>{t("Sign out")}</Button>}
    </div>
    {blocked && !running && <span className="bp6-text-muted">{t(blocked)}</span>}
    {running && <Callout compact intent="primary" className="provider-account-running" icon={<ActivitySpinner size={16} />}>
      <p>{t(!running.prompted ? "Starting the authorization…" : running.url
        ? "Finish the authorization in your browser. If no window opened, open this address:" : "Finish the authorization in the browser window that opened.")}</p>
      {running.url && <Copyable value={running.url} label={t("Copy address")} />}
      <Button size="small" onClick={cancel}>{t("Cancel")}</Button>
    </Callout>}
    {notice && !running && <Callout compact intent={notice.intent} role={notice.intent === "success" ? "status" : "alert"}>{t(notice.key, notice.values)}</Callout>}
  </div>;
}
