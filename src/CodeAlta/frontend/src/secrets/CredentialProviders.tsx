import { useState } from "react";
import { Button, Card, InputGroup, NonIdealState, PopoverNext, Switch, Tag } from "@blueprintjs/core";
import type { CredentialProviderRpcView, SecretsList } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ConfirmPopover } from "../ConfirmPopover";
import { useShellLanguage } from "../shellLanguage";
import { CredentialIcon } from "./CredentialIcon";
import { commandLine, stateLabel } from "./credentials";

type Result = Promise<{ status: string; message?: string | null }>;

/** What the card of the providers does: each call is one change, and says how it went. */
export type CredentialProviderActions = Readonly<{
  unlock: (id: string, password: string | null) => Result;
  lock: (id: string) => Result;
  test: (id: string) => Result;
  remove: (id: string) => Result;
  edit: (provider: CredentialProviderRpcView) => void;
  runInTerminal?: (command: string) => void;
  /** Lets the developer instance ask a vault to unlock, or no longer; absent where the page offers no such switch. */
  setDeveloperUnlock?: (enabled: boolean) => Result;
}>;

/**
 * The credential providers of the user: this computer first, then each vault with its logo, its account and its state, and
 * what it needs (unlock, sign in, install). A value is never shown.
 */
export function CredentialProviders({ listing, busy, run, actions, onAdd }: {
  listing: SecretsList; busy: boolean; run: (call: () => Result) => Promise<boolean>; actions: CredentialProviderActions; onAdd: () => void;
}) {
  const { t } = useShellLanguage();
  const local = listing.secrets.filter(secret => !secret.provider).length;
  return <Card className="appearance-settings credential-providers" aria-labelledby="credential-providers-title">
    <div className="credential-providers-heading">
      <h2 id="credential-providers-title">{t("Vaults")}</h2>
      <Button size="small" icon={<AppIcon name="plus" size={14} />} text={t("Add a vault…")} disabled={busy} onClick={onAdd} />
    </div>
    <p className="credential-providers-intro">{t("Where the values of the secrets are kept: this computer, or a vault CodeAlta reads through its program and never writes into.")}</p>
    {listing.configurationProblems.length > 0 && <ul className="credential-problems" role="alert">
      {listing.configurationProblems.map(problem => <li key={problem}>{problem}</li>)}
    </ul>}
    <ul className="credential-provider-list" aria-label={t("Vaults")}>
      <li className="credential-provider-row" data-provider="this-computer">
        <span className="credential-provider-icon"><AppIcon name="computer" size={18} /></span>
        <span className="credential-provider-name"><strong>{t("This computer")}</strong>
          <span className="credential-provider-detail">{protectionName(listing.protection, t)}{" · "}{secretCount(local, t)}</span></span>
        <Tag minimal intent="success">{t("Ready")}</Tag>
        <span className="credential-provider-actions" />
      </li>
      {listing.providers.map(provider => <ProviderRow key={provider.id} provider={provider} busy={busy} run={run} actions={actions} canPrompt={listing.canPrompt} />)}
    </ul>
    {listing.providers.length === 0 && <NonIdealState className="credential-providers-empty" icon={<AppIcon name="secret" size={24} />}
      description={t("Add 1Password, Bitwarden, KeePassXC or another vault to keep the values of your secrets there.")} />}
    {/* A setting of the user that both instances read: off, the developer instance reads only what a vault gives without asking. */}
    {listing.providers.length > 0 && actions.setDeveloperUnlock && <div className="credential-developer-unlock">
      <Switch checked={listing.developerUnlock} disabled={busy} label={t("Let the developer instance unlock the vaults")}
        onChange={event => { const enabled = event.currentTarget.checked; void run(() => actions.setDeveloperUnlock!(enabled)); }} />
      <p>{t("It then asks your vault for its own run when you click Unlock in its window, as 1Password asks Windows Hello. Off, it reads only what a vault gives without asking.")}</p>
    </div>}
  </Card>;
}

/** A number of secrets, in the singular for one. */
function secretCount(count: number, t: ReturnType<typeof useShellLanguage>["t"]): string {
  return count === 1 ? t("1 secret") : t("{count} secrets", { count: String(count) });
}

function protectionName(protection: string | null, t: ReturnType<typeof useShellLanguage>["t"]): string {
  switch (protection) {
    case "dpapi": return t("Windows data protection");
    case "keychain": return t("macOS Keychain");
    case "libsecret": return t("Keyring of the desktop");
    case "file": return t("A key file only you can read");
    default: return t("Encrypted for you");
  }
}

