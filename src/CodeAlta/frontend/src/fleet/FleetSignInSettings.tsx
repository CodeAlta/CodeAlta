import { useEffect, useRef, useState } from "react";
import { Button, Callout, Dialog, DialogBody, DialogFooter, HTMLSelect, InputGroup, Menu, MenuItem, PopoverNext, Switch, Tag } from "@blueprintjs/core";
import { fleetServer, type FleetServerView, type FleetSignInLoginView, type FleetSignInSettingsView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { serverProblem } from "./fleetServer";
import { groupRoleChoices, hasCompany, linkableMethods, signInCode, signInDraft, signInDraftProblem, signInProblem, signInSummary, type SignInCode as Code, type SignInDraft } from "./signIn";
import { SignInCode } from "./SignInCode";

/** The calls of the sign-in of a server. */
export type FleetSignInApi = Pick<typeof fleetServer, "signInSettings" | "setSignInSettings" | "linkSignIn" | "startSignInCode" | "openSignInPage">;

const methodNames: Record<string, string> = { microsoft: "Microsoft", google: "Google", github: "GitHub" };

/**
 * The sign-in of a server, in its card of Settings > Fleet > Server: the methods linked to the person of this computer (a
 * passkey, Microsoft, Google or GitHub), with Link… to add one through the browser (GitHub shows a code to enter there); and, for an owner, how people sign in
 * to the organization, with Change… for the methods, the company's OpenID Connect provider and whether it is required.
 */
export function ServerSignIn({ server, epoch, api = fleetServer, revision }: { server: FleetServerView; epoch: string; api?: FleetSignInApi; revision: number }) {
  const { t } = useShellLanguage();
  const [settings, setSettings] = useState<FleetSignInSettingsView | null>(null);
  const [logins, setLogins] = useState<readonly FleetSignInLoginView[] | null>(null);
  const [editing, setEditing] = useState(false);
  const [linking, setLinking] = useState<string | null>(null);
  const [deviceCode, setDeviceCode] = useState<Code | null>(null);
  const work = useRef<AbortController | null>(null);
  useEffect(() => () => work.current?.abort(), []);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    const abort = new AbortController();
    api.signInSettings({ expectedEpoch: epoch, linkId: server.id }, { signal: abort.signal, timeoutMilliseconds: 20_000 }).then(value => {
      if (abort.signal.aborted) return;
      setSettings(value.status === "ok" ? value.settings : null);
      setLogins(value.status === "ok" ? value.logins : null);
    }, () => { if (!abort.signal.aborted) { setSettings(null); setLogins(null); } });
    return () => abort.abort();
  }, [api, epoch, server.id, revision, generation]);

  async function link(method: string) {
    const abort = new AbortController();
    work.current = abort;
    setLinking(method);
    setProblem(null);
    try {
      // GitHub: a code to enter at GitHub first, then the wait for it under its handle.
      let handle: string | null = null;
      if (method === "github") {
        const started = await api.startSignInCode({ expectedEpoch: epoch, method, url: null, linkId: server.id }, { signal: abort.signal, timeoutMilliseconds: 30_000 });
        if (abort.signal.aborted) return;
        const code = started.status === "ok" ? signInCode(started.code) : null;
        if (!code) { setProblem({ key: signInProblem(started.status) ?? serverProblem(started.status) ?? "The operation did not complete.", detail: started.message }); return; }
        setDeviceCode(code);
        handle = code.handle;
      }
      const result = await api.linkSignIn({ expectedEpoch: epoch, linkId: server.id, method, handle }, { signal: abort.signal, timeoutMilliseconds: 11 * 60_000 });
      if (abort.signal.aborted) return;
      if (result.status === "ok") { setGeneration(value => value + 1); return; }
      setProblem({ key: signInProblem(result.status) ?? serverProblem(result.status) ?? "The operation did not complete.", detail: result.message });
    } catch {
      if (!abort.signal.aborted) setProblem({ key: "The server does not answer." });
    } finally {
      if (work.current === abort) { work.current = null; setLinking(null); setDeviceCode(null); }
    }
  }

  function cancelLink() {
    work.current?.abort();
    work.current = null;
    setLinking(null);
    setDeviceCode(null);
    setProblem({ key: "The sign-in was cancelled." });
  }

  if (!logins) return null;
  // What the person may link: a passkey always, an account of a method the server offers.
  const offered = linkableMethods.filter(method => method === "passkey" || !settings || settings.methods.some(item => item.id === method && item.enabled && item.available));
  const summary = settings ? signInSummary(settings) : null;
  return <div className="fleet-server-sign-in">
    <AppIcon name="shield" size={15} />
    <div className="fleet-sign-in-main">
      <strong>{t("Sign-in")}</strong>
      {summary && <span className="muted-text fleet-sign-in-summary">{t(summary.key, { methods: summary.methods })}</span>}
      <span className="fleet-sign-in-logins" aria-label={t("Your sign-in methods")}>
        {logins.length === 0 ? <span className="muted-text">{t("Your sign-in methods")}: {t("None yet.")}</span>
          : logins.map((login, index) => <Tag key={`${login.method}:${index}`} minimal round title={login.name}>
            {login.method === "passkey" ? t("Passkey") : methodNames[login.method] ?? login.method} · {login.name}</Tag>)}
      </span>
      {linking && (deviceCode ? <SignInCode epoch={epoch} code={deviceCode} api={api} onCancel={cancelLink} />
        : <span className="muted-text">{t("Finish signing in in your browser…")}</span>)}
      {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </div>
    <PopoverNext placement="bottom-end" content={<Menu>
      {offered.map(method => <MenuItem key={method} text={method === "passkey" ? t("Add a passkey") : t("Link {method}", { method: methodNames[method] })}
        onClick={() => void link(method)} />)}
    </Menu>}>
      <Button size="small" variant="minimal" disabled={!!linking} icon={linking ? <ActivitySpinner size={14} /> : undefined}>{t("Link…")}</Button>
    </PopoverNext>
    {settings && <Button size="small" variant="minimal" onClick={() => setEditing(true)}>{t("Change…")}</Button>}
    {editing && settings && <SignInDialog server={server} epoch={epoch} api={api} view={settings}
      onClose={changed => { setEditing(false); if (changed) setGeneration(value => value + 1); }} />}
  </div>;
}

