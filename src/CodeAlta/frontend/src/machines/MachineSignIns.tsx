import { useCallback, useEffect, useState } from "react";
import { Button, Callout, Dialog, DialogBody, DialogFooter, InputGroup, Tag } from "@blueprintjs/core";
import { machines, type MachineSignInItemView, type MachineSignInPromptView, type MachineSignInsView, type MachineView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { Copyable } from "../ProviderAccount";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { managementProblem } from "./machineManagement";
import { signInKindLabel, signInProblem, signInStateIntent, signInStateLabel } from "./signInStates";

/** The calls of the sign-ins of a machine. */
export type MachineSignInsApi = Pick<typeof machines, "signIns" | "signInStart" | "signInWait" | "signInCode" | "signInCancel" | "signOut">;

type Problem = { key: MessageKey; detail?: string | null } | null;

/**
 * The sign-ins a machine this computer manages keeps for itself: its providers that sign in with an account and its MCP servers
 * that authorize with OAuth, each with whether it is signed in. Sign in… starts the sign-in on the machine: the page opens in the
 * browser of this computer, whose callback goes back to the machine, or a device code shows here. The tokens stay on the machine.
 */
export function MachineSignIns({ epoch, machine, api = machines }: { epoch: string; machine: MachineView; api?: MachineSignInsApi }) {
  const { t } = useShellLanguage();
  const [view, setView] = useState<MachineSignInsView | null>(null);
  const [problem, setProblem] = useState<Problem>(null);
  const [generation, setGeneration] = useState(0);
  const [signingIn, setSigningIn] = useState<MachineSignInItemView | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const reload = useCallback(() => setGeneration(value => value + 1), []);
  useEffect(() => {
    const abort = new AbortController();
    api.signIns({ expectedEpoch: epoch, machineId: machine.id }, { signal: abort.signal, timeoutMilliseconds: 30_000 }).then(value => {
      if (abort.signal.aborted) return;
      const failed = managementProblem(value.status);
      if (failed) setProblem({ key: failed, detail: value.message }); else { setProblem(null); setView(value); }
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The machine does not answer: it is offline." }); });
    return () => abort.abort();
  }, [api, epoch, machine.id, generation]);

  async function signOut(item: MachineSignInItemView) {
    setBusy(`${item.kind}:${item.key}`);
    try {
      const result = await api.signOut({ expectedEpoch: epoch, machineId: machine.id, kind: item.kind, key: item.key, space: item.space ?? null }, { timeoutMilliseconds: 60_000 });
      const failed = managementProblem(result.status);
      setProblem(failed ? { key: failed, detail: result.message } : null);
    } catch {
      setProblem({ key: "The machine does not answer: it is offline." });
    } finally { setBusy(null); reload(); }
  }

  // A machine that does not sign in from here (a node of an older version) says nothing: the section stays away.
  if (!view && !problem) return null;
  if (problem?.key === "The machine could not do it." && !view) return null;
  const items = view?.items ?? [];
  return <section className="machine-section machine-signins-section" aria-label={t("Sign-ins")}>
    <div className="machine-section-head"><h2>{t("Sign-ins")}</h2></div>
    <p className="muted-text">{t("What {name} signs in to by itself: its providers and its MCP servers. The page opens in the browser of this computer; the tokens stay on {name}.", { name: machine.name })}</p>
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {view && items.length === 0 && <p className="muted-text">{t("Nothing of {name} signs in from here.", { name: machine.name })}</p>}
    {items.length > 0 && <ul className="machine-providers machine-signins">{items.map(item => {
      const id = `${item.kind}:${item.key}`;
      const signedIn = item.state === "signed_in";
      return <li key={id} data-state={item.state}>
        <span className="machine-provider-icon"><AppIcon name={signedIn ? "checked" : "shield"} size={14} className={signedIn ? "machine-ready" : undefined} /></span>
        <span className="machine-provider-main">
          <span className="machine-provider-title"><strong>{item.title}</strong>
            <Tag minimal round intent={signInStateIntent(item.state)}>{t(signInStateLabel(item.state))}</Tag></span>
          <small className="muted-text">{[t(signInKindLabel(item.kind)), item.account && t("as {account}", { account: item.account })].filter(Boolean).join(" · ")}</small>
          {item.detail && item.ways.length === 0 && <small className="machine-provider-problem">{item.detail}</small>}
        </span>
        <span className="machine-provider-actions">
          {busy === id && <ActivitySpinner size={14} />}
          {!signedIn && item.ways.length > 0 && <Button size="small" intent="primary" icon={<AppIcon name="shield" size={13} />} text={t("Sign in…")} disabled={busy !== null}
            onClick={() => setSigningIn(item)} />}
          {signedIn && item.kind !== "cli" && <Button size="small" text={t("Sign out")} disabled={busy !== null} onClick={() => void signOut(item)} />}
        </span>
      </li>;
    })}</ul>}
    {signingIn && <SignInDialog epoch={epoch} machine={machine} api={api} item={signingIn} onClose={() => { setSigningIn(null); reload(); }} />}
  </section>;
}

/**
 * A sign-in that runs on the machine: the browser of this computer opens its page and the callback goes back to the machine,
 * a device code shows here with Copy, or the page shows a code that is pasted here. The dialog follows the machine until it ends.
 */
function SignInDialog({ epoch, machine, api, item, onClose }: {
  epoch: string; machine: MachineView; api: MachineSignInsApi; item: MachineSignInItemView; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [prompt, setPrompt] = useState<MachineSignInPromptView | null>(null);
  const [state, setState] = useState<"starting" | "waiting" | "completed" | "failed">("starting");
  const [account, setAccount] = useState<string | null>(null);
  const [problem, setProblem] = useState<Problem>(null);
  const [code, setCode] = useState("");
  const [sending, setSending] = useState(false);
  useEffect(() => {
    const abort = new AbortController();
    let attempt: string | null = null;
    void (async () => {
      try {
        const started = await api.signInStart({ expectedEpoch: epoch, machineId: machine.id, kind: item.kind, key: item.key, space: item.space ?? null, way: null },
          { signal: abort.signal, timeoutMilliseconds: 90_000 });
        if (abort.signal.aborted) return;
        if (started.status !== "ok" || !started.attemptId) { setState("failed"); setProblem({ key: signInProblem(started.status), detail: started.message }); return; }
        attempt = started.attemptId;
        setPrompt(started);
        setState("waiting");
        // The machine waits for the person; the page asks it again until the sign-in ends or the dialog closes.
        while (!abort.signal.aborted) {
          const waited = await api.signInWait({ expectedEpoch: epoch, machineId: machine.id, attemptId: started.attemptId }, { signal: abort.signal, timeoutMilliseconds: 40_000 });
          if (abort.signal.aborted) return;
          if (waited.status === "completed") { setAccount(waited.account ?? null); setState("completed"); return; }
          if (waited.status !== "waiting") {
            setState("failed");
            setProblem({ key: waited.code === "timeout" ? "The sign-in took too long: start it again." : signInProblem(waited.status), detail: waited.message });
            return;
          }
        }
      } catch {
        if (!abort.signal.aborted) { setState("failed"); setProblem({ key: "The machine does not answer: it is offline." }); }
      }
    })();
    return () => {
      abort.abort();
      // A dialog closed before the end ends the attempt on the machine, and the listener of this computer with it.
      if (attempt) void api.signInCancel({ expectedEpoch: epoch, machineId: machine.id, attemptId: attempt }, { timeoutMilliseconds: 15_000 }).catch(() => undefined);
    };
  }, [api, epoch, machine.id, item.kind, item.key, item.space]);

  async function sendCode() {
    if (!prompt?.attemptId || !code.trim()) return;
    setSending(true);
    try {
      const sent = await api.signInCode({ expectedEpoch: epoch, machineId: machine.id, attemptId: prompt.attemptId, code: code.trim() }, { timeoutMilliseconds: 40_000 });
      if (sent.status === "completed") { setAccount(sent.account ?? null); setState("completed"); }
      else if (sent.status !== "waiting") { setState("failed"); setProblem({ key: signInProblem(sent.status), detail: sent.message }); }
    } catch {
      setState("failed");
      setProblem({ key: "The machine does not answer: it is offline." });
    } finally { setSending(false); }
  }

  const name = item.title;
  return <Dialog isOpen className="machine-provider-signin-dialog" title={t("Sign {name} in on {machine}", { name, machine: machine.name })} onClose={onClose}>
    <DialogBody>
      {state === "starting" && <p className="muted-text"><ActivitySpinner size={14} /> {t("{machine} starts the sign-in…", { machine: machine.name })}</p>}
      {state === "waiting" && (prompt?.way === "browser" || prompt?.way === "page") && <Callout compact intent="primary" role="status" icon={<ActivitySpinner size={16} />}>
        <p>{t("Finish the sign-in in the browser that opened on this computer.")}</p>
        {prompt.page && <div className="machine-provider-code-row"><a href={prompt.page} target="_blank" rel="noreferrer">{prompt.page}</a></div>}
      </Callout>}
      {state === "waiting" && prompt?.way === "device" && <Callout compact intent="primary" role="status" icon={<ActivitySpinner size={16} />} className="machine-provider-code">
        <p>{t("Open this page in your browser and enter the code:")}</p>
        {prompt.page && <div className="machine-provider-code-row"><a href={prompt.page} target="_blank" rel="noreferrer">{prompt.page}</a></div>}
        {prompt.userCode && <Copyable code value={prompt.userCode} label={t("Copy code")} />}
      </Callout>}
      {state === "waiting" && prompt?.way === "code" && <Callout compact intent="primary" role="status">
        <p>{t("Open this page, then paste the code it shows:")}</p>
        {prompt.page && <div className="machine-provider-code-row"><a href={prompt.page} target="_blank" rel="noreferrer">{prompt.page}</a></div>}
        <form className="fleet-server-form" onSubmit={event => { event.preventDefault(); void sendCode(); }}>
          <InputGroup value={code} disabled={sending} autoComplete="off" spellCheck={false} aria-label={t("Code")} onChange={event => setCode(event.target.value)}
            rightElement={<Button size="small" intent="primary" disabled={sending || !code.trim()} text={t("Send")} onClick={() => void sendCode()} />} />
        </form>
      </Callout>}
      {state === "completed" && <Callout compact intent="success" role="status" icon={<AppIcon name="checked" size={16} />}>
        {account ? t("{name} is signed in on {machine} as {account}.", { name, machine: machine.name, account }) : t("{name} is signed in on {machine}.", { name, machine: machine.name })}
      </Callout>}
      {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<Button intent={state === "completed" ? "primary" : undefined} text={t(state === "completed" || state === "failed" ? "Done" : "Cancel")} onClick={onClose} />} />
  </Dialog>;
}
