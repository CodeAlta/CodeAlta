import { useCallback, useEffect, useRef, useState } from "react";
import { Button, InputGroup, Menu, MenuDivider, MenuItem, PopoverNext, Tag } from "@blueprintjs/core";
import { secrets, type SecretsList } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";
import { CredentialIcon } from "./secrets/CredentialIcon";
import { stateLabel } from "./secrets/credentials";
import { isSecretName, referenceOf } from "./secrets/secrets";
import { VaultBindingDialog } from "./secrets/VaultPicker";

/** What the editor needs of the secrets of this computer: their names, the vaults, and a secret bound to an item of a vault. */
export type EnvironmentSecretsApi = Pick<typeof secrets, "list" | "browse" | "bindSecret">;

type Row = Readonly<{ name: string; value: string }>;

/** The rows of the text of an environment: one `NAME=value` per line, a line without `=` being a name alone. */
export function environmentRows(text: string): Row[] {
  return text.replaceAll("\r\n", "\n").split("\n").filter(line => line.trim().length > 0).map(line => {
    const equals = line.indexOf("=");
    return equals < 0 ? { name: line.trim(), value: "" } : { name: line.slice(0, equals), value: line.slice(equals + 1) };
  });
}

/** The text of the rows of an environment; a row left empty is not written. */
export function environmentRowsText(rows: readonly Row[]): string {
  return rows.filter(row => row.name.trim() || row.value).map(row => `${row.name.trim()}=${row.value}`).join("\n");
}

/** The name of the secret a value is, when the value is one reference to a secret (`${secret:NAME}`) and nothing else. */
export function secretOfValue(value: string): string | null {
  return /^\$\{secret:([^}]+)\}$/.exec(value.trim())?.[1] ?? null;
}

/**
 * The variables a provider starts its program with: a row each, with its name and its value. A value can be a secret of this
 * computer instead of a text: the key button lists the secrets and binds an item of a vault, and the row then shows the name
 * of the secret, where it is kept and whether it is set, never its value. The text edited is one `NAME=value` per line.
 */