/** The groups of the company's provider that give a role at each sign-in: the widest of a person's groups applies, a member in none; owners keep theirs. */
function GroupRoles({ draft, working, update }: { draft: SignInDraft; working: boolean; update: (change: Partial<SignInDraft>) => void }) {
  const { t } = useShellLanguage();
  const names: Record<string, MessageKey> = { admin: "Admin", member: "Member", auditor: "Auditor", guest: "Guest" };
  const set = (index: number, change: Partial<{ group: string; role: string }>) =>
    update({ groupRoles: draft.groupRoles.map((role, at) => at === index ? { ...role, ...change } : role) });
  return <div className="fleet-sign-in-groups" role="group" aria-label={t("Roles from groups")}>
    <span>{t("Roles from groups")}</span>
    {draft.groupRoles.map((role, index) => <div key={index} className="fleet-sign-in-group">
      <InputGroup value={role.group} disabled={working} spellCheck={false} placeholder={t("Group id")} aria-label={t("Group id")}
        onChange={event => set(index, { group: event.target.value })} />
      <HTMLSelect value={role.role} disabled={working} aria-label={t("Role")} onChange={event => set(index, { role: event.target.value })}
        options={groupRoleChoices.map(choice => ({ value: choice, label: t(names[choice]) }))} />
      <Button size="small" variant="minimal" icon={<AppIcon name="trash" size={14} />} disabled={working}
        aria-label={t("Remove the group {name}", { name: role.group || t("Group id") })} onClick={() => update({ groupRoles: draft.groupRoles.filter((_role, at) => at !== index) })} />
    </div>)}
    <Button size="small" variant="minimal" icon={<AppIcon name="plus" size={14} />} disabled={working} text={t("Add a group")}
      onClick={() => update({ groupRoles: [...draft.groupRoles, { group: "", role: "member" }] })} />
    {draft.groupRoles.length > 0 && <small className="muted-text">{t("At each company sign-in, a person gets the widest role of their groups, member in none. Owners keep their role.")}</small>}
  </div>;
}

