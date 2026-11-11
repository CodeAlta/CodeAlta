import { useCallback, useEffect, useState } from "react";
import { Button, Callout, Checkbox, Dialog, DialogBody, DialogFooter, HTMLSelect, InputGroup, Tag } from "@blueprintjs/core";
import { machines, type MachineProviderOfferView, type MachineProviderSignInView, type MachineProvidersView, type MachineProviderStatusView, type MachineSignInItemView,
  type MachineView } from "#neoastra";
import { ProviderIcon } from "../ProviderIcon";
import { ProviderPicker, type ProviderPicked } from "./ProviderPicker";
import { ProgramProgress } from "./ProgramProgress";
import { programAction, programLabel } from "./providerOffers";
import { SignInDialog, type MachineSignInsApi } from "./MachineSignIns";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { Copyable } from "../ProviderAccount";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { requestLimit } from "../providerForm";
import { managementProblem, newProviderKey, providerFix, providerProblem, providerState, providerStateIntent, providerStateLabel } from "./machineManagement";
import { SignInPageLink, signInPageOpener } from "./SignInPageLink";

/** The calls of the providers of a machine. */
export type MachineProvidersApi = Pick<typeof machines, "providers" | "setProvider" | "providerSignIn" | "providerSignInWait" | "providerOffers" | "addProvider" | "updateProvider"
  | "providerOperationWait"> & MachineSignInsApi & Partial<Pick<typeof machines, "openSignInPage">>;

type Problem = { key: MessageKey; detail?: string | null } | null;

/**
 * The providers of a machine this computer manages: each with whether it is ready and why not (a missing key, a sign-in),
 * Add a provider…, its key typed here and sent over the end-to-end channel to the secrets store of the machine (never in clear
 * in its configuration), and Sign in… for a provider that signs in with a code, run on the machine while the code shows here.
 */