export function ProviderEnvironment({ id, epoch, value, disabled, onChange, api = secrets, namePlaceholder = "NAME" }: {
  id: string; epoch: string; value: string; disabled: boolean; onChange: (text: string) => void; api?: EnvironmentSecretsApi; namePlaceholder?: string;
}) {
  const { t } = useShellLanguage();
  const [rows, setRows] = useState<readonly Row[]>(() => environmentRows(value));
  // The text the rows were last written as: another text is a change from outside (a revert, a save), which the rows follow.
  const written = useRef(value);
  useEffect(() => {
    if (value === written.current) return;
    written.current = value; setRows(environmentRows(value));
  }, [value]);
  const [listing, setListing] = useState<SecretsList | null>(null);
  // The secrets could not be read: the menu says so, and the rows are still edited as text.
  const [listFailed, setListFailed] = useState(false);
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    let alive = true;
    api.list({ expectedEpoch: epoch }).then(reply => { if (!alive) return; if (reply.status === "ok") { setListing(reply); setListFailed(false); } else setListFailed(true); },
      () => { if (alive) setListFailed(true); });
    return () => { alive = false; };
  }, [api, epoch, generation]);
  const root = useRef<HTMLDivElement>(null);
  // The field that takes the focus once the rows are drawn again: the name of a row just added, the value of a row whose secret was left.
  const focusNext = useRef<Readonly<{ index: number; field: "name" | "value" }> | null>(null);
  useEffect(() => {
    const wanted = focusNext.current;
    if (!wanted) return;
    focusNext.current = null;
    root.current?.querySelectorAll(".provider-environment-row")[wanted.index]?.querySelector<HTMLInputElement>(`.provider-environment-${wanted.field} input`)?.focus();
  }, [rows]);
  // The binding of a vault item that is under way: one that was dismissed, or whose editor is gone, changes no row when it ends.
  const bindingRun = useRef(0);
  useEffect(() => () => { bindingRun.current++; }, []);
  const browse = useCallback((vault: string, parent: string | null, signal?: AbortSignal) => api.browse({ expectedEpoch: epoch, id: vault, parent }, { signal }), [api, epoch]);
  // The row whose value an item of a vault is chosen for.
  const [binding, setBinding] = useState<number | null>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);

  function change(next: readonly Row[]) {
    setRows(next);
    const text = environmentRowsText(next);
    written.current = text;
    if (text !== value) onChange(text);
  }
  const edit = (index: number, row: Partial<Row>) => change(rows.map((current, at) => at === index ? { ...current, ...row } : current));
  const known = listing?.secrets ?? [];
  const vaults = listing?.providers ?? [];

  return <div ref={root} className="provider-environment" id={id} role="group" aria-label={t("Environment")}>
    {rows.map((row, index) => {
      const secretName = secretOfValue(row.value);
      const secret = secretName ? known.find(item => item.name.toLowerCase() === secretName.toLowerCase()) : null;
      const state = secret ? stateLabel(secret.state) : null;
      const source = secret?.provider ? t("Kept in {vault}", { vault: `${secret.providerName ?? secret.provider}${secret.label ? ` › ${secret.label}` : ""}` }) : null;
      // The buttons of a row are named after its variable: a screen reader tells the rows apart.
      const named = (label: string) => row.name.trim() ? `${label}: ${row.name.trim()}` : label;
      return <div key={index} className="provider-environment-row" role="group" aria-label={row.name.trim() || t("Name of the variable")}>
        <InputGroup className="provider-environment-name" value={row.name} disabled={disabled} spellCheck={false} placeholder={namePlaceholder} aria-label={t("Name of the variable")}
          intent={!row.name.trim() && row.value ? "danger" : "none"} onChange={event => edit(index, { name: event.target.value.replaceAll("=", "") })} />
        <span className="provider-environment-equals" aria-hidden="true">=</span>
        {secretName
          ? <div className="provider-environment-secret" data-secret={secretName} title={t("The value is read from this secret when the provider starts. It is never shown here.")}>
              {secret?.provider ? <CredentialIcon icon={secret.icon} size={14} /> : <AppIcon name="secret" size={14} />}
              <span className="provider-environment-secret-kind">{t("Secret")}</span>
              <code title={secretName}>{secretName}</code>
              {source && <span className="provider-environment-secret-source" title={source}>{source}</span>}
              {listing && !secret && <Tag minimal intent="warning">{t("Not set on this computer")}</Tag>}
              {secret && state && secret.state !== "ready" && <Tag minimal intent={state.intent} title={secret.stateMessage ?? undefined}>{t(state.label)}</Tag>}
              <Button variant="minimal" size="small" disabled={disabled} icon={<AppIcon name="edit" size={13} />} aria-label={named(t("Type a value instead"))} title={t("Type a value instead")}
                onClick={() => { focusNext.current = { index, field: "value" }; edit(index, { value: "" }); }} />
            </div>
          : <InputGroup className="provider-environment-value" value={row.value} disabled={disabled} spellCheck={false} placeholder={t("Value")} aria-label={t("Value of the variable")}
              onChange={event => edit(index, { value: event.target.value })} />}
        <PopoverNext placement="bottom-end" disabled={disabled} content={<Menu className="provider-environment-secrets" aria-label={t("Use a secret")}>
          {vaults.length > 0 && <MenuItem icon={<CredentialIcon icon={vaults[0].icon} size={14} />} text={t("Use a vault item…")} onClick={() => { setProblem(null); setBinding(index); }} />}
          <MenuDivider title={t("Use a secret")} />
          {known.map(item => <MenuItem key={item.name} text={item.name} selected={secretName?.toLowerCase() === item.name.toLowerCase()}
            icon={item.provider ? <CredentialIcon icon={item.icon} size={14} /> : <AppIcon name="secret" size={14} />}
            label={item.provider ? item.providerName ?? item.provider : undefined} onClick={() => edit(index, { value: referenceOf("secret", item.name) })} />)}
          {known.length === 0 && <MenuItem disabled text={t(listing ? "No secret on this computer yet" : listFailed ? "The secrets could not be read." : "Loading…")} />}
          <MenuDivider />
          <li className="provider-environment-secrets-hint">{t("Secrets and vaults are added in Settings > Secrets.")}</li>
        </Menu>}>
          <Button disabled={disabled} icon={<AppIcon name="secret" size={14} />} aria-haspopup="menu" aria-label={named(t("Use a secret"))} title={t("Use a secret or a vault item as the value")} />
        </PopoverNext>
        <Button variant="minimal" disabled={disabled} icon={<AppIcon name="close" size={14} />} aria-label={named(t("Remove the variable"))} title={t("Remove the variable")}
          onClick={() => change(rows.filter((_, at) => at !== index))} />
      </div>;
    })}
    <Button variant="minimal" size="small" className="provider-environment-add" disabled={disabled} icon={<AppIcon name="plus" size={14} />} text={t("Add a variable")}
      onClick={() => { focusNext.current = { index: rows.length, field: "name" }; setRows([...rows, { name: "", value: "" }]); }} />
    {problem && <span className="secret-problem" role="alert">{problem}</span>}
    {binding !== null && listing && <VaultBindingDialog providers={vaults} types={listing.types} names={known.map(item => item.name)}
      localNames={known.filter(item => !item.provider).map(item => item.name)}
      initialName={isSecretName(rows[binding]?.name.trim() ?? "") ? rows[binding].name.trim() : undefined} nameEditable
      busy={busy} browse={browse} onClose={() => { bindingRun.current++; setBusy(false); setBinding(null); }}
      onBind={async chosen => {
        const run = ++bindingRun.current;
        setBusy(true); setProblem(null);
        // A binding that fails closes the dialog: what went wrong is said under the rows, where it is read.
        const failed = (message: string | null) => { setProblem(message ?? t("The secret could not be kept in the vault.")); setBusy(false); setBinding(null); return false; };
        try {
          const result = await api.bindSecret({ expectedEpoch: epoch, name: chosen.name, provider: chosen.provider, reference: chosen.reference, label: chosen.label, replaceLocal: chosen.replaceLocal });
          if (run !== bindingRun.current) return false;
          if (result.status !== "ok") return failed(result.message);
          edit(binding, { value: referenceOf("secret", chosen.name) });
          setBusy(false); setGeneration(current => current + 1);
          return true;
        } catch { return run === bindingRun.current ? failed(null) : false; }
      }} />}
  </div>;
}
