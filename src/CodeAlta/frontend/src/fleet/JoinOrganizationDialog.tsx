import { useEffect, useRef, useState } from "react";
import { Button, Callout, Checkbox, Dialog, DialogBody, DialogFooter, InputGroup, Radio, RadioGroup, Tag } from "@blueprintjs/core";
import { fleetServer, spaces as spacesApi, type FleetServerView, type FleetSignInMethodView, type SpaceItem } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { keyWords, notSetUpProblem, serverProblem } from "./fleetServer";
import { defaultJoinSpaces, joinSteps, joinWords, organizationAddressOf, type JoinCase, type JoinStep } from "./joinOrganization";
import { orderedSignInMethods, signInCode, signInProblem, type SignInCode as Code } from "./signIn";
import { SignInCode } from "./SignInCode";
import { hasBrandMark, SignInMethodIcon } from "./SignInMethodIcon";

/** The calls of the dialog. */
export type JoinOrganizationApi = Pick<typeof fleetServer, "join" | "linkSpace" | "status" | "signInMethods" | "joinWithSignIn" | "startSignInCode" | "openSignInPage">;

/** How often the dialog reads whether this computer is approved, while it waits for that. */
const approvalPollMilliseconds = 3000;

/** How long the browser may take before the sign-in is given up: the server's attempt lives ten minutes. */
const signInTimeoutMilliseconds = 11 * 60_000;

/** The calls of the dialog on the spaces of this computer. */
export type JoinOrganizationSpacesApi = Pick<typeof spacesApi, "list" | "create">;

/**
 * Join an organization: the address of the company's server, then signing in to it — through the system browser with a
 * method the server offers (its company sign-in, a passkey, a linked Microsoft, Google or GitHub account; GitHub shows a
 * code to enter at GitHub while this computer waits), or with the
 * one-time enrollment code an administrator gives — then the space whose sessions it sees (a new Work space by default),
 * and the approval this computer waits for, with the six words of its key: from another computer of the person, or from
 * the owner for a person's first one. A person whose computers are all lost starts from here too: `onRecover` goes to the
 * recovery key instead.
 */
