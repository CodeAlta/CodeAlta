import { useEffect, useRef, useState } from "react";
import { Button, Callout, HTMLSelect, InputGroup, Radio, RadioGroup, TextArea } from "@blueprintjs/core";
import { network, type NetworkCheckResponse, type NetworkProxyResponse } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { SettingsFileLocations } from "../SettingsFileLocation";
import { useSettingsFiles, type SettingsFilesApi } from "../settingsFiles";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import { checkRequest, defaultCheckTarget, describeCheck, formChanged, formError, formOf, saveRequest, type ProxyAuthentication, type ProxyForm, type ProxyMode } from "./network";

type NetworkApi = Pick<typeof network, "read" | "save" | "check">;

/**
 * Settings page for the network: the proxy of CodeAlta and of the programs it starts, saved in the configuration file and
 * applied at once, and a check of the connection through the form as it is.
 */
export function NetworkSettings({ epoch, api = network, onOpenFile, filesApi }: {
  epoch: string | null;
  api?: NetworkApi;
  /** Called once the code editor was asked to show the configuration file: the window leaves Settings. */
  onOpenFile?: () => void;
  /** Says where the configuration file is and opens it; the host by default. */
  filesApi?: SettingsFilesApi;
}) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, setNotice, reload, mutate } = useSettingsEditor(options => api.read({ expectedEpoch: epoch }, options), `${epoch}`);
  const files = useSettingsFiles({ page: "config", epoch, projectId: null, revision: listing, onOpened: onOpenFile, setNotice, api: filesApi });
  return <SettingsPage className="network-settings" label={t("Network")} group="Advanced" title="Network"
    description="How CodeAlta and the programs it starts reach the internet." notice={notice} loading={loading} busy={busy} onReload={reload}>
    {!listing ? <SettingsUnavailable loading={loading} icon="globe" title="Network settings unavailable" /> : <>
      <SettingsFileLocations files={files} disabled={busy} />
      <NetworkView listing={listing} busy={busy}
        onSave={form => void mutate(async () => {
          const reply = await api.save(saveRequest(form, epoch), { timeoutMilliseconds: 30_000 });
          return { status: reply.status, message: reply.message };
        }, "Saved. Programs already running keep their proxy until they restart.")}
        onCheck={(form, target, signal) => api.check(checkRequest(form, target, epoch), { timeoutMilliseconds: 30_000, signal })} />
    </>}
  </SettingsPage>;
}

