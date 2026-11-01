import { useEffect, useRef, useState, type ReactNode } from "react";
import { Button, Callout, Card, HTMLSelect, InputGroup, Radio, RadioGroup, Tag, TagInput } from "@blueprintjs/core";
import { sandboxSettings, type SandboxCheckEvent, type SandboxProfileItem, type SandboxScopeItem, type SandboxSettingsResponse } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon, type IconName } from "../AppIcon";
import type { MessageKey } from "../localization";
import { SettingsField as Field } from "../SettingsField";
import { RemoveButton, SettingsPage, SettingsUnavailable, useSettingsEditor } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import { notSandboxed, resolveSandboxProfile, sandboxScopeKinds, type ResolvedSandboxProfile as Resolved, type SandboxApi, type SandboxScopeKind } from "./sandboxChoices";

const workspaceProfile = "workspace";
// A profile being written that has no name yet.
const newProfile = "\u0000new";

/** The steps of the check, in the words of the window; a step the host adds later shows its id. */
const checkSteps: Readonly<Record<string, MessageKey>> = Object.freeze({
  available: "The sandbox of this computer",
  profile: "The profile",
  start: "The start of a sandbox",
  write: "A write in a test project",
  secrets: "A file of CodeAlta out of its reach",
  network: "The network of the profile",
});
// The steps that run a developer tool are named by their command.
const commandSteps: Readonly<Record<string, string>> = Object.freeze({ dotnet: "dotnet --version", git: "git --version" });

const scopeIcons: Readonly<Record<SandboxScopeKind, IconName>> = Object.freeze({ provider: "provider", model: "model", space: "space", project: "folder" });

/** A profile as the page edits it: the written values of a profile of the user, null where it inherits. */
type ProfileDraft = Readonly<{ originalName: string | null; name: string; extends: string | null; network: string | null; outside: string | null;
  hosts: readonly string[]; write: readonly string[]; read: readonly string[]; deny: readonly string[]; localPorts: readonly number[];
  env: readonly string[]; mcpServers: readonly string[] }>;

const draftOf = (profile: SandboxProfileItem): ProfileDraft => ({ originalName: profile.name, name: profile.name, extends: profile.extends, network: profile.network,
  outside: profile.outside, hosts: profile.hosts, write: profile.write, read: profile.read, deny: profile.deny, localPorts: profile.localPorts, env: profile.env,
  mcpServers: profile.mcpServers });

const emptyDraft = (base: string | null, name: string): ProfileDraft => ({ originalName: null, name, extends: base, network: null, outside: null,
  hosts: [], write: [], read: [], deny: [], localPorts: [], env: [], mcpServers: [] });

const sameDraft = (left: ProfileDraft, right: ProfileDraft) => JSON.stringify(left) === JSON.stringify(right);

/** The check of a profile as it runs: its steps as they come, then whether all passed. */
type CheckRun = Readonly<{ profile: string; steps: readonly SandboxCheckEvent[]; passed: boolean | null; failure: string | null }>;

/**
 * Settings > Sandbox: whether the sandbox of the sessions can run on this computer and its check, the sandbox of every
 * session, the profiles (those of CodeAlta, read-only, and those of the user, edited here), and the other places that
 * choose a sandbox, each a link to where it is chosen. A change is saved at once, except the edit of a profile, saved
 * with its button.
 */
