import { useEffect, useRef, useState } from "react";
import { Button, Callout, Card, CardList, Checkbox, FormGroup, HTMLSelect, InputGroup, Menu, MenuDivider, MenuItem, NonIdealState, PopoverNext, Section, SectionCard, Switch, Tag, type Intent } from "@blueprintjs/core";
import { globalConfig, modelCatalog, providerLogin, providerUsage, type GlobalConfigProviderDefaults, type GlobalConfigProvidersResponse, type ModelCatalogCapability, type ModelCatalogProbeRequest, type ModelCatalogProbeResponse,
  type ModelCatalogProvidersRequest, type ModelCatalogProvidersResponse } from "#neoastra";
import { ProviderAccount } from "./ProviderAccount";
import { ProviderModelPicker } from "./ProviderModelPicker";
import { ProviderUsagePanel } from "./UsageLimits";
import { hasSubscriptionUsage } from "./subscriptionUsage";
import { providerDefault } from "./providerSignIn";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { providerBrand } from "./brands";
import { Logo, ProviderIcon } from "./ProviderIcon";
import { ProviderColorInput, ProviderIconPicker } from "./ProviderIconFields";
import { configReadNotice, configSaveNotice, type ConfigNotice } from "./configEditor";
import { isSignedOut, providerEdit, providerForm, providerFormDirty, providerProblem, runsOwnCli, usesAccountSignIn, validateProviderForm, type ProviderForm } from "./providerForm";
import { GuidedTour, type GuidedTourStep } from "./GuidedTour";
import { providerTourSteps, providerTourStorageKey, startsProviderTour } from "./providerTour";
import { SettingsFileLocations } from "./SettingsFileLocation";
import { useSettingsFiles, type SettingsFilesApi } from "./settingsFiles";
import { useShellLanguage } from "./shellLanguage";
import { ClaudeCodeCli, type ClaudeCliApi } from "./claudeCli/ClaudeCodeCli";
import { sameConfigDir } from "./claudeCli/claudeCli";
import { ResourceSpaceChip, type ResourceSpaces } from "./spaces/SpaceChips";
import { PluginProviderFields } from "./pluginProviders/PluginProviderFields";
import { PluginProviderPanel } from "./pluginProviders/PluginProviderPanel";
import { ProviderCatalogDialog } from "./pluginProviders/ProviderCatalogDialog";
import { pluginFieldsProblem, pluginSettingValues, pluginType } from "./pluginProviders/pluginProviderForm";

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
export function ProviderSettings({ epoch, config = globalConfig, plugins = globalConfig, login = providerLogin, usage = providerUsage, cli, readRuntime, readModels = modelCatalog.models, probe, onOpenModels, onOpenConfiguration, onApplied, guide = false, onGuideClosed, onOpenFile, filesApi, providerSessions, spaceScope = null, runInTerminal }: {
  epoch: string; config?: Pick<typeof globalConfig, "providers" | "saveProvider" | "deleteProvider" | "addBuiltInProvider" | "duplicateProvider">;
  /** The providers whose type a plugin runs (ACP agents): their form, their catalog and their sign-in; the host by default. */
  plugins?: Pick<typeof globalConfig, "savePluginProvider" | "pluginCatalog" | "installPluginProvider" | "pluginProviderDetails" | "updatePluginProvider" | "pluginProviderSignIn">;
  /** Runs a command in a terminal of the window: the sign-in of an agent that signs in in a terminal. */
  runInTerminal?: (command: string) => void;
  login?: Pick<typeof providerLogin, "status" | "login" | "logout">;
  /** The Claude Code CLI CodeAlta pins and its download; the host by default. */
  cli?: ClaudeCliApi;
  /** How many sessions use a provider, by its key: a change of the folder of its account asks first when it has some. */
  providerSessions?: (key: string) => number;
  /** Reads the usage of the subscription of a provider that has one. */
  usage?: Pick<typeof providerUsage, "read">;
  readRuntime: (request: ModelCatalogProvidersRequest, options: CallOptions) => Promise<ModelCatalogProvidersResponse>;
  readModels?: typeof modelCatalog.models;
  probe: (request: ModelCatalogProbeRequest, options: CallOptions) => Promise<ModelCatalogProbeResponse>;
  onOpenModels: () => void; onOpenConfiguration: () => void; onApplied?: () => void;
  /** The application started without an enabled provider: the setup guide starts by itself, the first time. */
  guide?: boolean; onGuideClosed?: () => void;
  /** Called once the code editor was asked to show the configuration file: the window leaves Settings. */
  onOpenFile?: () => void;
  /** Says where the configuration file is and opens it; the host by default. */
  filesApi?: SettingsFilesApi;
  /** The spaces of the providers, shown and changed beside each of them; null while there is only the default space. */
  spaceScope?: ResourceSpaces;
}) {
  const { t } = useShellLanguage();
  const page = useRef<HTMLElement>(null);
  const [tour, setTour] = useState(false);
  const guided = useRef(false);
  const [listing, setListing] = useState<GlobalConfigProvidersResponse | null>(null);
  // What the providers are doing, and why one is not ready when the host names a reason.
  const [availability, setAvailability] = useState<ReadonlyMap<string, Readonly<{ availability: string; reason: string | null; capabilities?: readonly ModelCatalogCapability[] | null }>>>(new Map());
  // The catalog of a provider type of a plugin that is open (the agents of the ACP registry), by its type.
  const [catalogType, setCatalogType] = useState<string | null>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [form, setForm] = useState<ProviderForm | null>(null);
  const [notice, setNotice] = useState<ConfigNotice | null>(null);
  const files = useSettingsFiles({ page: "config", epoch, projectId: null, onOpened: onOpenFile, setNotice, api: filesApi });
  const [diagnostic, setDiagnostic] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [probing, setProbing] = useState(false);  const [generation, setGeneration] = useState(0);
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
      setAvailability(new Map(runtime?.status === "ok" && runtime.epoch === epoch ? runtime.providers.map(provider => [provider.id.toLowerCase(), { availability: provider.availability, reason: provider.reason ?? null, capabilities: provider.capabilities ?? null }]) : []));
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
  const pluginTypes = listing?.pluginTypes ?? [];
  const catalogs = pluginTypes.filter(type => type.hasCatalog);
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
  const original = selected && selected !== newProvider ? providers.find(provider => provider.key === selected) ?? null : null;  const baseline = listing && selected ? providerForm(original, listing.defaultProvider, listing.providerTypes) : null;
  const dirty = !!form && !!baseline && providerFormDirty(form, baseline);
  const modelProvider = original?.enabled && form && baseline
    && (["key", "type", "enabled", "apiUrl", "apiKeyEnv", "apiKey", "clearApiKey"] as const).every(field => form[field] === baseline[field]) ? original.key : null;
  // A provider whose type a plugin runs: its plugin declares its settings, its catalog and its sign-in.
  const formPlugin = form ? pluginType(pluginTypes, form.type) : null;
  const problem = form ? validateProviderForm(form, providers, original?.key ?? null) ?? (formPlugin ? pluginFieldsProblem(formPlugin, form.plugin) : null) : null;
  const status = (key: string, enabled: boolean): { label: string; intent: Intent } => {
    if (!enabled) return { label: t("Disabled"), intent: "none" };
    const state = availability.get(key.toLowerCase());
    // A provider whose account is signed out is not broken: it waits for a sign-in.
    if (isSignedOut(state?.reason)) return { label: t("Signed out"), intent: "warning" };
    const value = state?.availability;
    return { label: value ?? t("Not loaded"), intent: value === "Ready" ? "success" : value === "Failed" || value === "Unsupported" ? "danger" : value ? "warning" : "none" };
  };
  const signedOut = original && original.type === form?.type && isSignedOut(availability.get(original.key.toLowerCase())?.reason);
  function choose(key: string) {
    if (busy || key === selected || !listing) return;
    setSelected(key); setNotice(null); setDiagnostic(null);
    setForm(providerForm(key === newProvider ? null : providers.find(provider => provider.key === key) ?? null, listing.defaultProvider, listing.providerTypes));
  }
  const edit = (change: Partial<ProviderForm>) => setForm(current => current ? { ...current, ...change } : current);
  // What a blank field falls back to: this provider's built-in values, then its adapter type's.
  const fallback = (field: keyof GlobalConfigProviderDefaults) => form ? providerDefault(field, form.type, original, listing?.typeDefaults ?? []) : null;
  // The icon of the provider as the form has it, before it is saved: the one found for it, and the one chosen.
  const logoSource = form ? { key: form.key, type: form.type, name: form.displayName || fallback("displayName"), apiUrl: form.apiUrl || fallback("apiUrl") || original?.effectiveApiUrl,
    iconData: original?.type === form.type ? original.plugin?.iconData : null } : null;
  const automaticLogo = logoSource && providerBrand(logoSource);
  const shownLogo = logoSource && providerBrand({ ...logoSource, icon: form!.icon, color: form!.color });

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
  // The sessions of a provider whose account folder changes keep their conversations in the old folder.
  const movedSessions = original && form && runsOwnCli(form.type) && original.type === form.type && !sameConfigDir(original.configDir, form.configDir)
    ? providerSessions?.(original.key) ?? 0 : 0;
  const [confirmFolder, setConfirmFolder] = useState(false);
  function save() {
    if (!form || !listing || problem || busy) return;
    setConfirmFolder(false);
    if (formPlugin) {
      const optional = (value: string) => value.trim() ? value.trim() : null;
      const key = form.key.trim().toLowerCase();
      void settle(() => plugins.savePluginProvider({ expectedEpoch: epoch, expectedRevision: listing.revision, originalKey: original?.key ?? null, key, type: form.type,
        enabled: form.enabled, displayName: optional(form.displayName), model: optional(form.model), reasoningEffort: optional(form.reasoningEffort),
        values: pluginSettingValues(formPlugin, form.plugin), makeDefault: form.makeDefault, icon: optional(form.icon)?.toLowerCase() ?? null,
        color: optional(form.color), permissionMode: optional(form.permissionMode) }, { timeoutMilliseconds: 60000 }), key);
      return;
    }
    const wire = providerEdit(form);
    void settle(() => config.saveProvider({ expectedEpoch: epoch, expectedRevision: listing.revision, originalKey: original?.key ?? null,
      provider: wire, makeDefault: form.makeDefault, applyProviders: true }, { timeoutMilliseconds: 60000 }), wire.key);
  }
  // A provider CodeAlta knows: it is added as CodeAlta ships it, and shown so that its credential can be given.
  function addBuiltIn(key: string) {
    if (!listing || busy) return;
    void settle(() => config.addBuiltInProvider({ expectedEpoch: epoch, expectedRevision: listing.revision, key }, { timeoutMilliseconds: 60000 }), key);
  }
  // Another account of the same type: a copy without the folder of the account, selected for the user to give it one.
  function duplicate() {
    if (!original || !listing || busy || dirty) return;
    setBusy(true); setNotice(null); setDiagnostic(null);
    void config.duplicateProvider({ expectedEpoch: epoch, expectedRevision: listing.revision, key: original.key }, { timeoutMilliseconds: 60000 })
      .then(result => {
        if (!alive.current) return;
        setNotice(configSaveNotice(result, true)); setDiagnostic(result.message);
        if (result.status === "ok" || result.status === "apply_failed") { selectAfterLoad.current = result.key ?? null; setGeneration(value => value + 1); onApplied?.(); }
      }, () => { if (alive.current) setNotice({ key: "The save did not complete; reload to see what is on disk.", intent: "danger" }); })
      .finally(() => { if (alive.current) setBusy(false); });
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
        const id = original.key.toLowerCase();
        setAvailability(current => new Map(current).set(id, { availability: reply.availability, reason: reply.reason ?? null, capabilities: current.get(id)?.capabilities ?? null }));
        // What an agent can do is known once it ran: the listing of the runtime has it after the test.
        void readRuntime({ expectedEpoch: epoch }, { signal: new AbortController().signal, timeoutMilliseconds: 15000 }).then(runtime => {
          const provider = runtime.status === "ok" && runtime.epoch === epoch ? runtime.providers.find(value => value.id.toLowerCase() === id) : undefined;
          if (alive.current && provider?.capabilities)
            setAvailability(current => new Map(current).set(id, { availability: provider.availability, reason: provider.reason ?? null, capabilities: provider.capabilities ?? null }));
        }, () => undefined);
        const cause = providerProblem(reply.reason);
        setNotice(cause ? { key: cause, parameters: { provider: original.effectiveName }, intent: "warning" }
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
        <Button icon={<AppIcon name="refresh" size={15} />} disabled={loading || busy} onClick={() => { setGeneration(value => value + 1); spaceScope?.refresh?.(); }}>{t("Reload")}</Button>
        {listing && (listing.builtIn.length > 0 || catalogs.length > 0)
          // The providers CodeAlta knows and the user does not have yet are offered first: one click adds one with its key and type.
          ? <PopoverNext placement="bottom-end" content={<Menu className="provider-add-menu" aria-label={t("Add provider")}>
              {listing.builtIn.length > 0 && <MenuDivider title={t("Built-in providers")} />}
              {listing.builtIn.map(entry => <MenuItem key={entry.key} icon={<ProviderIcon providerKey={entry.key} known={{ type: entry.type, name: entry.name }} size={15} fallback="model" />} text={entry.name} label={entry.type} disabled={busy}
                onClick={() => addBuiltIn(entry.key)} />)}
              {/* The catalogs of the plugins: the agents of the ACP registry, added in one step. */}
              {catalogs.length > 0 && <MenuDivider title={t("Agents")} />}
              {catalogs.map(type => <MenuItem key={type.type} icon={<AppIcon name="plugin" size={15} />} text={t("{title}…", { title: type.catalogTitle ?? type.name })}
                label={type.tag ?? undefined} disabled={busy} onClick={() => setCatalogType(type.type)} />)}
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
    {/* The providers are sections of the configuration file of the user: the page says where that file is. */}
    <SettingsFileLocations files={files} disabled={busy} />
    {!listing ? <NonIdealState icon={loading ? <ActivitySpinner size={28} /> : <AppIcon name="model" size={36} />}
        title={t(loading ? "Loading configured providers." : "Provider configuration unavailable")} />
      : <div className="provider-settings-layout">
        <CardList compact className="provider-settings-list" aria-label={t("Configured providers")}>
          {providers.map(provider => { const state = status(provider.key, provider.enabled); return <Card key={provider.key} interactive selected={selected === provider.key}
            data-provider-key={provider.key} aria-current={selected === provider.key ? "true" : undefined} onClick={() => choose(provider.key)}>
            <span className="provider-settings-logo"><ProviderIcon providerKey={provider.key} size={20} fallback="model"
              known={{ type: provider.type, name: provider.effectiveName, apiUrl: provider.effectiveApiUrl, icon: provider.icon, color: provider.color, iconData: provider.plugin?.iconData }} /></span>
            <span className="provider-settings-name"><strong>{provider.effectiveName}</strong><small>{provider.key} · {provider.type}</small></span>
            <span className="provider-settings-tags">{provider.key === (listing.startingProvider ?? listing.defaultProvider) && <Tag minimal round intent="primary" title={t("New sessions start with this provider.")}>{t("Default")}</Tag>}
              {provider.plugin?.tag && <Tag minimal round className="provider-plugin-tag">{provider.plugin.tag}</Tag>}
              <Tag minimal round intent={state.intent}>{state.label}</Tag></span>
            {/* The spaces go on a row of their own: however many there are, they never take the room of the name. */}
            {spaceScope && <span className="provider-settings-spaces">
              <ResourceSpaceChip scope={spaceScope} kind="provider" resourceKey={provider.key} name={provider.effectiveName} disabled={busy} /></span>}
          </Card>; })}
          {selected === newProvider && <Card interactive selected><span className="provider-settings-logo">{shownLogo && <Logo brand={shownLogo} size={20} fallback="model" />}</span><span className="provider-settings-name"><strong>{form?.displayName || t("New provider")}</strong>
            <small>{form?.key || "…"} · {form?.type}</small></span><Tag minimal round intent="warning">{t("Unsaved changes")}</Tag></Card>}
          {providers.length === 0 && selected !== newProvider && <Card><span className="bp6-text-muted">{t("No providers are configured yet.")}</span></Card>}
        </CardList>
        {form && <Section className="provider-settings-form" icon={shownLogo ? <Logo brand={shownLogo} size={20} fallback="model" /> : undefined} title={original ? original.effectiveName : t("New provider")}
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
            <FormGroup label={t("Icon")} labelFor="provider-icon">
              <ProviderIconPicker id="provider-icon" value={form.icon} automatic={automaticLogo!} shown={shownLogo!} disabled={busy} onChange={icon => edit({ icon })} /></FormGroup>
            <FormGroup label={t("Icon color")} labelFor="provider-color">
              <ProviderColorInput id="provider-color" value={form.color} disabled={busy} onChange={color => edit({ color })} /></FormGroup>
            <FormGroup label={t("Default model")} labelFor="provider-model">
              <div className="provider-model-field">
                <DefaultedInput id="provider-model" value={form.model} fallback={fallback("model")} unset={t("First model listed")} disabled={busy} onChange={model => edit({ model })} />
                <ProviderModelPicker key={`${epoch}:${modelProvider}:${listing.revision}`} epoch={epoch} providerId={modelProvider} disabled={busy || probing}
                  value={form.model} fallback={fallback("model")} readModels={readModels} onChange={model => edit({ model })} />
              </div></FormGroup>
            <FormGroup label={t("Reasoning")} labelFor="provider-reasoning">
              <HTMLSelect id="provider-reasoning" fill value={form.reasoningEffort} disabled={busy} onChange={event => edit({ reasoningEffort: event.target.value })}>
                <option value="">{fallback("reasoningEffort") ? t("Default ({value})", { value: fallback("reasoningEffort")! }) : t("High when supported")}</option>
                {[...new Set([...(form.reasoningEffort ? [form.reasoningEffort] : []), ...listing.reasoningEfforts])].map(effort => <option key={effort} value={effort}>{effort}</option>)}
              </HTMLSelect></FormGroup>
            {/* A provider is assigned to spaces once it is saved; each click of the chip saves at once. */}
            {spaceScope && original && <FormGroup label={t("Spaces")} className="provider-settings-wide">
              <ResourceSpaceChip scope={spaceScope} kind="provider" resourceKey={original.key} name={original.effectiveName} disabled={busy} /></FormGroup>}
            {runsOwnCli(form.type) && <FormGroup label={t("Account folder")} labelFor="provider-config-dir"
              helperText={t("The folder of the Claude account of this provider (CLAUDE_CONFIG_DIR). A new folder starts without settings, memory or MCP servers.")}>
              <InputGroup id="provider-config-dir" value={form.configDir} disabled={busy} spellCheck={false} placeholder={t("~/.claude, the folder of Claude Code")}
                onChange={event => edit({ configDir: event.target.value })} /></FormGroup>}
            {runsOwnCli(form.type) && <FormGroup label="ANTHROPIC_API_KEY" labelFor="provider-anthropic-key"
              helperText={t("When this variable is set. Without an answer saved by Claude Code, sessions do not start.")}>
              <HTMLSelect id="provider-anthropic-key" fill value={form.anthropicApiKey} disabled={busy} onChange={event => edit({ anthropicApiKey: event.target.value })}>
                <option value="">{t("Follow the answer saved by Claude Code")}</option>
                <option value="ignore">{t("Ignore it: use my Claude login")}</option>
                <option value="use">{t("Use it: bill its API account")}</option>
              </HTMLSelect></FormGroup>}
            {formPlugin && <PluginProviderFields key={`${original?.key ?? "new"}:${form.type}`} type={formPlugin} values={form.plugin} disabled={busy} onChange={plugin => edit({ plugin })} />}
            {formPlugin && formPlugin.permissionModes.length > 0 && <FormGroup label={t("Permissions")} labelFor="provider-permission-mode">
              <HTMLSelect id="provider-permission-mode" fill value={form.permissionMode} disabled={busy} onChange={event => edit({ permissionMode: event.target.value })}>
                <option value="">default</option>
                {formPlugin.permissionModes.filter(mode => mode !== "default").map(mode => <option key={mode} value={mode}>{mode}</option>)}
              </HTMLSelect></FormGroup>}
            {runsOwnCli(form.type) && <FormGroup label={t("Permissions")} labelFor="provider-permission-mode"
              helperText={t("The mode the CLI starts a session in. What the mode does not decide by itself is asked here.")}>
              <HTMLSelect id="provider-permission-mode" fill value={form.permissionMode} disabled={busy} onChange={event => edit({ permissionMode: event.target.value })}>
                <option value="">{t("The setting of the CLI")}</option>
                {[...new Set([...(form.permissionMode ? [form.permissionMode] : []), ...listing.permissionModes])].map(mode => <option key={mode} value={mode}>{mode}</option>)}
              </HTMLSelect></FormGroup>}
            {runsOwnCli(form.type) && <FormGroup label={t("Prompt suggestions")} labelFor="provider-prompt-suggestions"
              helperText={t("After a turn, Claude Code suggests what you may write next, in the empty prompt: Tab or → takes it. Each suggestion is a request of its own to the model.")}>
              <Switch id="provider-prompt-suggestions" checked={form.promptSuggestions} disabled={busy} label={t("Suggest the next prompt")}
                onChange={event => edit({ promptSuggestions: event.currentTarget.checked })} /></FormGroup>}
            {!runsOwnCli(form.type) && !formPlugin && <FormGroup label={t("API URL")} labelFor="provider-url">
              <DefaultedInput id="provider-url" value={form.apiUrl} fallback={fallback("apiUrl") ?? original?.effectiveApiUrl ?? null} disabled={busy} onChange={apiUrl => edit({ apiUrl })} /></FormGroup>}
            {/* A signed-out account says how to sign in, beside the box whose command does it. */}
            {signedOut && !notice && runsOwnCli(form.type) && <Callout intent="warning" compact className="provider-settings-wide provider-signed-out" icon={<AppIcon name="user" size={16} />}>
              {t("{provider} is not signed in. Run the command of the Claude Code CLI box in a terminal and type /login, then test again.", { provider: original!.effectiveName })}</Callout>}
            {formPlugin
              ? original && original.type === form.type
                ? <PluginProviderPanel key={original.key} epoch={epoch} providerKey={original.key} api={plugins}
                    capabilities={availability.get(original.key.toLowerCase())?.capabilities ?? null} signedOut={!!signedOut}
                    blocked={dirty ? t("Save before signing in.") : null} runInTerminal={runInTerminal}
                    onChanged={() => { selectAfterLoad.current = original.key; setGeneration(value => value + 1); onApplied?.(); }}
                    onSignedIn={() => void test()} />
                : <Callout compact className="provider-settings-wide" icon={<AppIcon name="plugin" size={16} />}>{t("Save the provider, then sign in and test it here.")}</Callout>
              : runsOwnCli(form.type)
              ? <ClaudeCodeCli epoch={epoch} api={cli} providerKey={original?.type === form.type ? original.key : null} configDir={form.configDir}
                  command={original?.type === form.type ? original.command : null} />
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
            {movedSessions > 0
              // Their transcripts stay in the old folder: the CLI started with the new one does not find them.
              ? <PopoverNext isOpen={confirmFolder} placement="top-start" onClose={() => setConfirmFolder(false)} content={<div className="provider-settings-confirm">
                  <p>{movedSessions === 1 ? t("1 session uses this provider. Its conversation stays in the old folder, where Claude Code will no longer find it.")
                    : t("{count} sessions use this provider. Their conversations stay in the old folder, where Claude Code will no longer find them.", { count: String(movedSessions) })}</p>
                  <Button intent="primary" disabled={busy} onClick={save}>{t("Change the folder")}</Button></div>}>
                  <Button intent="primary" disabled={!dirty || !!problem || busy} onClick={() => setConfirmFolder(true)}>{t("Save and apply")}</Button>
                </PopoverNext>
              : <Button intent="primary" disabled={!dirty || !!problem || busy} onClick={save}>{t("Save and apply")}</Button>}
            <Button disabled={!dirty || busy} onClick={() => { if (baseline) setForm(baseline); }}>{t("Revert")}</Button>
            <span className="provider-settings-spacer" />
            {original && <Button icon={probing ? <ActivitySpinner size={14} /> : <AppIcon name="check" size={15} />} disabled={!original.enabled || probing || busy || dirty}
              title={dirty ? t("Save before testing.") : undefined} onClick={() => void test()}>{t(probing ? "Testing selected provider…" : "Test selected provider")}</Button>}
            <Button icon={<AppIcon name="model" size={15} />} onClick={onOpenModels}>{t("Browse models")}</Button>
            <Button variant="minimal" icon={<AppIcon name="edit" size={15} />} onClick={onOpenConfiguration} title={t("Every other provider setting is in the configuration file.")}>{t("Configuration file")}</Button>
            {original && !formPlugin && <Button variant="minimal" icon={<AppIcon name="duplicate" size={15} />} disabled={busy || dirty}
              title={t(dirty ? "Save before duplicating." : "Another account of the same type: a copy of this provider with a folder of its own.")} onClick={duplicate}>{t("Duplicate")}</Button>}
            {original && <PopoverNext placement="top-end" content={<div className="provider-settings-confirm"><p>{t("Remove {name} from the configuration? Sessions that use it keep their history.", { name: original.effectiveName })}</p>
              <Button intent="danger" disabled={busy} onClick={remove}>{t("Remove provider")}</Button></div>}>
              <Button variant="minimal" intent="danger" icon={<AppIcon name="trash" size={15} />} disabled={busy} aria-label={t("Remove provider")} title={t("Remove provider")} />
            </PopoverNext>}
          </SectionCard>
        </Section>}
      </div>}
    {tourSteps.length > 0 && <GuidedTour steps={tourSteps} label={t("Setup guide")} onClose={closeTour} />}
    {catalogType && <ProviderCatalogDialog epoch={epoch} type={catalogType} api={plugins}
      title={catalogs.find(type => type.type === catalogType)?.catalogTitle ?? catalogType}
      onAdded={key => { selectAfterLoad.current = key; setGeneration(value => value + 1); onApplied?.(); }}
      onOpen={key => { setCatalogType(null); choose(key); }}
      onClose={() => setCatalogType(null)} />}
  </main>;
}