/** How people sign in to the organization: the methods, the company's OpenID Connect provider and whether it is required. */
function SignInDialog({ server, epoch, api, view, onClose }: {
  server: FleetServerView; epoch: string; api: Pick<FleetSignInApi, "setSignInSettings">; view: FleetSignInSettingsView; onClose: (changed: boolean) => void;
}) {
  const { t } = useShellLanguage();
  const [draft, setDraft] = useState<SignInDraft>(() => signInDraft(view));
  const [working, setWorking] = useState(false);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const invalid = signInDraftProblem(draft);
  const update = (change: Partial<SignInDraft>) => setDraft(value => ({ ...value, ...change }));
  const toggle = (method: string, on: boolean) => update({ enabled: on ? [...draft.enabled.filter(item => item !== method), method] : draft.enabled.filter(item => item !== method) });
  async function save() {
    if (invalid) return;
    setWorking(true);
    setProblem(null);
    try {
      const company = hasCompany(draft);
      const result = await api.setSignInSettings({ expectedEpoch: epoch, linkId: server.id, version: view.version, enabled: draft.enabled,
        issuer: company ? draft.issuer.trim() : null, clientId: company ? draft.clientId.trim() : null, clientSecret: company && draft.clientSecret ? draft.clientSecret : null,
        displayName: company ? draft.displayName.trim() || null : null, groupsClaim: company ? draft.groupsClaim.trim() || null : null, requireSso: draft.requireSso,
        groupRoles: company ? draft.groupRoles.map(role => ({ group: role.group.trim(), role: role.role })) : null },
        { timeoutMilliseconds: 60_000 });
      if (result.status === "ok") { onClose(true); return; }
      setProblem({ key: signInProblem(result.status) ?? serverProblem(result.status) ?? "The operation did not complete.", detail: result.message });
    } catch {
      setProblem({ key: "The server does not answer." });
    } finally { setWorking(false); }
  }
  const company = view.methods.find(method => method.id === "company");
  return <Dialog isOpen className="fleet-sign-in-dialog" title={t("How people sign in")} canOutsideClickClose={false} onClose={() => { if (!working) onClose(false); }}>
    <DialogBody>
      <form className="fleet-server-form" onSubmit={event => { event.preventDefault(); void save(); }}>
        <div className="fleet-sign-in-methods" role="group" aria-label={t("Sign in with")}>
          {view.methods.filter(method => method.id !== "company").map(method => <Switch key={method.id} disabled={working || !method.available && !draft.enabled.includes(method.id)}
            checked={draft.enabled.includes(method.id)} onChange={event => toggle(method.id, event.currentTarget.checked)}
            labelElement={<span>{method.id === "passkey" ? t("Passkey") : method.name}{!method.available && <span className="muted-text"> · {t("Not registered yet")}</span>}</span>} />)}
        </div>
        <Switch checked={draft.enabled.includes("company")} disabled={working} label={t("Company sign-in")} onChange={event => toggle("company", event.currentTarget.checked)} />
        <label><span>{t("Issuer")}</span>
          <InputGroup value={draft.issuer} disabled={working} placeholder="https://login.microsoftonline.com/<tenant>/v2.0" spellCheck={false}
            onChange={event => update({ issuer: event.target.value })} /></label>
        <div className="fleet-telemetry-choices">
          <label><span>{t("Client id")}</span>
            <InputGroup value={draft.clientId} disabled={working} spellCheck={false} onChange={event => update({ clientId: event.target.value })} /></label>
          <label><span>{t("Client secret")}</span>
            <InputGroup type="password" autoComplete="off" value={draft.clientSecret} disabled={working} placeholder={view.hasClientSecret ? "••••" : undefined}
              title={view.hasClientSecret ? t("A secret is set: leave empty to keep it.") : undefined} onChange={event => update({ clientSecret: event.target.value })} /></label>
        </div>
        <div className="fleet-telemetry-choices">
          <label><span>{t("Name shown")}</span>
            <InputGroup value={draft.displayName} disabled={working} placeholder={company?.name ?? "Contoso"} onChange={event => update({ displayName: event.target.value })} /></label>
          <label><span>{t("Groups claim")}</span>
            <InputGroup value={draft.groupsClaim} disabled={working} spellCheck={false} onChange={event => update({ groupsClaim: event.target.value })} /></label>
        </div>
        <small className="muted-text">{t("Redirect to register at the provider: http://127.0.0.1 (any port).")}</small>
        {hasCompany(draft) && <GroupRoles draft={draft} working={working} update={update} />}
        <Switch checked={draft.requireSso} disabled={working} label={t("Require company sign-in")} onChange={event => update({ requireSso: event.currentTarget.checked })} />
        {draft.requireSso && <small className="muted-text">{t("Only company sign-in lets a new computer in; your passkey stays as the owner's way in.")}</small>}
        <button type="submit" hidden />
      </form>
      {(problem ?? (invalid ? { key: invalid } : null)) && <Callout compact intent="warning" role="alert">{t((problem ?? { key: invalid! }).key)}
        {problem?.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={working} text={t("Cancel")} onClick={() => onClose(false)} />
      <Button intent="primary" disabled={!!invalid || working} icon={working ? <ActivitySpinner size={14} /> : undefined} text={t("Save")} onClick={() => void save()} />
    </>} />
  </Dialog>;
}
