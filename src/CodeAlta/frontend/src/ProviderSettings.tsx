import { useEffect, useRef, useState } from "react";
import { Button, Callout, Card, CardList, Checkbox, FormGroup, HTMLSelect, InputGroup, Menu, MenuDivider, MenuItem, NonIdealState, PopoverNext, Section, SectionCard, Switch, Tag, type Intent } from "@blueprintjs/core";
import { globalConfig, providerLogin, providerUsage, type GlobalConfigProviderDefaults, type GlobalConfigProvidersResponse, type ModelCatalogProbeRequest, type ModelCatalogProbeResponse,
  type ModelCatalogProvidersRequest, type ModelCatalogProvidersResponse } from "#neoastra";
import { ProviderAccount } from "./ProviderAccount";
import { ProviderUsagePanel } from "./UsageLimits";
import { hasSubscriptionUsage } from "./subscriptionUsage";
import { providerDefault } from "./providerSignIn";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { configReadNotice, configSaveNotice, type ConfigNotice } from "./configEditor";
import { providerEdit, providerForm, providerFormDirty, providerProblem, runsOwnCli, usesAccountSignIn, validateProviderForm, type ProviderForm } from "./providerForm";
import { GuidedTour, type GuidedTourStep } from "./GuidedTour";
import { providerTourSteps, providerTourStorageKey, startsProviderTour } from "./providerTour";
import { useShellLanguage } from "./shellLanguage";

type CallOptions = { signal: AbortSignal; timeoutMilliseconds: number };
const newProvider = "\u0000new";

// A text field that can be left to its default: blank shows the default as its placeholder with a "Default"
// tag, and an overridden value has a button that goes back to it. `unset` says what a blank field without a
// default stands for.
function DefaultedInput({ id, value, fallback, unset, disabled, onChange }: {
  id: string; value: string; fallback: string | null; unset?: string; disabled: boolean; onChange: (value: string) => void;
}) {
  const { t } = useShellLanguage();
  return <InputGroup id={id} value={value} disabled={disabled} spellCheck={false} onChange={event => onChange(event.target.value)}
    placeholder={fallback ?? unset ?? t("Provider default")}
    rightElement={value ? <Button variant="minimal" size="small" disabled={disabled} icon={<AppIcon name="reset" size={13} />}
      aria-label={t("Use the default")} title={fallback ? t("Use the default: {value}", { value: fallback }) : t("Use the default")} onClick={() => onChange("")} />
      : <Tag minimal className="provider-default-tag">{t("Default")}</Tag>} />;
}

/**
 * Settings page for model providers: the configured definitions on the left, an edit form on the right.
 * Saving writes the global configuration and re-registers the providers in the running host.
 */