export function SandboxSettings({ epoch, api = sandboxSettings, onOpenScope }: {
  /** The host; null where the window does not own one. */
  epoch: string | null;
  api?: SandboxApi;
  /** Opens the place where a scope chooses its sandbox: the editor of a provider, the page of a space, the row of a project. */
  onOpenScope?: (scope: SandboxScopeItem) => void;
}) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, setNotice, reload, mutate } = useSettingsEditor<SandboxSettingsResponse>(options => epoch
    ? api.read({ expectedEpoch: epoch }, options) : Promise.resolve({ status: "unavailable", availability: null, allSessions: null, profiles: [], scopes: [] }), epoch ?? "");
  const [selected, setSelected] = useState<string | null>(null);
  const [draft, setDraft] = useState<ProfileDraft | null>(null);
  const profiles = listing?.profiles ?? [];
  const shown = selected === newProfile ? null : profiles.find(profile => profile.name === selected) ?? profiles[0] ?? null;
  // The editor of the profile shown: what is written, and what is being edited; a new profile is a draft alone. The
  // edits of a profile outlive a reload of the page (another change saved); a save or another profile starts afresh.
  const fresh = useRef(true);
  useEffect(() => {
    if (selected === newProfile) return;
    const restart = fresh.current;
    fresh.current = false;
    setDraft(current => !shown || shown.builtIn ? null : !restart && current?.originalName === shown.name ? current : draftOf(shown));
  }, [listing, selected]);
  function select(name: string | null) {
    fresh.current = true;
    setSelected(name);
    setNotice(null);
  }

  async function saveProfile(next: ProfileDraft) {
    if (!epoch) return;
    const { originalName, ...values } = next;
    const name = values.name.trim();
    const saved = await mutate(() => api.saveProfile({ expectedEpoch: epoch, originalName,
      profile: { ...values, name, builtIn: false, problem: null } }, { timeoutMilliseconds: 30_000 }));
    if (saved) { fresh.current = true; setSelected(name); }
  }
  async function removeProfile(name: string) {
    if (!epoch) return;
    if (await mutate(() => api.deleteProfile({ expectedEpoch: epoch, name }, { timeoutMilliseconds: 30_000 }))) { fresh.current = true; setSelected(null); }
  }
  function startProfile(base: string | null) {
    const taken = new Set(profiles.map(profile => profile.name.toLowerCase()));
    let name = base ? `my-${base}` : "my-profile";
    for (let index = 2; taken.has(name.toLowerCase()); index++) name = `${base ? `my-${base}` : "my-profile"}-${index}`;
    setDraft(emptyDraft(base, name));
    select(newProfile);
  }

  const page = (body: ReactNode) => <SettingsPage className="sandbox-settings" label={t("Sandbox")} group="Agent & models" title="Sandbox"
    description="Where the commands and the file tools of the sessions run." notice={notice} loading={loading} busy={busy} onReload={reload}>{body}</SettingsPage>;
  if (!listing || !listing.availability) return page(<SettingsUnavailable loading={loading} icon="sandbox" title="The sandbox settings are unavailable." />);
  const availability = listing.availability;
  const names = profiles.map(profile => profile.name);
  return page(<div className="sandbox-settings-grid">
    <div className="sandbox-settings-top">
      <SandboxStatus epoch={epoch!} api={api} availability={availability} profiles={names} preferred={listing.allSessions ?? workspaceProfile} />
      <EverySession profiles={names} value={listing.allSessions} disabled={busy}
        onChange={profile => void mutate(() => api.saveAllSessions({ expectedEpoch: epoch, profile }, { timeoutMilliseconds: 30_000 }))} />
    </div>
    <Card className="appearance-settings sandbox-profiles" aria-labelledby="sandbox-profiles-title">
      <h2 id="sandbox-profiles-title">{t("Profiles")}</h2>
      <div className="sandbox-profile-tabs" role="tablist" aria-label={t("Profiles")}>
        {profiles.map(profile => <Button key={profile.name} role="tab" size="small" variant={shown?.name === profile.name && selected !== newProfile ? "solid" : "minimal"}
          aria-selected={shown?.name === profile.name && selected !== newProfile} data-sandbox-profile={profile.name}
          icon={profile.problem ? <AppIcon name="warning" size={13} /> : profile.builtIn ? <AppIcon name="sandbox" size={13} /> : undefined}
          onClick={() => select(profile.name)}>{profile.name}</Button>)}
        {selected === newProfile && draft && <Button role="tab" size="small" variant="solid" aria-selected>{draft.name || t("New profile")}</Button>}
        <Button size="small" variant="minimal" icon={<AppIcon name="plus" size={13} />} disabled={busy} onClick={() => startProfile(null)}>{t("New profile")}</Button>
      </div>
      {selected === newProfile && draft
        ? <ProfileEditor key="new" draft={draft} saved={null} profiles={profiles} networks={availability.networks} busy={busy}
            onChange={setDraft} onSave={next => void saveProfile(next)} onCancel={() => select(null)} />
        : shown?.builtIn
          ? <BuiltInProfile profile={shown} networks={availability.networks} busy={busy} onDerive={() => startProfile(shown.name)} />
          : shown && draft && <ProfileEditor key={shown.name} draft={draft} saved={draftOf(shown)} problem={shown.problem} profiles={profiles} networks={availability.networks} busy={busy}
            onChange={setDraft} onSave={next => void saveProfile(next)} onRemove={() => void removeProfile(shown.name)} />}
    </Card>
    <Card className="appearance-settings sandbox-scopes" aria-labelledby="sandbox-scopes-title">
      <h2 id="sandbox-scopes-title">{t("Where it applies")}</h2>
      <p className="settings-field-help">{t("A session takes the sandbox of its model, else of its provider, of its project, of its space, else the one of every session. It can also have its own.")}</p>
      {listing.scopes.length === 0 ? <p className="sandbox-scopes-none">{t("No provider, model, space or project chooses a sandbox.")}</p>
        : <ul className="sandbox-scope-list">
          {listing.scopes.map(scope => {
            const kind = (scope.kind in sandboxScopeKinds ? scope.kind : "project") as SandboxScopeKind;
            const name = scope.kind === "model" && scope.modelId ? `${scope.name} · ${scope.modelId}` : scope.name;
            return <li key={`${scope.kind}:${scope.id}:${scope.modelId ?? ""}`} data-sandbox-scope={scope.kind}>
              <Button variant="minimal" size="small" className="sandbox-scope-link" icon={<AppIcon name={scopeIcons[kind]} size={14} />}
                disabled={!onOpenScope} title={t("Change it where it is chosen")} onClick={() => onOpenScope?.(scope)}>
                <span className="sandbox-scope-kind">{t(sandboxScopeKinds[kind])}</span><span className="sandbox-scope-name">{name}</span></Button>
              <span className="sandbox-scope-arrow" aria-hidden="true">→</span>
              <Tag minimal round className="sandbox-scope-profile" data-off={scope.profile.toLowerCase() === notSandboxed || undefined}>
                {scope.profile.toLowerCase() === notSandboxed ? t("Not sandboxed") : scope.profile}</Tag>
            </li>;
          })}
        </ul>}
    </Card>
  </div>);
}

