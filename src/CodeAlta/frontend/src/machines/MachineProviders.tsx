import { useCallback, useEffect, useState } from "react";
import { Button, Callout, Checkbox, Dialog, DialogBody, DialogFooter, HTMLSelect, InputGroup, Tag } from "@blueprintjs/core";
import { machines, type MachineProviderSignInView, type MachineProvidersView, type MachineProviderStatusView, type MachineView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { Copyable } from "../ProviderAccount";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { managementProblem, newProviderKey, providerFix, providerProblem, providerState, providerStateIntent, providerStateLabel } from "./machineManagement";

/** The calls of the providers of a machine. */
export type MachineProvidersApi = Pick<typeof machines, "providers" | "setProvider" | "providerSignIn" | "providerSignInWait">;

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
  const reload = useCallback(() => setGeneration(value => value + 1), []);
  useEffect(() => {
    const abort = new AbortController();
    api.providers({ expectedEpoch: epoch, machineId: machine.id }, { signal: abort.signal, timeoutMilliseconds: 30_000 }).then(value => {
      if (abort.signal.aborted) return;
      const failed = managementProblem(value.status);
      if (failed) setProblem({ key: failed, detail: value.message }); else { setProblem(null); setView(value); }
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The machine does not answer: it is offline." }); });
    return () => abort.abort();
  }, [api, epoch, machine.id, generation]);
  // A provider that prepares is read again a moment later, until it is ready or says why not.
  useEffect(() => {
    if (!view?.providers.some(provider => providerState(provider) === "preparing")) return;
    const timer = setTimeout(reload, 2500);
    return () => clearTimeout(timer);
  }, [reload, view]);

  const providers = view?.providers ?? [];
  return <section className="machine-section machine-providers-section" aria-label={t("Providers")}>
    <div className="machine-section-head">
      <h2>{t("Providers")}</h2>
      <Button size="small" icon={<AppIcon name="plus" size={14} />} disabled={!view} onClick={() => setEditing(null)}>{t("Add a provider…")}</Button>
    </div>
    <p className="muted-text">{t("The providers the sessions of {name} run on.", { name: machine.name })}</p>
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {view && providers.length === 0 && <Callout compact intent="warning">{t("No provider is set up on {name}: its sessions cannot start.", { name: machine.name })}</Callout>}
    {providers.length > 0 && <ul className="machine-providers">{providers.map(provider => {
      const state = providerState(provider);
      const why = providerProblem(provider);
      const fix = providerFix(provider);
      return <li key={provider.key} data-state={state}>
        <span className="machine-provider-icon">{state === "preparing" ? <ActivitySpinner size={14} />
          : <AppIcon name={state === "ready" ? "checked" : state === "disabled" ? "minus" : "warning"} size={14} className={state === "ready" ? "machine-ready" : undefined} />}</span>
        <span className="machine-provider-main">
          <span className="machine-provider-title"><strong>{provider.name}</strong>
            <Tag minimal round intent={providerStateIntent(state)}>{t(providerStateLabel(state))}</Tag>
            {provider.isDefault && <Tag minimal round>{t("Default")}</Tag>}</span>
          <small className="muted-text">{[provider.type, provider.model, provider.keySecret && t("key in the secret {name}", { name: provider.keySecret })].filter(Boolean).join(" · ")}</small>
          {why && <small className="machine-provider-problem">{t(why.key, why.values)}</small>}
        </span>
        <span className="machine-provider-actions">
          {fix === "sign_in" && state !== "ready" && <Button size="small" intent="primary" icon={<AppIcon name="shield" size={13} />} text={t("Sign in…")}
            onClick={() => setSigningIn(provider.key)} />}
          {fix === "key" && <Button size="small" intent={state === "not_ready" ? "primary" : undefined} text={t(state === "not_ready" ? "Add its key…" : "Edit…")}
            onClick={() => setEditing(provider)} />}
        </span>
      </li>;
    })}</ul>}
    {editing !== undefined && view && <ProviderDialog epoch={epoch} machine={machine} api={api} view={view} provider={editing}
      onClose={(saved, signIn) => { setEditing(undefined); if (saved) reload(); if (signIn) setSigningIn(signIn); }} />}
    {signingIn && <ProviderSignInDialog epoch={epoch} machine={machine} api={api} providerKey={signingIn}
      name={providers.find(provider => provider.key === signingIn)?.name ?? signingIn} onClose={() => { setSigningIn(null); reload(); }} />}
  </section>;
}

/**
 * A provider to add or change on a machine: its type (when new), its name, its key (typed here, kept in the secrets of the
 * machine; a provider that has one keeps it while the field stays empty), its model and its endpoint. A provider that signs in
 * with a code takes no key: it is added, then its sign-in starts.
 */
function ProviderDialog({ epoch, machine, api, view, provider, onClose }: {
  epoch: string; machine: MachineView; api: MachineProvidersApi; view: MachineProvidersView; provider: MachineProviderStatusView | null;
  onClose: (saved: boolean, signIn?: string) => void;
}) {
  const { t } = useShellLanguage();
  const [type, setType] = useState(provider?.type ?? view.types[0]?.type ?? "");
  const [name, setName] = useState(provider?.name ?? "");
  const [apiKey, setApiKey] = useState("");
  const [model, setModel] = useState(provider?.model ?? "");
  const [apiUrl, setApiUrl] = useState(provider?.apiUrl ?? "");
  const [makeDefault, setMakeDefault] = useState(provider?.isDefault ?? view.providers.length === 0);
  const [saving, setSaving] = useState(false);
  const [problem, setProblem] = useState<Problem>(null);
  const chosen = view.types.find(value => value.type === type);
  const takesKey = provider ? provider.credential === "key" : chosen?.credential === "key";
  const ready = !!type && (!takesKey || !!apiKey.trim() || !!provider?.keySecret && provider.reason !== "secret_missing");
  async function save() {
    if (!ready) return;
    setSaving(true);
    setProblem(null);
    try {
      const key = provider?.key ?? newProviderKey(type, view.providers.map(value => value.key));
      const result = await api.setProvider({ expectedEpoch: epoch, machineId: machine.id, key, type: provider ? null : type, displayName: name.trim() || (provider ? "" : null),
        model: model.trim() || (provider?.model ? "" : null), apiUrl: apiUrl.trim() || (provider?.apiUrl ? "" : null), apiKey: takesKey ? apiKey.trim() || null : null,
        enabled: true, makeDefault }, { timeoutMilliseconds: 60_000 });
      const failed = managementProblem(result.status);
      if (failed) { setProblem({ key: failed, detail: result.message }); return; }
      onClose(true, !takesKey && chosen?.signIn && !provider ? key : undefined);
    } catch {
      setProblem({ key: "The machine does not answer: it is offline." });
    } finally { setSaving(false); }
  }
  return <Dialog isOpen className="machine-provider-dialog" title={t(provider ? "{name} on {machine}" : "Add a provider to {machine}", { name: provider?.name ?? "", machine: machine.name })}
    canOutsideClickClose={false} onClose={() => { if (!saving) onClose(false); }}>
    <DialogBody>
      <form className="fleet-server-form" onSubmit={event => { event.preventDefault(); void save(); }}>
        {!provider && <label><span>{t("Provider")}</span>
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
        {prompt.url && <div className="machine-provider-code-row"><a href={prompt.url} target="_blank" rel="noreferrer">{prompt.url}</a></div>}
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