/** The form of the proxy and the check of the connection, over what the host keeps. */
export function NetworkView({ listing, busy, onSave, onCheck }: {
  listing: NetworkProxyResponse; busy: boolean; onSave: (form: ProxyForm) => void;
  onCheck: (form: ProxyForm, target: string, signal: AbortSignal) => Promise<NetworkCheckResponse>;
}) {
  const { t } = useShellLanguage();
  const [form, setForm] = useState<ProxyForm>(() => formOf(listing));
  // What the host keeps comes back after a save or a reload: the form shows it again.
  useEffect(() => { setForm(formOf(listing)); }, [listing]);
  const change = (next: Partial<ProxyForm>) => setForm(current => ({ ...current, ...next }));
  const error = formError(form, listing.passwordSet);
  const changed = formChanged(form, listing);
  const manual = form.mode === "manual";
  return <>
    {listing.applyError && <Callout intent="warning" compact role="alert">{t("The proxy settings could not be applied as saved.")}
      <div className="config-editor-diagnostic">{listing.applyError}</div></Callout>}
    {listing.pending && <Callout intent="warning" compact role="status">
      {t("The configuration file has proxy settings that do not run yet: save them here, or restart CodeAlta.")}</Callout>}
    <section className="network-block" aria-label={t("Proxy")}>
      <h2>{t("Proxy")}</h2>
      <RadioGroup className="network-modes" data-setting="network.proxy.mode" selectedValue={form.mode} disabled={busy}
        onChange={event => change({ mode: event.currentTarget.value as ProxyMode })}>
        <Radio value="system">
          <span className="network-mode"><strong>{t("Use the system settings")}</strong>
            <small>{t("The variables HTTP_PROXY, HTTPS_PROXY and NO_PROXY of the environment, else the proxy of the operating system.")}</small>
            <small>{listing.systemProxy ? t("Now through {proxy}.", { proxy: listing.systemProxy }) : t("Now without a proxy.")}</small></span>
        </Radio>
        <Radio value="none">
          <span className="network-mode"><strong>{t("No proxy")}</strong><small>{t("Directly, whatever the environment and the system say.")}</small></span>
        </Radio>
        <Radio value="manual">
          <span className="network-mode"><strong>{t("Manual proxy configuration")}</strong></span>
        </Radio>
      </RadioGroup>
      {manual && <div className="network-manual">
        <label className="network-field" data-setting="network.proxy.url"><span>{t("Proxy address")}</span>
          <InputGroup value={form.url} disabled={busy} placeholder="http://proxy.example.com:8080" aria-label={t("Proxy address")}
            onValueChange={url => change({ url })} /></label>
        <label className="network-field" data-setting="network.proxy.no_proxy"><span>{t("No proxy for")}</span>
          <TextArea value={form.bypass} disabled={busy} rows={3} placeholder={"*.corp.example.com\n10.0.0.0/8"} aria-label={t("No proxy for")}
            onChange={event => change({ bypass: event.currentTarget.value })} />
          <small>{t("Hosts, domains, addresses and CIDR ranges reached directly, separated by commas or lines. This computer always is.")}</small></label>
        <label className="network-field" data-setting="network.proxy.authentication"><span>{t("Proxy authentication")}</span>
          <HTMLSelect value={form.authentication} disabled={busy} aria-label={t("Proxy authentication")}
            onChange={event => change({ authentication: event.currentTarget.value as ProxyAuthentication })}
            options={[{ value: "none", label: t("None") }, { value: "account", label: t("My account (integrated)") }, { value: "password", label: t("User name and password") }]} /></label>
        {form.authentication === "account" && <small className="network-note">
          {t("CodeAlta signs in as the account it runs under (Windows integrated authentication, Kerberos). The programs it starts sign in by themselves, if they can.")}</small>}
        {form.authentication === "password" && <>
          <label className="network-field" data-setting="network.proxy.user"><span>{t("User name")}</span>
            <InputGroup value={form.user} disabled={busy} placeholder={t("user or DOMAIN\\user")} aria-label={t("User name")} onValueChange={user => change({ user })} /></label>
          <label className="network-field"><span>{t("Password")}</span>
            <InputGroup type="password" value={form.password} disabled={busy} autoComplete="new-password" aria-label={t("Password")}
              placeholder={listing.passwordSet ? t("Kept as a secret: type to replace it") : ""} onValueChange={password => change({ password })} />
            <small>{t("Kept as the CodeAlta secret NETWORK_PROXY_PASSWORD, never in config.toml. The programs CodeAlta starts receive it in the address of the proxy.")}</small></label>
        </>}
      </div>}
      {error && <Callout intent="warning" compact role="status">{t(error)}</Callout>}
      <div className="network-actions">
        <Button intent="primary" disabled={busy || !changed || !!error} onClick={() => onSave(form)}>{t("Save")}</Button>
        <Button disabled={busy || !changed} onClick={() => setForm(formOf(listing))}>{t("Revert")}</Button>
      </div>
      <small className="network-note">
        {t("A change applies at once to CodeAlta and to the programs it starts from then on. Agent CLIs, MCP servers and terminals already running keep their proxy until they restart. Sandboxed sessions always go through the proxy of their sandbox.")}</small>
    </section>
    <ConnectionCheck form={form} disabled={busy || !!error} onCheck={onCheck} />
  </>;
}

/** Requests an address through the form as it is, and says what came back. */
function ConnectionCheck({ form, disabled, onCheck }: {
  form: ProxyForm; disabled: boolean; onCheck: (form: ProxyForm, target: string, signal: AbortSignal) => Promise<NetworkCheckResponse>;
}) {
  const { t } = useShellLanguage();
  const [target, setTarget] = useState(defaultCheckTarget);
  const [checking, setChecking] = useState(false);
  const [result, setResult] = useState<NetworkCheckResponse | null | "failed">(null);
  const controller = useRef<AbortController | null>(null);
  useEffect(() => () => controller.current?.abort(), []);
  const check = () => {
    controller.current?.abort();
    const current = new AbortController();
    controller.current = current;
    setChecking(true);
    setResult(null);
    onCheck(form, target, current.signal).then(value => { if (!current.signal.aborted) setResult(value); },
      () => { if (!current.signal.aborted) setResult("failed"); }).finally(() => { if (!current.signal.aborted) setChecking(false); });
  };
  const summary = result && result !== "failed" ? describeCheck(result) : null;
  return <section className="network-block" aria-label={t("Check connection")}>
    <h2>{t("Check connection")}</h2>
    <div className="network-check">
      <InputGroup value={target} disabled={checking} aria-label={t("Address to check")} placeholder={defaultCheckTarget} onValueChange={setTarget}
        onKeyDown={event => { if (event.key === "Enter" && !disabled && !checking) check(); }} />
      <Button icon={<AppIcon name="globe" size={15} />} loading={checking} disabled={disabled} onClick={check}>{t("Check connection")}</Button>
    </div>
    <small className="network-note">{t("Requests the address through the settings above, saved or not.")}</small>
    {result === "failed" && <Callout intent="danger" compact role="alert">{t("The connection could not be checked.")}</Callout>}
    {summary && <Callout intent={summary.intent} compact role={summary.intent === "success" ? "status" : "alert"}>
      {t(summary.key, summary.values)}{summary.detail && <div className="config-editor-diagnostic">{summary.detail}</div>}</Callout>}
  </section>;
}