// Whether the sandbox runs here, and the check of a profile, its steps shown as they come.
function SandboxStatus({ epoch, api, availability, profiles, preferred }: {
  epoch: string; api: SandboxApi; availability: NonNullable<SandboxSettingsResponse["availability"]>; profiles: readonly string[]; preferred: string;
}) {
  const { t } = useShellLanguage();
  const [profile, setProfile] = useState(preferred);
  const [run, setRun] = useState<CheckRun | null>(null);
  const running = useRef<AbortController | null>(null);
  useEffect(() => () => running.current?.abort(), []);
  const checked = profiles.includes(profile) ? profile : profiles.includes(preferred) ? preferred : profiles[0] ?? workspaceProfile;
  async function check() {
    running.current?.abort();
    const controller = new AbortController();
    running.current = controller;
    const steps: SandboxCheckEvent[] = [];
    setRun({ profile: checked, steps, passed: null, failure: null });
    try {
      for await (const event of await api.check({ expectedEpoch: epoch, profile: checked }, { signal: controller.signal, timeoutMilliseconds: 180_000 })) {
        if (controller.signal.aborted) return;
        if (event.status === "step") { steps.push(event); setRun({ profile: checked, steps: [...steps], passed: null, failure: null }); }
        else if (event.status === "done") { setRun({ profile: checked, steps: [...steps], passed: event.passed ?? false, failure: null }); return; }
        else { setRun({ profile: checked, steps: [...steps], passed: false, failure: event.status }); return; }
      }
      if (!controller.signal.aborted) setRun({ profile: checked, steps: [...steps], passed: false, failure: "no_answer" });
    } catch {
      if (!controller.signal.aborted) setRun({ profile: checked, steps: [...steps], passed: false, failure: "no_answer" });
    } finally { if (running.current === controller) running.current = null; }
  }
  const active = run !== null && run.passed === null;
  return <Card className="appearance-settings sandbox-status" aria-labelledby="sandbox-status-title">
    <h2 id="sandbox-status-title">{t("Status")}</h2>
    <div className="sandbox-availability" data-available={availability.available}>
      <span className="sandbox-availability-dot" aria-hidden="true">●</span>
      <strong>{t(availability.available ? "Available: {backend}" : "Not available: {backend}", { backend: availability.backend })}</strong>
    </div>
    {availability.reason && <p className="settings-field-help sandbox-reason">{availability.reason}</p>}
    {availability.setup && <p className="settings-field-help sandbox-setup"><AppIcon name="info" size={13} /> {availability.setup}</p>}
    <div className="sandbox-check-bar">
      <HTMLSelect aria-label={t("Profile to check")} value={checked} disabled={active} onChange={event => setProfile(event.currentTarget.value)}>
        {profiles.map(name => <option key={name} value={name}>{name}</option>)}
      </HTMLSelect>
      <Button icon={active ? <ActivitySpinner size={14} /> : <AppIcon name="check" size={15} />} disabled={active} onClick={() => void check()}>{t("Check")}</Button>
      {run && run.passed !== null && <Tag minimal intent={run.passed ? "success" : "danger"} className="sandbox-check-result">
        {t(run.passed ? "{profile} passed the check" : "{profile} did not pass the check", { profile: run.profile })}</Tag>}
    </div>
    {run && <ol className="sandbox-check-steps" aria-label={t("Steps of the check")} aria-live="polite">
      {run.steps.map((step, index) => <li key={`${step.id}:${index}`} data-passed={step.passed ?? false}>
        <AppIcon name={step.passed ? "check" : "close"} size={14} />
        <span className="sandbox-check-step">{step.id && checkSteps[step.id] ? t(checkSteps[step.id]) : step.id && commandSteps[step.id] ? <code>{commandSteps[step.id]}</code> : step.id}</span>
        {step.detail && <span className="sandbox-check-detail" title={step.detail}>{step.detail}</span>}
      </li>)}
      {active && <li className="sandbox-check-waiting"><ActivitySpinner size={12} /><span>{t("Checking…")}</span></li>}
      {run.failure && <li data-passed="false"><AppIcon name="error" size={14} /><span>{t("The check did not complete ({status}).", { status: run.failure })}</span></li>}
    </ol>}
  </Card>;
}

