import { useEffect, useState } from "react";
import { Button, Callout, Dialog, DialogBody, DialogFooter, InputGroup, Radio, RadioGroup, Tag } from "@blueprintjs/core";
import { fleetServer, spaces as spacesApi, type FleetServerView, type SpaceItem } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { keyWords, serverProblem } from "./fleetServer";
import { joinSteps, organizationAddressOf, type JoinStep } from "./joinOrganization";

/** The calls of the dialog. */
export type JoinOrganizationApi = Pick<typeof fleetServer, "join" | "linkSpace" | "status">;

/** The calls of the dialog on the spaces of this computer. */
export type JoinOrganizationSpacesApi = Pick<typeof spacesApi, "list" | "create">;

/**
 * Join an organization: the address of the company's server, then signing in to it — with the one-time enrollment code
 * an administrator gives, the way the server lets a computer in today — then the space whose sessions it sees (a new
 * Work space by default), and the approval this computer waits for, with the six words of its key. A person whose
 * computers are all lost starts from here too: `onRecover` goes to the recovery key instead.
 */
export function JoinOrganizationDialog({ epoch, api = fleetServer, spaces = spacesApi, onRecover, onClose }: {
  epoch: string; api?: JoinOrganizationApi; spaces?: JoinOrganizationSpacesApi; onRecover?: () => void; onClose: (joined: boolean) => void;
}) {
  const { t } = useShellLanguage();
  const [step, setStep] = useState<JoinStep>("address");
  const [address, setAddress] = useState("");
  const [code, setCode] = useState("");
  const [server, setServer] = useState<FleetServerView | null>(null);
  const [words, setWords] = useState<string | null>(null);
  const [list, setList] = useState<readonly SpaceItem[]>([]);
  const [space, setSpace] = useState<string>("new");
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const url = organizationAddressOf(address);
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
      let spaceId = space;
      if (space === "new") {
        const created = await spaces.create({ expectedEpoch: epoch, name: t("Work"), description: server.organization ?? server.url, icon: null, color: null, copyFrom: null,
          projectIds: null }, { timeoutMilliseconds: 15_000 });
        if (created.status !== "ok" || !created.space) { setProblem({ key: "The space could not be made.", detail: created.message }); return; }
        spaceId = created.space.id;
      }

      const linked = await api.linkSpace({ expectedEpoch: epoch, spaceId, linkId: server.id }, { timeoutMilliseconds: 15_000 });
      if (linked.status !== "ok") { setProblem({ key: serverProblem(linked.status) ?? "The operation did not complete.", detail: linked.message }); return; }
      setStep("approval");
    } catch {
      setProblem({ key: "The operation did not complete." });
    } finally { setBusy(false); }
  }

  return <Dialog isOpen className="join-organization-dialog" title={t("Join an organization")} canOutsideClickClose={false} canEscapeKeyClose={!busy}
    onClose={() => { if (!busy) onClose(!!server); }}>
    <DialogBody>
      <ol className="join-organization-steps" aria-label={t("Steps")}>
        {joinSteps.map((value, index) => <li key={value} data-state={value === step ? "current" : joinSteps.indexOf(step) > index ? "done" : "waiting"}>
          <span className="join-organization-step-mark">{joinSteps.indexOf(step) > index ? <AppIcon name="checked" size={13} /> : index + 1}</span>
          {t(value === "address" ? "Server" : value === "signin" ? "Sign in" : value === "space" ? "Space" : "Approval")}
        </li>)}
      </ol>
      {step === "address" && <label className="add-machine-field"><span>{t("Address of your organization's server")}</span>
        <InputGroup autoFocus fill value={address} placeholder="https://fleet.example.com" aria-label={t("Address of your organization's server")}
          onChange={event => setAddress(event.currentTarget.value)} onKeyDown={event => { if (event.key === "Enter" && url) setStep("signin"); }} />
        <small className="muted-text">{t("Your administrator gives it, or your company's configuration sets it.")}</small>
      </label>}
      {step === "address" && address.trim() && !url && <Callout compact intent="warning">{t("This is not the address of a server.")}</Callout>}
      {step === "signin" && <>
        <p className="muted-text">{t("Sign in to {server} with the one-time enrollment code your administrator made for you. This computer then waits for their approval.", { server: url })}</p>
        <label className="add-machine-field"><span>{t("Enrollment code")}</span>
          <InputGroup autoFocus fill value={code} disabled={busy} aria-label={t("Enrollment code")} onChange={event => setCode(event.currentTarget.value)}
            onKeyDown={event => { if (event.key === "Enter") void signIn(); }} /></label>
      </>}
      {step === "space" && server && <>
        <p className="muted-text">{t("Which space works with {organization}? Its sessions are published to the server: their states and counts, never their content.",
          { organization: server.organization ?? server.url })}</p>
        <RadioGroup selectedValue={space} onChange={event => setSpace(event.currentTarget.value)} className="join-organization-spaces">
          <Radio value="new" labelElement={<span><strong>{t("A new Work space")}</strong> <Tag minimal round intent="primary">{t("Recommended")}</Tag></span>} />
          {list.map(item => <Radio key={item.id} value={item.id} label={item.name} />)}
        </RadioGroup>
      </>}
      {step === "approval" && server && <Callout compact intent="primary" role="status" icon={<AppIcon name="shield" size={16} />}>
        {t("An administrator of {organization} approves this computer. They see these words beside it:", { organization: server.organization ?? server.url })}
        {words && <div className="pair-machine-words add-machine-words">{keyWords(words).map((word, index) => <Tag key={`${index}:${word}`} minimal>{word}</Tag>)}</div>}
        <div className="muted-text">{t("Settings > Fleet > Server shows when it is done.")}</div>
      </Callout>}
      {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      {step === "address" && <><Button text={t("Cancel")} onClick={() => onClose(false)} />
        <Button intent="primary" disabled={!url} text={t("Next")} onClick={() => setStep("signin")} /></>}
      {step === "signin" && <><Button disabled={busy} text={t("Back")} onClick={() => setStep("address")} />
        <Button intent="primary" disabled={busy || !code.trim()} icon={busy ? <ActivitySpinner size={14} /> : undefined} text={t("Sign in")} onClick={() => void signIn()} /></>}
      {step === "space" && <Button intent="primary" disabled={busy} icon={busy ? <ActivitySpinner size={14} /> : undefined} text={t("Link the space")} onClick={() => void linkSpace()} />}
      {step === "approval" && <Button intent="primary" text={t("Done")} onClick={() => onClose(true)} />}
    </>}>
      {step === "address" && onRecover && <Button variant="minimal" size="small" icon={<AppIcon name="shield" size={14} />} text={t("Recover with the recovery key…")}
        onClick={onRecover} />}
    </DialogFooter>
  </Dialog>;
}
