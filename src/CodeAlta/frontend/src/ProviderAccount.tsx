import { useEffect, useRef, useState } from "react";
import { Button, Callout, Tag } from "@blueprintjs/core";
import type { providerLogin as ProviderLoginClient, ProviderLoginStatusResponse } from "#neoastra";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { loginFailure, loginModeLabel, loginPrompt, type LoginPrompt } from "./providerSignIn";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

type Api = Pick<typeof ProviderLoginClient, "status" | "login" | "logout">;

// A value with a button that copies it; the button shows the outcome for a moment.
function Copyable({ value, label, code = false }: { value: string; label: string; code?: boolean }) {
  const { t } = useShellLanguage();
  const [copied, setCopied] = useState(false);
  const reset = useRef<number | undefined>(undefined);
  useEffect(() => () => window.clearTimeout(reset.current), []);
  return <span className={`provider-account-copy${code ? " provider-account-code" : ""}`}><code>{value}</code>
    <Button size="small" variant="minimal" icon={<AppIcon name={copied ? "checked" : "copy"} size={14} />} aria-label={copied ? t("Copied") : label} title={copied ? t("Copied") : label}
      onClick={() => { void navigator.clipboard?.writeText(value).then(() => { setCopied(true); window.clearTimeout(reset.current);
        reset.current = window.setTimeout(() => setCopied(false), 1600); }, () => { /* The value stays selectable. */ }); }} /></span>;
}

/**
 * Account sign-in of a provider that authenticates with its own account (Codex, Copilot, xAI): the stored
 * sign-in state, a button per sign-in method, and sign-out. While a sign-in runs, the page shows the address
 * the host opened in the browser and, for a device flow, the code to enter there.
 */
export function ProviderAccount({ epoch, providerKey, api, blocked, onChanged }: {
  epoch: string; providerKey: string; api: Api;
  /** Why sign-in is not offered right now (unsaved changes), or null. */
  blocked: MessageKey | null;
  /** Called after a sign-in or sign-out changed what the host stores. */
  onChanged: () => void;
}) {
  const { t, locale } = useShellLanguage();
  const [status, setStatus] = useState<ProviderLoginStatusResponse | null>();
  const [generation, setGeneration] = useState(0);
  const [running, setRunning] = useState<{ mode: string; prompt: LoginPrompt | null } | null>(null);
  const [notice, setNotice] = useState<{ key: MessageKey; intent: "success" | "warning" | "danger"; detail?: string | null } | null>(null);
  const [signingOut, setSigningOut] = useState(false);
  const work = useRef<AbortController | null>(null);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; work.current?.abort(); }; }, []);
  useEffect(() => {
    const abort = new AbortController();
    setStatus(undefined);
    void api.status({ expectedEpoch: epoch, key: providerKey }, { signal: abort.signal, timeoutMilliseconds: 15000 })
      .then(value => { if (!abort.signal.aborted) setStatus(value.status === "ok" && value.key === providerKey ? value : null); },
        () => { if (!abort.signal.aborted) setStatus(null); });
    return () => abort.abort();
  }, [api, epoch, providerKey, generation]);
  // Another provider's sign-in must not keep running behind the form of this one.
  useEffect(() => () => { work.current?.abort(); work.current = null; }, [providerKey]);

  async function signIn(mode: string) {
    if (work.current || blocked) return;
    const abort = new AbortController();
    work.current = abort;
    setRunning({ mode, prompt: null }); setNotice(null);
    try {
      const events = await api.login({ expectedEpoch: epoch, key: providerKey, mode }, { signal: abort.signal });
      for await (const event of events) {
        if (abort.signal.aborted || !alive.current) return;
        if (event.kind === "prompt") setRunning({ mode, prompt: loginPrompt(event) });
        else if (event.kind === "completed") {
          setNotice({ key: event.detail ? "Signed in. {detail}" : "Signed in.", intent: "success", detail: event.detail });
          onChanged();
        } else if (event.kind === "failed") setNotice({ ...loginFailure(event.code), detail: event.detail });
      }
    } catch {
      if (alive.current && !abort.signal.aborted) setNotice({ key: "The sign-in did not complete.", intent: "danger" });
    } finally {
      if (work.current === abort) work.current = null;
      if (alive.current) { setRunning(null); setGeneration(value => value + 1); }
    }
  }
  function cancel() { work.current?.abort(); work.current = null; setRunning(null); setNotice({ key: "Sign-in canceled.", intent: "warning" }); setGeneration(value => value + 1); }
  async function signOut() {
    if (work.current || signingOut) return;
    setSigningOut(true); setNotice(null);
    try {
      const reply = await api.logout({ expectedEpoch: epoch, key: providerKey }, { timeoutMilliseconds: 30000 });
      if (!alive.current) return;
      setNotice(reply.status === "ok" ? { key: "Signed out.", intent: "success" } : { key: "The sign-out did not complete.", intent: "danger" });
      if (reply.status === "ok") onChanged();
    } catch { if (alive.current) setNotice({ key: "The sign-out did not complete.", intent: "danger" }); }
    finally { if (alive.current) { setSigningOut(false); setGeneration(value => value + 1); } }
  }

  const expires = status?.expiresAt && Number.isFinite(Date.parse(status.expiresAt))
    ? new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(status.expiresAt)) : null;
  return <div className="provider-account provider-settings-wide">
    <div className="provider-account-status">
      <AppIcon name="user" size={16} />
      <strong>{t("Account")}</strong>
      {status === undefined ? <ActivitySpinner size={12} />
        : status === null ? <Tag minimal intent="warning">{t("Sign-in state unavailable")}</Tag>
        : <Tag minimal intent={status.signedIn ? "success" : "none"}>{status.signedIn ? status.account ? t("Signed in as {account}", { account: status.account }) : t("Signed in") : t("Not signed in")}</Tag>}
      {status?.detail && <span className="bp6-text-muted">{status.detail}</span>}
      {status?.signedIn && expires && <span className="bp6-text-muted">{t("Token valid until {time}", { time: expires })}</span>}
      <span className="provider-settings-spacer" />
      {(status?.modes ?? []).map(mode => <Button key={mode} size="small" intent={status?.signedIn ? "none" : "primary"} disabled={!!blocked || !!running || signingOut}
        title={blocked ? t(blocked) : undefined} onClick={() => void signIn(mode)}>{t(loginModeLabel(mode))}</Button>)}
      {status?.signedIn && <Button size="small" disabled={!!running || signingOut} onClick={() => void signOut()}>{t("Sign out")}</Button>}
    </div>
    {running && <Callout compact intent="primary" className="provider-account-running" icon={<ActivitySpinner size={16} />}>
      <p>{t(!running.prompt ? "Starting the sign-in…" : running.prompt.userCode ? "Enter this code on the sign-in page to finish:" : running.prompt.browserOpened
        ? "Finish the sign-in in the browser window that opened." : "Open this address in a browser to finish the sign-in:")}</p>
      {running.prompt?.userCode && <Copyable code value={running.prompt.userCode} label={t("Copy code")} />}
      {running.prompt?.url && <Copyable value={running.prompt.url} label={t("Copy address")} />}
      <Button size="small" onClick={cancel}>{t("Cancel")}</Button>
    </Callout>}
    {notice && !running && <Callout compact intent={notice.intent} role={notice.intent === "success" ? "status" : "alert"}>{t(notice.key, { detail: notice.detail ?? "" })}</Callout>}
  </div>;
}