/**
 * What the setting of every session says, as the runtime reads it: `off` (in any case) or nothing is no sandbox; a
 * profile is matched without case. `known` is the profile of the list it names, null for one that does not exist.
 */
export function everySessionChoice(profiles: readonly string[], value: string | null): { sandboxed: string | null; known: string | null } {
  const trimmed = value?.trim() ?? "";
  if (trimmed === "" || trimmed.toLowerCase() === "off") return { sandboxed: null, known: null };
  return { sandboxed: trimmed, known: profiles.find(name => name.toLowerCase() === trimmed.toLowerCase()) ?? null };
}

// The sandbox of every session that no other place sandboxes: none, or a profile.
function EverySession({ profiles, value, disabled, onChange }: { profiles: readonly string[]; value: string | null; disabled: boolean; onChange: (profile: string | null) => void }) {
  const { t } = useShellLanguage();
  const { sandboxed, known } = everySessionChoice(profiles, value);
  const [chosen, setChosen] = useState(known ?? (profiles.includes(workspaceProfile) ? workspaceProfile : profiles[0] ?? workspaceProfile));
  useEffect(() => { if (known) setChosen(known); }, [known]);
  // A profile the settings name that does not exist is shown as it is: sessions run no command with it.
  const missing = sandboxed && !known ? sandboxed : null;
  return <Card className="appearance-settings sandbox-every-session" aria-labelledby="sandbox-every-session-title" data-setting="sandbox.all_sessions">
    <h2 id="sandbox-every-session-title">{t("Every session")}</h2>
    <RadioGroup className="sandbox-every-session-choice" selectedValue={sandboxed ? "on" : "off"} disabled={disabled}
      onChange={event => onChange(event.currentTarget.value === "on" ? chosen : null)}>
      <Radio value="off" label={t("Not sandboxed")} />
      <Radio value="on" label={t("Sandboxed with")} />
      <HTMLSelect aria-label={t("Profile of every session")} value={missing ?? chosen} disabled={disabled}
        onChange={event => { const next = event.currentTarget.value; setChosen(next); if (sandboxed) onChange(next); }}>
        {missing && <option value={missing}>{t("{profile}, which cannot be used", { profile: missing })}</option>}
        {profiles.map(name => <option key={name} value={name}>{name}</option>)}
      </HTMLSelect>
    </RadioGroup>
    <p className="settings-field-help">{t("A provider, a model, a space, a project or a session can choose otherwise.")}</p>
  </Card>;
}

const networkChoices: readonly (readonly [string, MessageKey])[] = [["hosts", "Allowed hosts"], ["off", "Off"], ["on", "On"]];
const outsideChoices: readonly (readonly [string, MessageKey])[] = [["ask", "Ask me"], ["never", "Never"]];