/** One vault: its logo, name, type and account, its state, and the action its state needs. */
function ProviderRow({ provider, busy, run, actions, canPrompt }: {
  provider: CredentialProviderRpcView; busy: boolean; run: (call: () => Result) => Promise<boolean>; actions: CredentialProviderActions; canPrompt: boolean;
}) {
  const { t } = useShellLanguage();
  const [removing, setRemoving] = useState(false);
  const state = stateLabel(provider.state);
  const detail = [provider.typeName ?? provider.type, provider.account].filter(Boolean).join(" · ");
  return <li className="credential-provider-row" data-provider={provider.id} data-state={provider.state}>
    <span className="credential-provider-icon"><CredentialIcon icon={provider.icon} size={18} /></span>
    <span className="credential-provider-name">
      <strong title={provider.id}>{provider.displayName}</strong>
      <span className="credential-provider-detail" title={provider.programPath ?? undefined}>{detail}
        {provider.secrets > 0 && <>{" · "}{secretCount(provider.secrets, t)}</>}</span>
      {provider.message && provider.state !== "ready" && <span className="credential-provider-message">{provider.message}</span>}
      {provider.state === "not_installed" && <InstallHint provider={provider} runInTerminal={actions.runInTerminal} />}
    </span>
    <Tag minimal intent={state.intent}>{t(state.label)}</Tag>
    <span className="credential-provider-actions">
      {provider.state === "locked" && (canPrompt
        ? <UnlockButton provider={provider} busy={busy} onUnlock={password => run(() => actions.unlock(provider.id, password))} />
        : <Tag minimal title={t("The developer instance asks no vault to unlock: unlock it in the normal instance, or turn on Let the developer instance unlock the vaults, below.")}>{t("Unlock in the normal instance")}</Tag>)}
      {provider.state === "sign_in" && provider.signInCommand && actions.runInTerminal
        && <Button size="small" icon={<AppIcon name="terminal" size={14} />} text={t("Sign in…")} disabled={busy}
          onClick={() => actions.runInTerminal!(commandLine(provider.signInCommand!))} />}
      {provider.state === "ready" && (provider.held > 0 || provider.unlocked) && <Button size="small" variant="minimal" icon={<AppIcon name="lock" size={14} />} text={t("Lock")} disabled={busy}
        title={t("Forget the values read from this vault until it is unlocked again.")} onClick={() => void run(() => actions.lock(provider.id))} />}
      <Button size="small" variant="minimal" icon={<AppIcon name="refresh" size={14} />} aria-label={t("Check {name}", { name: provider.displayName })} disabled={busy}
        onClick={() => void run(() => actions.test(provider.id))} />
      <Button size="small" variant="minimal" icon={<AppIcon name="edit" size={14} />} aria-label={t("Edit {name}", { name: provider.displayName })} disabled={busy}
        onClick={() => actions.edit(provider)} />
      <span className="secret-delete">
        <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} aria-label={t("Remove {name}", { name: provider.displayName })}
          disabled={busy} onClick={() => setRemoving(true)} />
        {removing && <ConfirmPopover always intent="danger" busy={busy} placement="bottom-end" title={t("Remove this provider?")} subject={provider.displayName}
          detail={provider.secrets > 1 ? t("Its {count} secrets are removed with it. Nothing changes in the vault.", { count: String(provider.secrets) })
            : provider.secrets === 1 ? t("Its secret is removed with it. Nothing changes in the vault.") : t("Nothing changes in the vault.")}
          confirmLabel={t("Remove")} onConfirm={() => { setRemoving(false); void run(() => actions.remove(provider.id)); }} onCancel={() => setRemoving(false)} />}
      </span>
    </span>
  </li>;
}

/** Unlock: the vault shows its own prompt, or, for a vault that takes its master password, a field that is never kept. */
function UnlockButton({ provider, busy, onUnlock }: { provider: CredentialProviderRpcView; busy: boolean; onUnlock: (password: string | null) => Promise<boolean> }) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const [password, setPassword] = useState("");
  if (!provider.unlockAsksPassword) {
    return <Button size="small" intent="primary" icon={<AppIcon name="secret" size={14} />} text={t("Unlock")} disabled={busy} onClick={() => void onUnlock(null)} />;
  }

  const submit = async () => {
    const typed = password;
    setPassword("");
    if (await onUnlock(typed)) setOpen(false);
  };
  return <PopoverNext isOpen={open} placement="bottom-end" onInteraction={next => { if (!next && !busy) { setOpen(false); setPassword(""); } }}
    content={<form className="credential-unlock" onSubmit={event => { event.preventDefault(); if (password) void submit(); }}>
      <strong>{t("Unlock {name}", { name: provider.displayName })}</strong>
      <InputGroup type="password" autoComplete="off" autoFocus small value={password} disabled={busy} aria-label={t("Master password of {name}", { name: provider.displayName })}
        onChange={event => setPassword(event.target.value)} />
      <p>{t("Given to the program of the vault once, never kept.")}</p>
      <div className="rename-actions">
        <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={() => { setOpen(false); setPassword(""); }} />
        <Button type="submit" intent="primary" size="small" text={t("Unlock")} disabled={busy || !password} />
      </div>
    </form>}>
    <Button size="small" intent="primary" icon={<AppIcon name="secret" size={14} />} text={t("Unlock…")} disabled={busy} onClick={() => setOpen(true)} />
  </PopoverNext>;
}

/** How to install the program of a vault: the command of the package manager, run in a terminal the user sees, and its page. */
function InstallHint({ provider, runInTerminal }: { provider: CredentialProviderRpcView; runInTerminal?: (command: string) => void }) {
  const { t } = useShellLanguage();
  const command = provider.installCommand ? commandLine(provider.installCommand) : null;
  return <span className="credential-install">
    {command && <code>{command}</code>}
    {command && runInTerminal && <Button size="small" icon={<AppIcon name="terminal" size={13} />} text={t("Run in a terminal")} onClick={() => runInTerminal(command)} />}
    {provider.installPage && <a href={provider.installPage} target="_blank" rel="noreferrer">{t("How to install it")}</a>}
  </span>;
}
