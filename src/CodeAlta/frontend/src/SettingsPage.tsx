import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { Button, Callout, NonIdealState, PopoverNext, SegmentedControl } from "@blueprintjs/core";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon, type IconName } from "./AppIcon";
import type { MessageKey } from "./localization";
import { settingsFailure, type SettingsNotice, type SettingsScope } from "./settingsEditing";
import { useShellLanguage } from "./shellLanguage";

type CallOptions = { signal: AbortSignal; timeoutMilliseconds: number };
/** The selected, editable project a settings page may also write to; null edits global settings only. */
export type SettingsProject = Readonly<{ id: string; name: string }> | null;

/**
 * Loads one settings listing and runs its mutations: a failed read or write becomes a notice,
 * a successful write reloads the listing. `key` identifies what is listed (host epoch and project).
 */
export function useSettingsEditor<T extends { status: string }>(read: (options: CallOptions) => Promise<T>, key: string) {
  const [listing, setListing] = useState<T | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<SettingsNotice | null>(null);
  const [generation, setGeneration] = useState(0);
  const latest = useRef(read); latest.current = read;
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true);
    latest.current({ signal: controller.signal, timeoutMilliseconds: 20000 }).then(value => {
      if (controller.signal.aborted) return;
      const failure = settingsFailure(value.status);
      if (failure) { setListing(null); setNotice(failure); } else setListing(value);
    }).catch(() => { if (!controller.signal.aborted) { setListing(null); setNotice(settingsFailure("read_failed")); } })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [key, generation]);
  const reload = useCallback(() => setGeneration(value => value + 1), []);
  /** Runs one write; returns whether it succeeded. `policy_failed` saved the item, so it also reloads. */
  const mutate = useCallback(async (call: () => Promise<{ status: string; message?: string | null }>, success?: MessageKey) => {
    setBusy(true); setNotice(null);
    try {
      const result = await call();
      if (!alive.current) return false;
      const failure = settingsFailure(result.status, result.message);
      setNotice(failure ?? (success ? { key: success, intent: "success" } : null));
      if (!failure || result.status === "policy_failed") setGeneration(value => value + 1);
      return !failure;
    } catch {
      if (alive.current) setNotice({ key: "The operation did not complete.", intent: "danger" });
      return false;
    } finally { if (alive.current) setBusy(false); }
  }, []);
  return { listing, loading, busy, notice, setNotice, reload, mutate };
}

/** The common frame of a settings page: heading with actions, one notice line, then the page body. */
export function SettingsPage({ className, label, group, title, description, actions, notice, loading, busy, onReload, children }: {
  className?: string; label: string; group: MessageKey; title: MessageKey; description?: MessageKey;
  actions?: ReactNode; notice: SettingsNotice | null; loading: boolean; busy: boolean; onReload: () => void; children: ReactNode;
}) {
  const { t } = useShellLanguage();
  return <main className={`configuration-page settings-editor${className ? ` ${className}` : ""}`} aria-label={label}>
    <header className="page-heading settings-editor-heading">
      <div><span className="eyebrow">{t(group)}</span><h1>{t(title)}</h1>{description && <p>{t(description)}</p>}</div>
      <div className="settings-editor-actions">
        {(loading || busy) && <ActivitySpinner size={14} />}
        <Button icon={<AppIcon name="refresh" size={15} />} disabled={loading || busy} onClick={onReload}>{t("Reload")}</Button>
        {actions}
      </div>
    </header>
    {notice && <Callout intent={notice.intent} compact role={notice.intent === "success" ? "status" : "alert"}>
      {t(notice.key)}{notice.detail && <div className="config-editor-diagnostic">{notice.detail}</div>}</Callout>}
    {children}
  </main>;
}

/** Shown in place of the page body while the first read is running or after it failed. */
export function SettingsUnavailable({ loading, icon, title }: { loading: boolean; icon: IconName; title: MessageKey }) {
  const { t } = useShellLanguage();
  return <NonIdealState icon={loading ? <ActivitySpinner size={28} /> : <AppIcon name={icon} size={36} />} title={t(loading ? "Loading…" : title)} />;
}

/**
 * The red button that removes what a page can create (a prompt, a skill, a plugin, a server): the name of what goes
 * is confirmed in a popover first. With `text` it is labeled, as at the foot of a form; without, it is the icon alone,
 * as in a row.
 */
export function RemoveButton({ name, text = false, disabled, onRemove }: { name: string; text?: boolean; disabled?: boolean; onRemove: () => void }) {
  const { t } = useShellLanguage();
  return <PopoverNext placement="top-end" content={<div className="provider-settings-confirm"><p>{t("Remove {name}?", { name })}</p>
    <Button className="bp6-popover-dismiss" intent="danger" disabled={disabled} onClick={onRemove}>{t("Remove")}</Button></div>}>
    {text ? <Button variant="minimal" intent="danger" icon={<AppIcon name="trash" size={15} />} disabled={disabled} text={t("Remove")} />
      : <Button variant="minimal" size="small" intent="danger" icon={<AppIcon name="trash" size={15} />} disabled={disabled}
        aria-label={t("Remove {name}", { name })} title={t("Remove")} />}
  </PopoverNext>;
}

/** Chooses whether a change is stored for the user (global) or for the selected project. */
export function ScopeChoice({ value, onChange, project, disabled }: {
  value: SettingsScope; onChange: (scope: SettingsScope) => void; project: SettingsProject; disabled?: boolean;
}) {
  const { t } = useShellLanguage();
  if (!project) return null;
  return <SegmentedControl className="settings-scope" size="small" disabled={disabled} value={value} onValueChange={next => onChange(next as SettingsScope)}
    options={[{ label: t("Global"), value: "Global" }, { label: t("Project: {name}", { name: project.name }), value: "Project" }]} />;
}