export function ProviderSettings({ epoch, config = globalConfig, login = providerLogin, usage = providerUsage, readRuntime, probe, onOpenModels, onOpenConfiguration, onApplied, guide = false, onGuideClosed }: {
  epoch: string; config?: Pick<typeof globalConfig, "providers" | "saveProvider" | "deleteProvider" | "addBuiltInProvider">;
  login?: Pick<typeof providerLogin, "status" | "login" | "logout">;
  /** Reads the usage of the subscription of a provider that has one. */
  usage?: Pick<typeof providerUsage, "read">;
  readRuntime: (request: ModelCatalogProvidersRequest, options: CallOptions) => Promise<ModelCatalogProvidersResponse>;
  probe: (request: ModelCatalogProbeRequest, options: CallOptions) => Promise<ModelCatalogProbeResponse>;
  onOpenModels: () => void; onOpenConfiguration: () => void; onApplied?: () => void;
  /** The application started without an enabled provider: the setup guide starts by itself, the first time. */
  guide?: boolean; onGuideClosed?: () => void;
}) {
  const { t } = useShellLanguage();
  const page = useRef<HTMLElement>(null);
  const [tour, setTour] = useState(false);
  const guided = useRef(false);
  const [listing, setListing] = useState<GlobalConfigProvidersResponse | null>(null);
  const [availability, setAvailability] = useState<ReadonlyMap<string, string>>(new Map());
  const [selected, setSelected] = useState<string | null>(null);
  const [form, setForm] = useState<ProviderForm | null>(null);
  const [notice, setNotice] = useState<ConfigNotice | null>(null);
  const [diagnostic, setDiagnostic] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [probing, setProbing] = useState(false);
  const [generation, setGeneration] = useState(0);
  const alive = useRef(true);
  // After a save, select the saved provider once the refreshed listing arrives.
  const selectAfterLoad = useRef<string | null>(null);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);

  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    void Promise.all([
      config.providers({ expectedEpoch: epoch }, { signal: controller.signal, timeoutMilliseconds: 15000 }),
      // Runtime availability is decoration: a failed read only leaves the status tags unknown.
      readRuntime({ expectedEpoch: epoch }, { signal: controller.signal, timeoutMilliseconds: 15000 }).catch(() => null),
    ]).then(([value, runtime]) => {
      if (controller.signal.aborted) return;
      const failure = configReadNotice(value.status);
      if (failure) { setListing(null); setNotice(failure); return; }
      setListing(value);
      setAvailability(new Map(runtime?.status === "ok" && runtime.epoch === epoch ? runtime.providers.map(provider => [provider.id.toLowerCase(), provider.availability]) : []));
      const wanted = selectAfterLoad.current; selectAfterLoad.current = null;
      setSelected(current => {
        const key = wanted ?? current;
        const next = key && key !== newProvider && value.providers.some(provider => provider.key === key) ? key : value.providers[0]?.key ?? null;
        setForm(next ? providerForm(value.providers.find(provider => provider.key === next)!, value.defaultProvider, value.providerTypes) : null);
        return next;
      });
    }).catch(() => { if (!controller.signal.aborted) { setListing(null); setNotice(configReadNotice("read_failed")); } })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [config, readRuntime, epoch, generation]);

  const providers = listing?.providers ?? [];
  // Once the providers are listed, and only the first time nothing is enabled.
  useEffect(() => {
    if (!guide || !listing || guided.current) return;
    guided.current = true;
    let shown = false;
    try { shown = localStorage.getItem(providerTourStorageKey) !== null; } catch { /* Without storage the guide shows on each such start. */ }
    if (!startsProviderTour(listing.providers, shown)) { onGuideClosed?.(); return; }
    // Shown once: leaving it in any way, closing this window included, does not bring it back by itself.
    try { localStorage.setItem(providerTourStorageKey, "shown"); } catch { /* It may show again on a later start. */ }
    setTour(true);
  }, [guide, listing]);
  function closeTour() { setTour(false); onGuideClosed?.(); }
  const original = selected && selected !== newProvider ? providers.find(provider => provider.key === selected) ?? null : null;
  const baseline = listing && selected ? providerForm(original, listing.defaultProvider, listing.providerTypes) : null;
  const dirty = !!form && !!baseline && providerFormDirty(form, baseline);
  const problem = form ? validateProviderForm(form, providers, original?.key ?? null) : null;
  const status = (key: string, enabled: boolean): { label: string; intent: Intent } => {
    if (!enabled) return { label: t("Disabled"), intent: "none" };
    const value = availability.get(key.toLowerCase());
    return { label: value ?? t("Not loaded"), intent: value === "Ready" ? "success" : value === "Failed" || value === "Unsupported" ? "danger" : value ? "warning" : "none" };
  };
  function choose(key: string) {
    if (busy || key === selected || !listing) return;
    setSelected(key); setNotice(null); setDiagnostic(null);
    setForm(providerForm(key === newProvider ? null : providers.find(provider => provider.key === key) ?? null, listing.defaultProvider, listing.providerTypes));
  }
  const edit = (change: Partial<ProviderForm>) => setForm(current => current ? { ...current, ...change } : current);
  // What a blank field falls back to: this provider's built-in values, then its adapter type's.
  const fallback = (field: keyof GlobalConfigProviderDefaults) => form ? providerDefault(field, form.type, original, listing?.typeDefaults ?? []) : null;

  async function settle(run: () => Promise<{ status: string; message: string | null; providersApplied: number }>, select: string | null) {
    setBusy(true); setNotice(null); setDiagnostic(null);
    try {
      const result = await run();
      if (!alive.current) return;
      setNotice(configSaveNotice(result, true)); setDiagnostic(result.message);
      if (result.status === "ok" || result.status === "apply_failed") { selectAfterLoad.current = select; setGeneration(value => value + 1); onApplied?.(); }
    } catch {
      if (alive.current) setNotice({ key: "The save did not complete; reload to see what is on disk.", intent: "danger" });
    } finally { if (alive.current) setBusy(false); }
  }
  function save() {
    if (!form || !listing || problem || busy) return;
    const wire = providerEdit(form);
    void settle(() => config.saveProvider({ expectedEpoch: epoch, expectedRevision: listing.revision, originalKey: original?.key ?? null,
      provider: wire, makeDefault: form.makeDefault, applyProviders: true }, { timeoutMilliseconds: 60000 }), wire.key);
  }
  // A provider CodeAlta knows: it is added as CodeAlta ships it, and shown so that its credential can be given.
  function addBuiltIn(key: string) {
    if (!listing || busy) return;
    void settle(() => config.addBuiltInProvider({ expectedEpoch: epoch, expectedRevision: listing.revision, key }, { timeoutMilliseconds: 60000 }), key);
  }
  function remove() {
    if (!original || !listing || busy) return;
    void settle(() => config.deleteProvider({ expectedEpoch: epoch, expectedRevision: listing.revision, key: original.key, applyProviders: true },
      { timeoutMilliseconds: 60000 }), null);
  }
  async function test() {
    if (!original || probing || dirty) return;
    setProbing(true); setNotice(null); setDiagnostic(null);
    try {
      const reply = await probe({ expectedEpoch: epoch, providerId: original.key }, { signal: new AbortController().signal, timeoutMilliseconds: 45000 });
      if (!alive.current) return;
      if (reply.status === "ok" && reply.epoch === epoch) {
        setAvailability(current => new Map(current).set(original.key.toLowerCase(), reply.availability));
        const cause = providerProblem(reply.reason);
        setNotice(cause ? { key: cause, intent: "warning" }
          : { key: "Completed test for {id}: {availability}. This is provider initialization, not an authentication guarantee.",
            parameters: { id: original.key, availability: reply.availability }, intent: reply.availability === "Ready" ? "success" : "warning" });
      } else setNotice({ key: reply.status === "busy" ? "A provider test is already running. Try again after it settles." : "Provider test could not be confirmed.", intent: "warning" });
    } catch { if (alive.current) setNotice({ key: "Provider test could not be confirmed.", intent: "warning" }); }
    finally { if (alive.current) setProbing(false); }
  }

  const tourSteps: GuidedTourStep[] = !tour || !listing ? [] : providerTourSteps(providers).map(step => ({
    id: step.id, title: t(step.title, step.parameters), body: t(step.body, step.parameters), placement: step.target === "account" ? "top" : "right",
    enter: step.provider === null ? undefined : () => choose(step.provider!),
    target: () => page.current?.querySelector(step.target === "list" ? ".provider-settings-list" : step.target === "account" ? ".provider-account"
      : `[data-provider-key="${CSS.escape(step.provider ?? "")}"]`) ?? null,
  }));

  return <main ref={page} className="configuration-page provider-settings" aria-label={t("Provider management")}>
    <header className="page-heading provider-settings-heading">
      <div><span className="eyebrow">{t("Agent & models")}</span><h1>{t("Providers")}</h1>
        <p>{t("Model providers CodeAlta can use. Saving writes the global configuration and applies it to the running app.")}</p></div>
      <div className="provider-settings-actions">
        {(loading || busy) && <ActivitySpinner size={14} />}
        <Button variant="minimal" icon={<AppIcon name="question" size={15} />} disabled={!listing || tour} aria-label={t("Setup guide")} title={t("Setup guide")} onClick={() => setTour(true)} />
        <Button icon={<AppIcon name="refresh" size={15} />} disabled={loading || busy} onClick={() => setGeneration(value => value + 1)}>{t("Reload")}</Button>
        {listing && listing.builtIn.length > 0
          // The providers CodeAlta knows and the user does not have yet are offered first: one click adds one with its key and type.
          ? <PopoverNext placement="bottom-end" content={<Menu className="provider-add-menu" aria-label={t("Add provider")}>
              <MenuDivider title={t("Built-in providers")} />
              {listing.builtIn.map(entry => <MenuItem key={entry.key} icon={<AppIcon name="model" size={15} />} text={entry.name} label={entry.type} disabled={busy}
                onClick={() => addBuiltIn(entry.key)} />)}
              <MenuDivider />
              <MenuItem icon={<AppIcon name="plus" size={15} />} text={t("Custom provider…")} disabled={busy} onClick={() => choose(newProvider)} />
            </Menu>}>
              <Button intent="primary" icon={<AppIcon name="plus" size={15} />} endIcon={<AppIcon name="chevronDown" size={14} />} disabled={busy} aria-haspopup="menu">{t("Add provider")}</Button>
            </PopoverNext>
          : <Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!listing || busy} onClick={() => choose(newProvider)}>{t("Add provider")}</Button>}
      </div>
    </header>
    {notice && <Callout intent={notice.intent} compact role={notice.intent === "success" ? "status" : "alert"}>
      {t(notice.key, notice.parameters)}{diagnostic && <div className="config-editor-diagnostic">{diagnostic}</div>}</Callout>}
    {/* The providers a newer version wrote: not listed and not saved over, so that they are not taken as lost. */}
    {listing && listing.unsupported.length > 0 && <Callout intent="warning" compact role="status" className="provider-settings-unsupported">
      {t("This version of CodeAlta does not know these providers. They are left out here and kept in the configuration file; a newer version of CodeAlta can use them.")}
      <ul>{listing.unsupported.map(provider => <li key={provider.key}><code>{provider.key}</code> — {t("type {type}", { type: provider.type })}</li>)}</ul>
    </Callout>}
    {!listing ? <NonIdealState icon={loading ? <ActivitySpinner size={28} /> : <AppIcon name="model" size={36} />}
        title={t(loading ? "Loading configured providers." : "Provider configuration unavailable")} />
      : <div className="provider-settings-layout">
        <CardList compact className="provider-settings-list" aria-label={t("Configured providers")}>
          {providers.map(provider => { const state = status(provider.key, provider.enabled); return <Card key={provider.key} interactive selected={selected === provider.key}
            data-provider-key={provider.key} aria-current={selected === provider.key ? "true" : undefined} onClick={() => choose(provider.key)}>
            <span className="provider-settings-name"><strong>{provider.effectiveName}</strong><small>{provider.key} · {provider.type}</small></span>
            <span className="provider-settings-tags">{provider.key === listing.defaultProvider && <Tag minimal round intent="primary">{t("Default")}</Tag>}
              <Tag minimal round intent={state.intent}>{state.label}</Tag></span>
          </Card>; })}
          {selected === newProvider && <Card interactive selected><span className="provider-settings-name"><strong>{form?.displayName || t("New provider")}</strong>
            <small>{form?.key || "…"} · {form?.type}</small></span><Tag minimal round intent="warning">{t("Unsaved changes")}</Tag></Card>}
          {providers.length === 0 && selected !== newProvider && <Card><span className="bp6-text-muted">{t("No providers are configured yet.")}</span></Card>}
        </CardList>
        {form && <Section className="provider-settings-form" title={original ? original.effectiveName : t("New provider")}
          subtitle={original ? `${original.key} · ${original.type}` : t("Not saved yet")}
          rightElement={<Switch checked={form.enabled} disabled={busy} label={t("Enabled")} alignIndicator="end"
            onChange={event => edit({ enabled: event.currentTarget.checked, makeDefault: event.currentTarget.checked && form.makeDefault })} />}>
          <SectionCard className="provider-settings-fields">
            <FormGroup label={t("Provider key")} labelFor="provider-key" helperText={original ? t("Sessions refer to a provider by its key; renaming it does not update them.") : undefined}>
              <InputGroup id="provider-key" value={form.key} disabled={busy} maxLength={64} spellCheck={false} onChange={event => edit({ key: event.target.value })} placeholder="my-provider" /></FormGroup>
            <FormGroup label={t("Adapter type")} labelFor="provider-type">
              <HTMLSelect id="provider-type" fill value={form.type} disabled={busy} onChange={event => edit({ type: event.target.value })}
                options={[...new Set([form.type, ...listing.providerTypes])]} /></FormGroup>
            <FormGroup label={t("Display name")} labelFor="provider-name">
              <DefaultedInput id="provider-name" value={form.displayName} fallback={fallback("displayName") ?? (form.key.trim() || null)} disabled={busy} onChange={displayName => edit({ displayName })} /></FormGroup>
            <FormGroup label={t("Default model")} labelFor="provider-model">
              <DefaultedInput id="provider-model" value={form.model} fallback={fallback("model")} unset={t("First model listed")} disabled={busy} onChange={model => edit({ model })} /></FormGroup>
            <FormGroup label={t("Reasoning")} labelFor="provider-reasoning">
              <HTMLSelect id="provider-reasoning" fill value={form.reasoningEffort} disabled={busy} onChange={event => edit({ reasoningEffort: event.target.value })}>
                <option value="">{fallback("reasoningEffort") ? t("Default ({value})", { value: fallback("reasoningEffort")! }) : t("High when supported")}</option>
                {[...new Set([...(form.reasoningEffort ? [form.reasoningEffort] : []), ...listing.reasoningEfforts])].map(effort => <option key={effort} value={effort}>{effort}</option>)}
              </HTMLSelect></FormGroup>
            {!runsOwnCli(form.type) && <FormGroup label={t("API URL")} labelFor="provider-url">
              <DefaultedInput id="provider-url" value={form.apiUrl} fallback={fallback("apiUrl") ?? original?.effectiveApiUrl ?? null} disabled={busy} onChange={apiUrl => edit({ apiUrl })} /></FormGroup>}
            {runsOwnCli(form.type)
              ? <Callout compact className="provider-settings-wide" icon={<AppIcon name="terminal" size={16} />}>{t("This provider runs the Claude Code CLI installed on this computer. It signs in by itself: run claude in a terminal and use /login. CodeAlta never handles its credentials. The executable path and the other settings are in the configuration file.")}</Callout>
              : usesAccountSignIn(form.type)
              ? original && original.type === form.type
                ? <ProviderAccount epoch={epoch} providerKey={original.key} api={login} blocked={dirty ? "Save before signing in." : null}
                  onChanged={() => { selectAfterLoad.current = original.key; setGeneration(value => value + 1); onApplied?.(); }} />
                : <Callout compact className="provider-settings-wide" icon={<AppIcon name="user" size={16} />}>{t("This provider signs in with its account. Save it, then sign in here.")}</Callout>
              : <>
                <FormGroup label={t("API key environment variable")} labelFor="provider-key-env" helperText={t("Preferred: the key stays out of the configuration file.")}>
                  <DefaultedInput id="provider-key-env" value={form.apiKeyEnv} fallback={fallback("apiKeyEnv")} disabled={busy} onChange={apiKeyEnv => edit({ apiKeyEnv })} /></FormGroup>
                <FormGroup label={t("API key")} labelFor="provider-secret" helperText={original?.hasApiKey ? t("A key is stored in the configuration file. Leave blank to keep it.") : t("Stored as plain text in the configuration file.")}>
                  <InputGroup id="provider-secret" type="password" autoComplete="off" value={form.apiKey} disabled={busy || form.clearApiKey}
                    onChange={event => edit({ apiKey: event.target.value })} placeholder={original?.hasApiKey ? "••••••••" : ""} />
                  {original?.hasApiKey && <Checkbox checked={form.clearApiKey} disabled={busy} label={t("Remove the stored key")}
                    onChange={event => edit({ clearApiKey: event.currentTarget.checked, apiKey: "" })} />}</FormGroup>
              </>}
            {original && original.type === form.type && hasSubscriptionUsage(form.type)
              && <ProviderUsagePanel key={original.key} epoch={epoch} providerKey={original.key} api={usage} />}
            <Checkbox className="provider-settings-wide" checked={form.makeDefault} disabled={busy || !form.enabled} label={t("Use as the default provider for new sessions")}
              onChange={event => edit({ makeDefault: event.currentTarget.checked })} />
          </SectionCard>
          <SectionCard className="provider-settings-footer">
            {problem && dirty && <span className="provider-settings-problem" role="alert">{t(problem)}</span>}
            <Button intent="primary" disabled={!dirty || !!problem || busy} onClick={save}>{t("Save and apply")}</Button>
            <Button disabled={!dirty || busy} onClick={() => { if (baseline) setForm(baseline); }}>{t("Revert")}</Button>
            <span className="provider-settings-spacer" />
            {original && <Button icon={probing ? <ActivitySpinner size={14} /> : <AppIcon name="check" size={15} />} disabled={!original.enabled || probing || busy || dirty}
              title={dirty ? t("Save before testing.") : undefined} onClick={() => void test()}>{t(probing ? "Testing selected provider…" : "Test selected provider")}</Button>}
            <Button icon={<AppIcon name="model" size={15} />} onClick={onOpenModels}>{t("Browse models")}</Button>
            <Button variant="minimal" icon={<AppIcon name="edit" size={15} />} onClick={onOpenConfiguration} title={t("Every other provider setting is in the configuration file.")}>{t("Configuration file")}</Button>
            {original && <PopoverNext placement="top-end" content={<div className="provider-settings-confirm"><p>{t("Remove {name} from the configuration? Sessions that use it keep their history.", { name: original.effectiveName })}</p>
              <Button intent="danger" disabled={busy} onClick={remove}>{t("Remove provider")}</Button></div>}>
              <Button variant="minimal" intent="danger" icon={<AppIcon name="trash" size={15} />} disabled={busy} aria-label={t("Remove provider")} title={t("Remove provider")} />
            </PopoverNext>}
          </SectionCard>
        </Section>}
      </div>}
    {tourSteps.length > 0 && <GuidedTour steps={tourSteps} label={t("Setup guide")} onClose={closeTour} />}
  </main>;
}
