import { useEffect, useState } from "react";
import { Button, Callout } from "@blueprintjs/core";
import { fleetServer, type FleetServerView } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import type { MessageKey } from "../localization";
import { useShellLanguage } from "../shellLanguage";
import { signInCode, type SignInCode as Code } from "./signIn";
import { SignInCode } from "./SignInCode";

/** The calls of a sign-in again. */
export type SignInAgainApi = Pick<typeof fleetServer, "signInSettings" | "startSignInCode" | "openSignInPage">;

const methodNames: Record<string, string> = { microsoft: "Microsoft", google: "Google", github: "GitHub" };

/**
 * What an action the server confirms with a sign-in again shows once it asked for one (a computer that joined with an
 * enrollment code, a recovery key): one button for each way to sign in linked to the person, and the code of GitHub while
 * it waits. `run` does the action with the method chosen and, for GitHub, the handle of its code.
 */
export function SignInAgain({ server, epoch, api = fleetServer, message, values, run }: {
  server: Pick<FleetServerView, "id">; epoch: string; api?: SignInAgainApi; message: MessageKey; values?: Readonly<Record<string, string | number>>;
  run: (method: string, handle: string | null) => Promise<void>;
}) {
  const { t } = useShellLanguage();
  const [methods, setMethods] = useState<string[] | null>(null);
  const [working, setWorking] = useState<string | null>(null);
  const [code, setCode] = useState<Code | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    const abort = new AbortController();
    void api.signInSettings({ expectedEpoch: epoch, linkId: server.id }, { signal: abort.signal, timeoutMilliseconds: 15_000 })
      .then(read => { if (!abort.signal.aborted) setMethods(read.status === "ok" ? [...new Set(read.logins.map(login => login.method))] : []); })
      .catch(() => { if (!abort.signal.aborted) setMethods([]); });
    return () => abort.abort();
  }, [api, epoch, server.id]);
  async function go(method: string) {
    setWorking(method);
    setFailed(false);
    try {
      let handle: string | null = null;
      // GitHub confirms with a code to enter at GitHub, shown here while the host waits for it.
      if (method === "github") {
        const started = await api.startSignInCode({ expectedEpoch: epoch, method, url: null, linkId: server.id, purpose: "confirm", confirmWith: null, confirmHandle: null,
          recoveryWords: null }, { timeoutMilliseconds: 30_000 }).catch(() => null);
        const shown = started?.status === "ok" ? signInCode(started.code) : null;
        if (!shown) { setFailed(true); return; }
        setCode(shown);
        handle = shown.handle;
      }
      await run(method, handle);
    } finally { setWorking(null); setCode(null); }
  }
  const name = (method: string) => method === "passkey" ? t("Passkey") : methodNames[method] ?? method;
  return <Callout compact intent="primary" role="status" className="fleet-sign-in-confirm">
    {t(message, values)}
    {methods === null ? <div><ActivitySpinner size={14} /></div>
      : methods.length === 0 ? <div className="muted-text">{t("Link a way to sign in first: adding a computer or a recovery key needs one to sign in again with.")}</div>
        : <div className="fleet-sign-in-confirm-methods">{methods.map(method => <Button key={method} size="small" disabled={working !== null}
          text={t("Continue with {method}", { method: name(method) })} onClick={() => void go(method)} />)}</div>}
    {working && code && <SignInCode epoch={epoch} code={code} api={api} />}
    {working && !code && working !== "github" && <div className="muted-text">{t("Finish signing in in your browser…")}</div>}
    {failed && <div className="muted-text">{t("The operation did not complete.")}</div>}
  </Callout>;
}