// A profile of CodeAlta: what it gives, and a new profile of the user that starts from it.
function BuiltInProfile({ profile, networks, busy, onDerive }: { profile: SandboxProfileItem; networks: readonly string[]; busy: boolean; onDerive: () => void }) {
  const { t } = useShellLanguage();
  return <div className="sandbox-profile" data-built-in="true">
    <p className="settings-field-help">{t("A profile of CodeAlta. A new profile can start from it and add to it.")}</p>
    <ProfileFields values={{ ...draftOf(profile), originalName: profile.name }} resolved={null} base={null} networks={networks} readOnly />
    <div className="sandbox-profile-actions">
      <Button icon={<AppIcon name="duplicate" size={15} />} disabled={busy} onClick={onDerive}>{t("New profile from this")}</Button>
    </div>
  </div>;
}

// A profile of the user: its name, its base, and what it adds or changes, saved with its button.
function ProfileEditor({ draft, saved, problem = null, profiles, networks, busy, onChange, onSave, onCancel, onRemove }: {
  draft: ProfileDraft; saved: ProfileDraft | null; problem?: string | null; profiles: readonly SandboxProfileItem[]; networks: readonly string[]; busy: boolean;
  onChange: (draft: ProfileDraft) => void; onSave: (draft: ProfileDraft) => void; onCancel?: () => void; onRemove?: () => void;
}) {
  const { t } = useShellLanguage();
  const bases = profiles.filter(profile => profile.name.toLowerCase() !== (draft.originalName ?? "").toLowerCase());
  const base = draft.extends ? resolveSandboxProfile(draft.extends, profiles) : null;
  const dirty = !saved || !sameDraft(draft, saved);
  const valid = /^[A-Za-z0-9_-]{1,64}$/.test(draft.name.trim());
  return <div className="sandbox-profile">
    {problem && <Callout compact intent="danger" icon={<AppIcon name="warning" size={16} />} className="sandbox-profile-problem">{problem}</Callout>}
    <Field label={t("Name")} htmlFor="sandbox-profile-name">
      <InputGroup id="sandbox-profile-name" value={draft.name} disabled={busy} maxLength={64} spellCheck={false} intent={valid ? "none" : "danger"}
        onChange={event => onChange({ ...draft, name: event.target.value })} />
    </Field>
    <Field label={t("Starts from")} htmlFor="sandbox-profile-extends">
      <HTMLSelect id="sandbox-profile-extends" value={draft.extends ?? ""} disabled={busy} onChange={event => onChange({ ...draft, extends: event.currentTarget.value || null })}>
        <option value="">{t("Nothing")}</option>
        {bases.map(profile => <option key={profile.name} value={profile.name}>{profile.name}</option>)}
        {draft.extends && !bases.some(profile => profile.name === draft.extends) && <option value={draft.extends}>{draft.extends}</option>}
      </HTMLSelect>
    </Field>
    <ProfileFields values={draft} resolved={base} base={draft.extends} networks={networks} disabled={busy} onChange={onChange} />
    <div className="sandbox-profile-actions">
      <Button intent="primary" disabled={!dirty || !valid || busy} onClick={() => onSave(draft)}>{t("Save")}</Button>
      {saved ? <Button disabled={!dirty || busy} onClick={() => onChange(saved)}>{t("Revert")}</Button>
        : <Button disabled={busy} onClick={onCancel}>{t("Cancel")}</Button>}
      <span className="sandbox-profile-spacer" />
      {onRemove && saved && <RemoveButton name={saved.name} text disabled={busy} onRemove={onRemove} />}
    </div>
  </div>;
}