export function MachineProviders({ epoch, machine, api = machines }: { epoch: string; machine: MachineView; api?: MachineProvidersApi }) {
  const { t } = useShellLanguage();
  const [view, setView] = useState<MachineProvidersView | null>(null);
  const [problem, setProblem] = useState<Problem>(null);
  const [generation, setGeneration] = useState(0);
  // The provider being added (null key) or changed; undefined while no dialog is open.
  const [editing, setEditing] = useState<MachineProviderStatusView | null | undefined>(undefined);
  const [signingIn, setSigningIn] = useState<string | null>(null);
  // The sign-in a provider keeps on the machine (Claude Code, Codex, an ACP agent), started from its row.
  const [machineSignIn, setMachineSignIn] = useState<MachineSignInItemView | null>(null);
  const [signIns, setSignIns] = useState<readonly MachineSignInItemView[]>([]);
  const [picking, setPicking] = useState(false);
  // How to sign in a provider that signs in on the machine itself (Claude Code, with Anthropic's own sign-in), with Check again.
  const [machineHelp, setMachineHelp] = useState<{ item: MachineSignInItemView; provider: MachineProviderStatusView | null } | null>(null);
  // The type whose key the form takes, chosen in the picker.
  const [keyFor, setKeyFor] = useState<MachineProviderOfferView | null>(null);
  // The provider whose program the machine installs or updates, and what it says of it.
  const [installing, setInstalling] = useState<{ key: string; message: string | null; fraction: number | null } | null>(null);
  // Why an installation or an update failed: kept apart from the problem of the list, which its next read clears.
  const [actionProblem, setActionProblem] = useState<Problem>(null);
  const reload = useCallback(() => setGeneration(value => value + 1), []);
  useEffect(() => {
    const abort = new AbortController();
    api.providers({ expectedEpoch: epoch, machineId: machine.id }, { signal: abort.signal, timeoutMilliseconds: 30_000 }).then(value => {
      if (abort.signal.aborted) return;
      const failed = managementProblem(value.status);
      if (failed) setProblem({ key: failed, detail: value.message }); else { setProblem(null); setView(value); }
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The machine does not answer: it is offline." }); });
    // The sign-ins of the providers, so that a row signs its provider in where it is.
    api.signIns({ expectedEpoch: epoch, machineId: machine.id }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
      .then(value => { if (!abort.signal.aborted && value.status === "ok") setSignIns(value.items.filter(item => item.kind === "provider")); }, () => undefined);
    return () => abort.abort();
  }, [api, epoch, machine.id, generation]);

  // What the picker chose: a type whose key is typed next, or a provider added, whose sign-in starts at once when it has one.
  async function picked(choice: ProviderPicked | null) {
    setPicking(false);
    if (!choice) return;
    if ("keyFor" in choice) { setKeyFor(choice.keyFor); setEditing(null); return; }
    reload();
    if (choice.offer.signIn) { setSigningIn(choice.added); return; }
    try {
      const listed = await api.signIns({ expectedEpoch: epoch, machineId: machine.id }, { timeoutMilliseconds: 30_000 });
      const item = listed.items.find(value => value.kind === "provider" && value.key === choice.added);
      if (item && item.ways.length > 0 && item.state !== "signed_in") setMachineSignIn(item);
      else if (item && item.ways.length === 0 && item.state !== "signed_in" && item.detail) setMachineHelp({ item, provider: null });
    } catch { /* The row offers it. */ }
  }

  // Installs or updates the program a provider runs, and follows the machine until it ends.
  async function install(provider: MachineProviderStatusView) {
    setInstalling({ key: provider.key, message: null, fraction: null });
    setActionProblem(null);
    try {
      let state = await api.updateProvider({ expectedEpoch: epoch, machineId: machine.id, key: provider.key }, { timeoutMilliseconds: 60_000 });
      while (state.status === "ok" && state.state === "running" && state.id) {
        setInstalling({ key: provider.key, message: state.message ?? null, fraction: state.fraction ?? null });
        state = await api.providerOperationWait({ expectedEpoch: epoch, machineId: machine.id, id: state.id }, { timeoutMilliseconds: 40_000 });
      }

      if (state.status !== "ok") setActionProblem({ key: managementProblem(state.status) ?? "The machine could not do it.", detail: state.message });
      else if (state.state !== "completed") setActionProblem({ key: "The machine could not do it.", detail: state.message });
    } catch {
      setActionProblem({ key: "The machine does not answer: it is offline." });
    } finally { setInstalling(null); reload(); }
  }
  // A provider that prepares is read again a moment later, until it is ready or says why not.
  useEffect(() => {
    if (!view?.providers.some(provider => providerState(provider) === "preparing")) return;
    const timer = setTimeout(reload, 2500);
    return () => clearTimeout(timer);
  }, [reload, view]);

  const providers = view?.providers ?? [];
  // A key or a sign-in is a credential of the machine: only a computer paired with it gives one, never one that reaches it
  // through a server. The page says so before the click.
  const paired = machine.paired;
  return <section className="machine-section machine-providers-section" aria-label={t("Providers")}>
    <div className="machine-section-head">
      <h2>{t("Providers")}</h2>
      <Button size="small" icon={<AppIcon name="plus" size={14} />} disabled={!view || !paired} onClick={() => setPicking(true)}>{t("Add a provider…")}</Button>
    </div>
    <p className="muted-text">{t("The providers the sessions of {name} run on.", { name: machine.name })}</p>
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {actionProblem && <Callout compact intent="warning" role="alert" className="machine-provider-action-problem">{t(actionProblem.key)}
      {actionProblem.detail && <div className="muted-text">{actionProblem.detail}</div>}</Callout>}
    {view && !paired && <Callout compact intent="primary" className="machine-paired-only">{t("This computer reaches {name} through your server: it sees its providers, but only a computer paired with {name} adds them, gives their keys and signs them in. To do it from this one, run altanode pair --manage on {name}, then paste the link in Pair a machine… here.", { name: machine.name })}</Callout>}
    {view?.configurationError && <Callout compact intent="warning" role="alert" className="machine-configuration-error">
      {t("The configuration file of {name} has an error since it was last changed: its providers run as before until it is fixed.", { name: machine.name })}
      <div className="muted-text">{view.configurationError}</div></Callout>}
    {view && providers.length === 0 && <Callout compact intent="warning">{t("No provider is set up on {name}: its sessions cannot start.", { name: machine.name })}</Callout>}
    {providers.length > 0 && <ul className="machine-providers">{providers.map(provider => {
      const state = providerState(provider);
      const why = providerProblem(provider);
      const fix = providerFix(provider);
      const program = programLabel(provider.program);
      const action = programAction(provider.program);
      // A provider signed in on the machine itself (its program's login, an ACP agent's key) signs in from its row too.
      const signInItem = state !== "ready" && state !== "disabled" && (fix === null || fix === "sign_in" && !provider.signIn) ? signIns.find(item => item.key === provider.key && item.ways.length > 0 && item.state !== "signed_in") : undefined;
      // One that signs in on the machine itself says how, with Check again.
      const helpItem = state !== "ready" && state !== "disabled" && !signInItem ? signIns.find(item => item.key === provider.key && item.ways.length === 0 && item.state !== "signed_in" && item.detail) : undefined;
      return <li key={provider.key} data-state={state} data-provider={provider.key}>
        <span className="machine-provider-icon">{state === "preparing" || installing?.key === provider.key ? <ActivitySpinner size={14} />
          : <AppIcon name={state === "ready" ? "checked" : state === "disabled" ? "minus" : "warning"} size={14} className={state === "ready" ? "machine-ready" : undefined} />}</span>
        <span className="machine-provider-main">
          <span className="machine-provider-title"><ProviderIcon providerKey={provider.key} known={{ type: provider.type, name: provider.name, iconData: provider.icon }} size={16} />
            <strong>{provider.name}</strong>
            <Tag minimal round intent={providerStateIntent(state)}>{t(providerStateLabel(state))}</Tag>
            {provider.isDefault && <Tag minimal round>{t("Default")}</Tag>}</span>
          <small className="muted-text">{[provider.type, provider.model, provider.keySecret && t("key in the secret {name}", { name: provider.keySecret })].filter(Boolean).join(" · ")}</small>
          {program && <small className="muted-text machine-provider-program">{t(program.key, program.values)}</small>}
          {installing?.key === provider.key && <ProgramProgress message={installing.message} fraction={installing.fraction} />}
          {why && <small className="machine-provider-problem">{t(why.key, why.values)}</small>}
        </span>
        <span className="machine-provider-actions">
          {action && <Button size="small" intent="primary" icon={<AppIcon name="download" size={13} />} disabled={!paired || installing !== null}
            text={action.kind === "install" ? t("Install") : t("Update to {version}", { version: action.version })} onClick={() => void install(provider)} />}
          {signInItem && !action && <Button size="small" intent="primary" icon={<AppIcon name="shield" size={13} />} text={t("Sign in…")}
            disabled={!paired} onClick={() => setMachineSignIn(signInItem)} />}
          {helpItem && !action && <Button size="small" intent="primary" icon={<AppIcon name="shield" size={13} />} text={t("How to sign in…")}
            onClick={() => setMachineHelp({ item: helpItem, provider })} />}
          {fix === "sign_in" && state !== "ready" && <Button size="small" intent="primary" icon={<AppIcon name="shield" size={13} />} text={t("Sign in…")}
            disabled={!paired} onClick={() => setSigningIn(provider.key)} />}
          {fix === "key" && <Button size="small" intent={state === "not_ready" ? "primary" : undefined} text={t(state === "not_ready" ? "Add its key…" : "Edit…")}
            disabled={!paired} onClick={() => setEditing(provider)} />}
        </span>
      </li>;
    })}</ul>}
    {picking && view && <ProviderPicker epoch={epoch} machine={machine} api={api} types={view.types} onClose={choice => void picked(choice)} />}
    {editing !== undefined && view && <ProviderDialog epoch={epoch} machine={machine} api={api} view={view} provider={editing} initialType={editing === null ? keyFor?.type : undefined}
      onClose={(saved, signIn) => { setEditing(undefined); setKeyFor(null); if (saved) reload(); if (signIn) setSigningIn(signIn); }} />}
    {machineSignIn && <SignInDialog epoch={epoch} machine={machine} api={api} item={machineSignIn} onClose={() => { setMachineSignIn(null); reload(); }} />}
    {machineHelp && <MachineSignInHelp epoch={epoch} machine={machine} api={api} item={machineHelp.item}
      provider={machineHelp.provider ?? providers.find(value => value.key === machineHelp.item.key) ?? null} paired={paired}
      onClose={() => { setMachineHelp(null); reload(); }} />}
    {signingIn && <ProviderSignInDialog epoch={epoch} machine={machine} api={api} providerKey={signingIn}
      name={providers.find(provider => provider.key === signingIn)?.name ?? signingIn} onClose={() => { setSigningIn(null); reload(); }} />}
  </section>;
}

/**
 * How a provider that signs in on the machine itself is signed in (Claude Code, with Anthropic's own sign-in, which CodeAlta takes no
 * part in): the steps the machine gives, the command to copy, and Check again, which has the machine start the provider again so that
 * it reads the sign-in made meanwhile.
 */
function MachineSignInHelp({ epoch, machine, api, item, provider, paired, onClose }: {
  epoch: string; machine: MachineView; api: MachineProvidersApi; item: MachineSignInItemView; provider: MachineProviderStatusView | null; paired: boolean; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [checking, setChecking] = useState(false);
  const [result, setResult] = useState<{ ready: boolean; detail: string | null } | null>(null);
  async function check() {
    setChecking(true);
    setResult(null);
    try {
      await api.setProvider({ expectedEpoch: epoch, machineId: machine.id, key: item.key, type: null, displayName: null, model: null, apiUrl: null, apiKey: null,
        enabled: true, makeDefault: provider?.isDefault ?? false }, { timeoutMilliseconds: 60_000 });
      // The machine probes it again: its state is read until it prepared.
      for (let attempt = 0; attempt < 30; attempt++) {
        const listed = await api.providers({ expectedEpoch: epoch, machineId: machine.id }, { timeoutMilliseconds: 30_000 });
        const now = listed.providers.find(value => value.key === item.key);
        if (now && now.state !== "preparing") { setResult({ ready: now.state === "ready", detail: now.detail ?? null }); return; }
        await new Promise(resolve => setTimeout(resolve, 1000));
      }
      setResult({ ready: false, detail: null });
    } catch {
      setResult({ ready: false, detail: null });
    } finally { setChecking(false); }
  }
  return <Dialog isOpen className="machine-provider-signin-dialog machine-signin-help" title={t("Sign {name} in on {machine}", { name: item.title, machine: machine.name })} onClose={onClose}>
    <DialogBody>
      <p>{item.detail}</p>
      <Copyable code value={`altanode providers sign-in ${item.key}`} label={t("Copy the command")} />
      {result?.ready && <Callout compact intent="success" role="status" icon={<AppIcon name="checked" size={16} />}>{t("{name} is signed in on {machine}.", { name: item.title, machine: machine.name })}</Callout>}
      {result && !result.ready && <Callout compact intent="warning" role="alert">{t("{name} is not signed in on {machine} yet.", { name: item.title, machine: machine.name })}
        {result.detail && <div className="muted-text">{result.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button intent={result?.ready ? undefined : "primary"} disabled={checking || !paired} icon={checking ? <ActivitySpinner size={14} /> : <AppIcon name="refresh" size={14} />}
        text={t("Check again")} onClick={() => void check()} />
      <Button intent={result?.ready ? "primary" : undefined} text={t(result?.ready ? "Done" : "Close")} onClick={onClose} />
    </>} />
  </Dialog>;
}

/**
 * A provider to add or change on a machine: its type (when new), its name, its key (typed here, kept in the secrets of the
 * machine; a provider that has one keeps it while the field stays empty), its model and its endpoint. A provider that signs in
 * with a code takes no key: it is added, then its sign-in starts.
 */
function ProviderDialog({ epoch, machine, api, view, provider, initialType, onClose }: {
  epoch: string; machine: MachineView; api: MachineProvidersApi; view: MachineProvidersView; provider: MachineProviderStatusView | null; initialType?: string;
  onClose: (saved: boolean, signIn?: string) => void;
}) {
  const { t } = useShellLanguage();
  const [type, setType] = useState(provider?.type ?? initialType ?? view.types[0]?.type ?? "");
  const [name, setName] = useState(provider?.name ?? "");
  const [apiKey, setApiKey] = useState("");
  const [model, setModel] = useState(provider?.model ?? "");
  const [apiUrl, setApiUrl] = useState(provider?.apiUrl ?? "");
  const [requests, setRequests] = useState(provider?.maxConcurrentRequests?.toString() ?? "");
  const [makeDefault, setMakeDefault] = useState(provider?.isDefault ?? view.providers.length === 0);
  const [saving, setSaving] = useState(false);
  const [problem, setProblem] = useState<Problem>(null);
  const chosen = view.types.find(value => value.type === type);
  const takesKey = provider ? provider.credential === "key" : chosen?.credential === "key";
  const requestsValid = !requests.trim() || requestLimit(requests) !== null;
  const ready = !!type && requestsValid && (!takesKey || !!apiKey.trim() || !!provider?.keySecret && provider.reason !== "secret_missing");
  async function save() {
    if (!ready) return;
    setSaving(true);
    setProblem(null);
    try {
      const key = provider?.key ?? newProviderKey(type, view.providers.map(value => value.key));
      const result = await api.setProvider({ expectedEpoch: epoch, machineId: machine.id, key, type: provider ? null : type, displayName: name.trim() || (provider ? "" : null),
        model: model.trim() || (provider?.model ? "" : null), apiUrl: apiUrl.trim() || (provider?.apiUrl ? "" : null), apiKey: takesKey ? apiKey.trim() || null : null,
        // Empty removes the limit a provider has: 0 says so to the machine.
        maxConcurrentRequests: requestLimit(requests) ?? (provider?.maxConcurrentRequests ? 0 : null),
        enabled: true, makeDefault }, { timeoutMilliseconds: 60_000 });
      const failed = managementProblem(result.status);
      if (failed) { setProblem({ key: failed, detail: result.message }); return; }
      onClose(true, !takesKey && chosen?.signIn && !provider ? key : undefined);
    } catch {
      setProblem({ key: "The machine does not answer: it is offline." });
    } finally { setSaving(false); }
  }
  return <Dialog isOpen className="machine-provider-dialog" title={t(provider || initialType ? "{name} on {machine}" : "Add a provider to {machine}",
    { name: provider?.name ?? chosen?.name ?? "", machine: machine.name })}
    canOutsideClickClose={false} onClose={() => { if (!saving) onClose(false); }}>
    <DialogBody>
      <form className="fleet-server-form" onSubmit={event => { event.preventDefault(); void save(); }}>
        {!provider && !initialType && <label><span>{t("Provider")}</span>
          <HTMLSelect fill value={type} disabled={saving} aria-label={t("Provider")} onChange={event => setType(event.currentTarget.value)}
            options={view.types.map(value => ({ value: value.type, label: value.name }))} /></label>}
        <label><span>{t("Name")}</span>
          <InputGroup value={name} disabled={saving} placeholder={chosen?.name ?? provider?.name} aria-label={t("Name")} onChange={event => setName(event.target.value)} /></label>
        {takesKey && <label><span>{t("API key")}</span>
          <InputGroup type="password" value={apiKey} disabled={saving} autoComplete="off" spellCheck={false} aria-label={t("API key")}
            placeholder={provider?.keySecret && provider.reason !== "secret_missing" ? t("Kept in the secret {name}", { name: provider.keySecret }) : undefined}
            onChange={event => setApiKey(event.target.value)} /></label>}
        {!takesKey && chosen?.signIn && <p className="muted-text">{t("Once added, its sign-in code shows here.")}</p>}
        <label><span>{t("Model")}</span>
          <InputGroup value={model} disabled={saving} placeholder={t("The default of the provider")} aria-label={t("Model")} onChange={event => setModel(event.target.value)} /></label>
        {takesKey && <label><span>{t("Endpoint")}</span>
          <InputGroup value={apiUrl} disabled={saving} placeholder={t("The default of the provider")} aria-label={t("Endpoint")} onChange={event => setApiUrl(event.target.value)} /></label>}
        <label><span>{t("Requests at once")}</span>
          <InputGroup value={requests} disabled={saving} inputMode="numeric" spellCheck={false} placeholder={t("No limit")} aria-label={t("Requests at once")}
            intent={requestsValid ? "none" : "danger"} onChange={event => setRequests(event.target.value)} /></label>
        <Checkbox checked={makeDefault} disabled={saving} label={t("Sessions that name no provider run on it")} onChange={event => setMakeDefault(event.currentTarget.checked)} />
        <button type="submit" hidden />
      </form>
      {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={saving} text={t("Cancel")} onClick={() => onClose(false)} />
      <Button intent="primary" disabled={!ready || saving} icon={saving ? <ActivitySpinner size={14} /> : undefined} text={t(provider ? "Save" : "Add")} onClick={() => void save()} />
    </>} />
  </Dialog>;
}

/**
 * The device-code sign-in of a provider of a machine: the machine starts it and gives its page and code, shown here with
 * Copy; the person enters the code in their browser, and the dialog follows the machine until it is signed in.
 */
function ProviderSignInDialog({ epoch, machine, api, providerKey, name, onClose }: {
  epoch: string; machine: MachineView; api: MachineProvidersApi; providerKey: string; name: string; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const [prompt, setPrompt] = useState<MachineProviderSignInView | null>(null);
  const [state, setState] = useState<"starting" | "waiting" | "completed" | "failed">("starting");
  const [account, setAccount] = useState<string | null>(null);
  const [problem, setProblem] = useState<Problem>(null);
  const open = signInPageOpener(epoch, api.openSignInPage ? { openSignInPage: api.openSignInPage } : undefined);
  useEffect(() => {
    const abort = new AbortController();
    void (async () => {
      try {
        const started = await api.providerSignIn({ expectedEpoch: epoch, machineId: machine.id, key: providerKey }, { signal: abort.signal, timeoutMilliseconds: 90_000 });
        if (abort.signal.aborted) return;
        if (started.status !== "ok") { setState("failed"); setProblem({ key: managementProblem(started.status) ?? "The sign-in could not start on the machine.", detail: started.message }); return; }
        setPrompt(started);
        setState("waiting");
        // The machine waits for the person; the page asks it again until the sign-in ends or the dialog closes.
        while (!abort.signal.aborted) {
          const waited = await api.providerSignInWait({ expectedEpoch: epoch, machineId: machine.id, key: providerKey }, { signal: abort.signal, timeoutMilliseconds: 40_000 });
          if (abort.signal.aborted) return;
          if (waited.status === "completed") { setAccount(waited.account ?? null); setState("completed"); return; }
          if (waited.status !== "waiting") {
            setState("failed");
            setProblem({ key: waited.code === "timeout" ? "The code expired before it was entered." : managementProblem(waited.status === "failed" ? "provider.sign_in_failed" : waited.status) ?? "The sign-in could not start on the machine." });
            return;
          }
        }
      } catch {
        if (!abort.signal.aborted) { setState("failed"); setProblem({ key: "The machine does not answer: it is offline." }); }
      }
    })();
    return () => abort.abort();
  }, [api, epoch, machine.id, providerKey]);
  return <Dialog isOpen className="machine-provider-signin-dialog" title={t("Sign {name} in on {machine}", { name, machine: machine.name })} onClose={onClose}>
    <DialogBody>
      {state === "starting" && <p className="muted-text"><ActivitySpinner size={14} /> {t("{machine} starts the sign-in…", { machine: machine.name })}</p>}
      {state === "waiting" && prompt && <Callout compact intent="primary" role="status" icon={<ActivitySpinner size={16} />} className="machine-provider-code">
        <p>{t("Open this page in your browser and enter the code:")}</p>
        {prompt.url && <SignInPageLink url={prompt.url} open={open} />}
        {prompt.userCode && <Copyable code value={prompt.userCode} label={t("Copy code")} />}
      </Callout>}
      {state === "completed" && <Callout compact intent="success" role="status" icon={<AppIcon name="checked" size={16} />}>
        {account ? t("{name} is signed in on {machine} as {account}.", { name, machine: machine.name, account }) : t("{name} is signed in on {machine}.", { name, machine: machine.name })}
      </Callout>}
      {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<Button intent={state === "completed" ? "primary" : undefined} text={t(state === "completed" || state === "failed" ? "Done" : "Cancel")} onClick={onClose} />} />
  </Dialog>;
}
