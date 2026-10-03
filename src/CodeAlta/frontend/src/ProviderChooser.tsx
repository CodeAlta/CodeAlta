import { useLayoutEffect, useRef, useState } from "react";
import { HTMLSelect } from "@blueprintjs/core";
import { sessionOperations, type SessionProviderChoices } from "#neoastra";
import { useShellLanguage } from "./shellLanguage";

export function ProviderChooser({ epoch, sessionId, providerKey, disabled, current, onSelected, onBusyChange }: {
  epoch: string; sessionId: string; providerKey: string; disabled: boolean;
  current: () => boolean; onSelected: () => Promise<void>;
  /** Reports an admitted switch so a hosting popover can stay mounted until it settles. */
  onBusyChange?: (busy: boolean) => void;
}) {
  const { t } = useShellLanguage();
  const [busy, setBusy] = useState(false);
  useLayoutEffect(() => { onBusyChange?.(busy); return () => { if (busy) onBusyChange?.(false); }; }, [busy]);
  const [choices, setChoices] = useState<SessionProviderChoices | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const scope = useRef<AbortController | null>(null), submitting = useRef(false);
  const latest = useRef({ current, disabled, epoch, sessionId, providerKey }); latest.current = { current, disabled, epoch, sessionId, providerKey };
  const valid = (controller: AbortController) => scope.current === controller && !controller.signal.aborted
    && latest.current.epoch === epoch && latest.current.sessionId === sessionId && latest.current.providerKey === providerKey
    && !latest.current.disabled && latest.current.current();
  const admitted = (controller: AbortController) => scope.current === controller && !controller.signal.aborted
    && latest.current.epoch === epoch && latest.current.sessionId === sessionId && latest.current.current();
  async function load() {
    if (disabled || !current() || submitting.current) return;
    scope.current?.abort();
    const controller = new AbortController(); scope.current = controller;
    setChoices(null); setStatus(null);
    try {
      const value = await sessionOperations.providerChoices({ expectedEpoch: epoch, sessionId }, { signal: controller.signal });
      if (!valid(controller)) return;
      if (value.status !== "ok" || value.epoch !== epoch || value.sessionId !== sessionId || value.providerKey !== providerKey
        || !value.runtimeInstanceId || !value.revision || value.providers.length > 32) { setStatus(value.status === "ok" ? "stale_selection" : value.status); return; }
      setChoices(value);
    } catch { if (valid(controller)) setStatus("unavailable"); }
  }
  useLayoutEffect(() => {
    submitting.current = false; setBusy(false); setChoices(null); setStatus(null);
    return () => { scope.current?.abort(); scope.current = null; };
  }, [epoch, sessionId, providerKey]);
  useLayoutEffect(() => {
    // A host transition can disable idle controls while an admitted selection
    // settles. Keep its completion owner, but never admit another mutation.
    if (submitting.current) return;
    scope.current?.abort(); scope.current = null; setChoices(null);
    if (!disabled) void load();
  }, [epoch, sessionId, providerKey, disabled]);
  async function select(provider: string) {
    const controller = scope.current;
    if (!controller || !valid(controller) || submitting.current || !choices || provider === providerKey
      || choices.providerKey !== providerKey || !choices.providers.some(value => value.id === provider)
      || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
    submitting.current = true; setBusy(true); setStatus(null); setChoices(null);
    try {
      const result = await sessionOperations.selectProvider({ expectedEpoch: epoch, sessionId, providerKey: provider,
        expectedProviderKey: choices.providerKey!, runtimeInstanceId: choices.runtimeInstanceId!,
        attachmentGeneration: choices.attachmentGeneration, revision: choices.revision! }, { signal: controller.signal });
      if (!admitted(controller)) return;
      if (result.epoch !== epoch || result.sessionId !== sessionId || result.status !== "ok") { setStatus(result.status === "ok" ? "stale_selection" : result.status); setChoices(null); return; }
      await onSelected();
    } catch { if (admitted(controller)) { setStatus("selection_unconfirmed"); setChoices(null); } }
    finally { if (scope.current === controller) { submitting.current = false; setBusy(false); } }
  }
  return <span className="provider-chooser">
    <HTMLSelect fill className="current-provider" aria-label={t("Change provider")} value={providerKey} disabled={disabled || busy}
      title={t("History and draft are kept. The provider connects on your next Send.")}
      onFocus={() => { if (!choices && !busy) void load(); }} onChange={event => void select(event.currentTarget.value)}>
      {!choices?.providers.some(provider => provider.id === providerKey) && <option value={providerKey}>{providerKey}</option>}
      {choices?.providers.map(provider => <option key={provider.id} value={provider.id}>{provider.name}</option>)}
    </HTMLSelect>
    {busy && <span role="status">{t("Switching provider…")}</span>}
    {status && <span role="alert">{t("Provider selection unavailable ({status}).", { status })}</span>}
  </span>;
}