// The values of a profile: its network and its hosts, its folders, its local ports, its MCP servers, and whether a
// command may run outside it. With a base, a list adds to the one of the base, and an unset choice is the base's.
function ProfileFields({ values, resolved, base, networks, disabled = false, readOnly = false, onChange }: {
  values: ProfileDraft; resolved: Resolved | null; base: string | null; networks: readonly string[];
  disabled?: boolean; readOnly?: boolean; onChange?: (values: ProfileDraft) => void;
}) {
  const { t } = useShellLanguage();
  const off = disabled || readOnly;
  const set = (change: Partial<ProfileDraft>) => onChange?.({ ...values, ...change });
  // What the base gives besides the profile's own entries: a list adds to it.
  const inherited = (own: readonly (string | number)[], from: readonly (string | number)[] | undefined) => {
    const rest = base ? (from ?? []).filter(item => !own.includes(item)) : [];
    return rest.length > 0 ? <p className="settings-field-help sandbox-inherited">{t("From {base}: {values}", { base: base!, values: rest.join(", ") })}</p> : null;
  };
  // A choice of the profile; with a base, the first one leaves it to the base. Without one, an unset choice is `standard`.
  const choice = (name: string, value: string | null, choices: readonly (readonly [string, MessageKey])[], standard: string, fromBase: string | undefined,
    apply: (value: string | null) => void, offered: (value: string) => boolean = () => true) => {
    const baseLabel = choices.find(([key]) => key === fromBase)?.[1];
    return <RadioGroup inline name={name} className="sandbox-choice" disabled={off} selectedValue={value ?? (base ? "" : standard)}
      onChange={event => apply(event.currentTarget.value || null)}>
      {base && <Radio value="" label={baseLabel ? t("As in {base} ({value})", { base, value: t(baseLabel) }) : t("As in {base}", { base })} />}
      {choices.map(([key, label]) => <Radio key={key} value={key} label={t(label)} disabled={off || !offered(key)} />)}
    </RadioGroup>;
  };
  // A list of the profile: its entries as tags to change, or only to read in a profile of CodeAlta.
  const list = (id: string, label: string, items: readonly string[], placeholder: string, apply: (items: readonly string[]) => void) => readOnly
    ? <span id={id} className="sandbox-list-read" role="list" aria-label={label}>
      {items.length ? items.map(item => <Tag key={item} minimal role="listitem">{item}</Tag>) : <span className="sandbox-list-none">{t("None")}</span>}</span>
    : <TagInput inputProps={{ id, "aria-label": label }} className="sandbox-list" values={[...items]} disabled={off} addOnBlur separator={false}
      placeholder={off ? undefined : placeholder} tagProps={{ minimal: true }}
      onAdd={added => { apply([...new Set([...items, ...added.map(item => item.trim()).filter(Boolean)])]); }}
      onRemove={(_, index) => apply(items.filter((__, at) => at !== index))} />;
  const network = values.network ?? resolved?.network ?? (base ? null : "off");
  return <>
    <Field label={t("Network")}>
      {choice("sandbox-network", values.network, networkChoices, "off", resolved?.network, next => set({ network: next }), key => networks.length === 0 || networks.includes(key))}
    </Field>
    {(network === "hosts" || values.hosts.length > 0) && <Field label={t("Allowed hosts")} htmlFor="sandbox-profile-hosts" notice={inherited(values.hosts, resolved?.hosts)}>
      {list("sandbox-profile-hosts", t("Allowed hosts"), values.hosts, t("nuget.org, *.github.com…"), hosts => set({ hosts }))}
    </Field>}
    <Field label={t("Read-write folders")} htmlFor="sandbox-profile-write" notice={<>
      <p className="settings-field-help">{t("Always read-write: the folder of the session, its worktree, its scratchpad and temp.")}</p>
      {inherited(values.write, resolved?.write)}</>}>
      {list("sandbox-profile-write", t("Read-write folders"), values.write, t("Add a folder"), write => set({ write }))}
    </Field>
    <Field label={t("Read-only folders")} htmlFor="sandbox-profile-read" notice={inherited(values.read, resolved?.read)}>
      {list("sandbox-profile-read", t("Read-only folders"), values.read, t("Add a folder"), read => set({ read }))}
    </Field>
    <Field label={t("Never reached")} htmlFor="sandbox-profile-deny" notice={inherited(values.deny, resolved?.deny)}>
      {list("sandbox-profile-deny", t("Never reached"), values.deny, t("Add a folder or a file"), deny => set({ deny }))}
    </Field>
    <Field label={t("Local ports")} htmlFor="sandbox-profile-ports" notice={inherited(values.localPorts, resolved?.localPorts)}>
      {list("sandbox-profile-ports", t("Local ports"), values.localPorts.map(String), t("Add a port"),
        items => set({ localPorts: [...new Set(items.map(Number).filter(port => Number.isInteger(port) && port > 0 && port < 65536))] }))}
    </Field>
    <Field label={t("MCP servers")} htmlFor="sandbox-profile-mcp" notice={inherited(values.mcpServers, resolved?.mcpServers)}>
      {list("sandbox-profile-mcp", t("MCP servers"), values.mcpServers, t("Add an MCP server"), mcpServers => set({ mcpServers }))}
    </Field>
    <Field label={t("Run outside")}>
      {choice("sandbox-outside", values.outside, outsideChoices, "ask", resolved?.outside, outside => set({ outside }))}
    </Field>
  </>;
}