export function JoinOrganizationDialog({ epoch, kind = "organization", api = fleetServer, spaces = spacesApi, onRecover, onSetUp, onClose }: {
  epoch: string; kind?: JoinCase; api?: JoinOrganizationApi; spaces?: JoinOrganizationSpacesApi; onRecover?: () => void;
  /** Called with the address of a server that has no owner yet, when a setup code was typed as an enrollment code: the page goes on with Create my server… > It runs already. */
  onSetUp?: (address: string) => void; onClose: (joined: boolean) => void;
}) {
  const { t } = useShellLanguage();
  // The words of the person's own server (at home they are its owner) or of an organization's.
  const wording = joinWords(kind);
  const [step, setStep] = useState<JoinStep>("address");
  const [address, setAddress] = useState("");
  const [code, setCode] = useState("");
  const [server, setServer] = useState<FleetServerView | null>(null);
  const [words, setWords] = useState<string | null>(null);
  const [list, setList] = useState<readonly SpaceItem[]>([]);
  // The spaces chosen, once the person changed them; until then, those of the case (every space on their own server, a new Work space otherwise).
  const [chosen, setChosen] = useState<string[] | null>(null);
  const selected = chosen ?? defaultJoinSpaces(kind, list);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const [methods, setMethods] = useState<FleetSignInMethodView[] | null>(null);
  const [useCode, setUseCode] = useState(false);
  const [waiting, setWaiting] = useState<string | null>(null);
  const [deviceCode, setDeviceCode] = useState<Code | null>(null);
  const [byOwnDevice, setByOwnDevice] = useState(false);
  // Whether the computer or the administrator approved this one: the dialog says so instead of waiting on.
  const [approved, setApproved] = useState(false);
  const browser = useRef<AbortController | null>(null);
  const url = organizationAddressOf(address);
  useEffect(() => () => browser.current?.abort(), []);
  useEffect(() => {
    if (step !== "approval" || !server || approved) return;
    const abort = new AbortController();
    const read = () => api.status({ expectedEpoch: epoch }, { signal: abort.signal, timeoutMilliseconds: 10_000 }).then(value => {
      if (!abort.signal.aborted && value.servers.some(item => item.id === server.id && item.link === "approved")) setApproved(true);
    }, () => { /* Read again at the next tick. */ });
    void read();
    const timer = setInterval(() => void read(), approvalPollMilliseconds);
    return () => { abort.abort(); clearInterval(timer); };
  }, [api, approved, epoch, server, step]);
  useEffect(() => {
    if (step !== "signin" || !url) return;
    const abort = new AbortController();
    setMethods(null);
    api.signInMethods({ expectedEpoch: epoch, url }, { signal: abort.signal, timeoutMilliseconds: 15_000 }).then(value => {
      if (abort.signal.aborted) return;
      const offered = value.status === "ok" ? orderedSignInMethods(value.methods) : [];
      setMethods(offered);
      if (offered.length === 0) setUseCode(true);
    }, () => { if (!abort.signal.aborted) { setMethods([]); setUseCode(true); } });
    return () => abort.abort();
  }, [api, epoch, step, url]);

  async function signInWith(method: FleetSignInMethodView) {
    if (!url) return;
    const abort = new AbortController();
    browser.current = abort;
    setBusy(true);
    setWaiting(method.id);
    setProblem(null);
    try {
      // GitHub: a code to enter at GitHub first, then the wait for it under its handle.
      let handle: string | null = null;
      if (method.id === "github") {
        const started = await api.startSignInCode({ expectedEpoch: epoch, method: method.id, url, linkId: null }, { signal: abort.signal, timeoutMilliseconds: 30_000 });
        if (abort.signal.aborted) return;
        const code = started.status === "ok" ? signInCode(started.code) : null;
        if (!code) { setProblem({ key: signInProblem(started.status) ?? serverProblem(started.status) ?? "The operation did not complete.", detail: started.message }); return; }
        setDeviceCode(code);
        handle = code.handle;
      }
      const result = await api.joinWithSignIn({ expectedEpoch: epoch, url, method: method.id, handle }, { signal: abort.signal, timeoutMilliseconds: signInTimeoutMilliseconds });
      if (abort.signal.aborted) return;
      if (result.status !== "ok" || !result.server) {
        setProblem({ key: signInProblem(result.status) ?? serverProblem(result.status) ?? "The operation did not complete.", detail: result.message });
        return;
      }
      setServer(result.server);
      setByOwnDevice(result.hasDevices);
      const status = await api.status({ expectedEpoch: epoch }, { timeoutMilliseconds: 15_000 }).catch(() => null);
      setWords(status?.words ?? null);
      setStep("space");
    } catch {
      if (!abort.signal.aborted) setProblem({ key: "The server does not answer." });
    } finally {
      if (browser.current === abort) { browser.current = null; setWaiting(null); setDeviceCode(null); setBusy(false); }
    }
  }

  function cancelSignIn() {
    browser.current?.abort();
    browser.current = null;
    setWaiting(null);
    setDeviceCode(null);
    setBusy(false);
    setProblem({ key: "The sign-in was cancelled." });
  }
  useEffect(() => {
    const abort = new AbortController();
    spaces.list({ expectedEpoch: epoch }, { signal: abort.signal }).then(value => { if (!abort.signal.aborted && value.status === "ok") setList(value.spaces); }, () => { });
    return () => abort.abort();
  }, [epoch, spaces]);

  async function signIn() {
    if (!url || !code.trim()) return;
    setBusy(true);
    setProblem(null);
    try {
      const result = await api.join({ expectedEpoch: epoch, url, token: code.trim() }, { timeoutMilliseconds: 30_000 });
      if (result.status !== "ok" || !result.server) { setProblem({ key: serverProblem(result.status) ?? "The operation did not complete.", detail: result.message }); return; }
      setServer(result.server);
      const status = await api.status({ expectedEpoch: epoch }, { timeoutMilliseconds: 15_000 }).catch(() => null);
      setWords(status?.words ?? null);
      setStep("space");
    } catch {
      setProblem({ key: "The server does not answer." });
    } finally { setBusy(false); }
  }

  async function linkSpace() {
    if (!server) return;
    setBusy(true);
    setProblem(null);
    try {
      for (const choice of selected) {
        let spaceId = choice;
        if (choice === "new") {
          const created = await spaces.create({ expectedEpoch: epoch, name: t("Work"), description: server.organization ?? server.url, icon: null, color: null, copyFrom: null,
            projectIds: null }, { timeoutMilliseconds: 15_000 });
          if (created.status !== "ok" || !created.space) { setProblem({ key: "The space could not be made.", detail: created.message }); return; }
          spaceId = created.space.id;
        }

        const linked = await api.linkSpace({ expectedEpoch: epoch, spaceId, linkId: server.id }, { timeoutMilliseconds: 15_000 });
        if (linked.status !== "ok") { setProblem({ key: serverProblem(linked.status) ?? "The operation did not complete.", detail: linked.message }); return; }
      }
      setStep("approval");
    } catch {
      setProblem({ key: "The operation did not complete." });
    } finally { setBusy(false); }
  }

  return <Dialog isOpen className={`join-organization-dialog join-organization-${kind}`} title={t(wording.title)} canOutsideClickClose={false} canEscapeKeyClose={!busy}
    onClose={() => { if (!busy) onClose(!!server); }}>
    <DialogBody>
      <ol className="join-organization-steps" aria-label={t("Steps")}>
        {joinSteps.map((value, index) => <li key={value} data-state={value === step ? "current" : joinSteps.indexOf(step) > index ? "done" : "waiting"}>
          <span className="join-organization-step-mark">{joinSteps.indexOf(step) > index ? <AppIcon name="checked" size={13} /> : index + 1}</span>
          {t(value === "address" ? "Server" : value === "signin" ? "Sign in" : value === "space" ? "Space" : "Approval")}
        </li>)}
      </ol>
      {step === "address" && <label className="add-machine-field"><span>{t(wording.address)}</span>
        <InputGroup autoFocus fill value={address} placeholder={kind === "own" ? "http://server.local:2586" : "https://fleet.example.com"} aria-label={t(wording.address)}
          onChange={event => setAddress(event.currentTarget.value)} onKeyDown={event => { if (event.key === "Enter" && url) setStep("signin"); }} />
        <small className="muted-text">{t(wording.addressHelp)}</small>
      </label>}
      {step === "address" && address.trim() && !url && <Callout compact intent="warning">{t("This is not the address of a server.")}</Callout>}
      {step === "signin" && !useCode && <>
        <p className="muted-text">{t("Sign in to {server}. Your browser opens; this computer then waits for its approval.", { server: url })}</p>
        {methods === null ? <div className="join-organization-methods muted-text"><ActivitySpinner size={14} /> {t("Checking how this server signs people in…")}</div>
          : <div className="join-organization-methods" role="group" aria-label={t("Sign in with")}>
            {methods.map((method, index) => <Button key={method.id} fill alignText="start" data-method={method.id} disabled={busy}
              intent={index === 0 && !method.breakGlass && !hasBrandMark(method.id) ? "primary" : undefined} variant={method.breakGlass ? "minimal" : undefined}
              size={method.breakGlass ? "small" : undefined}
              icon={waiting === method.id ? <ActivitySpinner size={16} /> : <SignInMethodIcon method={method.id} size={method.breakGlass ? 14 : 16} />}
              text={method.breakGlass ? t("Owner's passkey") : t("Continue with {method}", { method: method.id === "passkey" ? t("Passkey") : method.name })}
              onClick={() => void signInWith(method)} />)}
          </div>}
        {waiting && (deviceCode ? <SignInCode epoch={epoch} code={deviceCode} api={api} />
          : <Callout compact intent="primary" role="status" icon={<AppIcon name="shield" size={16} />}>{t("Finish signing in in your browser…")}</Callout>)}
      </>}
      {step === "signin" && useCode && <>
        <p className="muted-text">{t(wording.code, { server: url })}</p>
        <label className="add-machine-field"><span>{t("Enrollment code")}</span>
          <InputGroup autoFocus fill value={code} disabled={busy} aria-label={t("Enrollment code")} onChange={event => setCode(event.currentTarget.value)}
            onKeyDown={event => { if (event.key === "Enter") void signIn(); }} />
          <small className="muted-text">{t(wording.codeHelp)}</small></label>
      </>}
      {step === "space" && server && <>
        <p className="muted-text">{t(wording.spaces, { organization: server.organization ?? server.url })}</p>
        {kind === "own"
          // Their own server shows their own sessions: each space of theirs, all of them at first.
          ? <div className="join-organization-spaces" role="group" aria-label={t("Spaces")}>
            {list.map(item => <Checkbox key={item.id} checked={selected.includes(item.id)} label={item.name}
              onChange={event => { const on = event.currentTarget.checked; setChosen(on ? [...selected.filter(id => id !== item.id), item.id] : selected.filter(id => id !== item.id)); }} />)}
          </div>
          : <RadioGroup selectedValue={selected[0] ?? "new"} onChange={event => setChosen([event.currentTarget.value])} className="join-organization-spaces">
            <Radio value="new" labelElement={<span><strong>{t("A new Work space")}</strong> <Tag minimal round intent="primary">{t("Recommended")}</Tag></span>} />
            {list.map(item => <Radio key={item.id} value={item.id} label={item.name} />)}
          </RadioGroup>}
      </>}
      {step === "approval" && server && approved && <Callout compact intent="success" role="status" icon={<AppIcon name="checked" size={16} />}>
        {t("This computer is approved: it is one of yours on {organization}.", { organization: server.organization ?? server.url })}
      </Callout>}
      {step === "approval" && server && !approved && <Callout compact intent="primary" role="status" icon={<AppIcon name="shield" size={16} />}>
        {byOwnDevice || kind === "own" ? t("Another of your computers approves this one. It shows these words beside it:")
          : t("An administrator of {organization} approves this computer. They see these words beside it:", { organization: server.organization ?? server.url })}
        {words && <div className="pair-machine-words add-machine-words">{keyWords(words).map((word, index) => <Tag key={`${index}:${word}`} minimal>{word}</Tag>)}</div>}
        <div className="muted-text">{t("Settings > Fleet > Server shows when it is done.")}</div>
      </Callout>}
      {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && problem.key !== notSetUpProblem && <div className="muted-text">{problem.detail}</div>}
        {/* A setup code typed here: Create my server… > It runs already takes it, with this address. */}
        {problem.key === notSetUpProblem && onSetUp && url && <div className="join-organization-setup">
          <Button size="small" intent="primary" icon={<AppIcon name="server" size={14} />} text={t("Set it up…")} onClick={() => onSetUp(url)} /></div>}
      </Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      {step === "address" && <><Button text={t("Cancel")} onClick={() => onClose(false)} />
        <Button intent="primary" disabled={!url} text={t("Next")} onClick={() => setStep("signin")} /></>}
      {step === "signin" && <><Button disabled={busy} text={t("Back")} onClick={() => { setStep("address"); setUseCode(false); }} />
        {waiting && <Button text={t("Cancel")} onClick={cancelSignIn} />}
        {useCode && <Button intent="primary" disabled={busy || !code.trim()} icon={busy ? <ActivitySpinner size={14} /> : undefined} text={t("Sign in")} onClick={() => void signIn()} />}</>}
      {step === "space" && selected.length === 0 && <Button disabled={busy} text={t("Skip")} onClick={() => setStep("approval")} />}
      {step === "space" && <Button intent="primary" disabled={busy || selected.length === 0} icon={busy ? <ActivitySpinner size={14} /> : undefined} text={t(wording.link)}
        onClick={() => void linkSpace()} />}
      {step === "approval" && <Button intent="primary" text={t("Done")} onClick={() => onClose(true)} />}
    </>}>
      {step === "address" && onRecover && <Button variant="minimal" size="small" icon={<AppIcon name="shield" size={14} />} text={t("Recover with the recovery key…")}
        onClick={onRecover} />}
      {step === "signin" && !busy && methods && methods.length > 0 && <Button variant="minimal" size="small" className="join-organization-switch"
        text={t(useCode ? "Sign in with an account instead" : "Use an enrollment code instead")} onClick={() => { setUseCode(value => !value); setProblem(null); }} />}
    </DialogFooter>
  </Dialog>;
}
