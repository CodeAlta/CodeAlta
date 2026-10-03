import { AppWindowSurface } from "./AppWindow";
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { copySessionId, dismissSessionInfoOnKey, type SessionInfoView } from "./sessionInfo";
import { sessionRuntimeState, sessionUsage } from "#neoastra";
import type { RuntimeTarget } from "./runtimeObservations";
import { readSessionInfoObservations, unavailableInfoObservations, type InfoField, type InfoObservations } from "./sessionInfoObservations";
import { canonicalInfoCopy, infoDemoDescription, infoFieldLabel } from "./sessionInfoPresentation";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

export type SessionInfoLifetime = { revision: number; current: () => boolean };

export function SessionInfoDialog({ info, demo, onClose, target = null, lifetime, canRead = () => true }: {
  info: SessionInfoView; demo: boolean; onClose: () => void; target?: RuntimeTarget | null;
  lifetime?: SessionInfoLifetime; canRead?: () => boolean;
}) {
  const { t } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const alive = useRef(false);
  const copying = useRef(false);
  const composingEscape = useRef(false);
  const closing = useRef(false);
  const [feedback, setFeedback] = useState<"copied" | "unavailable" | "failed" | null>(null);
  const [copyDetails, setCopyDetails] = useState(false);
  const [observations, setObservations] = useState(() => unavailableInfoObservations(target && !demo ? "Not requested. Refresh explicitly." : "Unavailable: recorded-only, archived, demo or unverified scope."));
  const [notice, setNotice] = useState<MessageKey | null>(target && !demo ? "Not requested. Refresh explicitly." : "Unavailable: recorded-only, archived, demo or unverified scope.");
  function unavailable(message: MessageKey) {
    setObservations(unavailableInfoObservations(message));
    setNotice(message);
  }
  const [pending, setPending] = useState(false);
  const generation = useRef(0);
  const request = useRef<AbortController | null>(null);
  const identity = useRef<InfoObservations["identity"]>(undefined);
  const current = () => alive.current && !closing.current && (lifetime?.current() ?? true) && (!target || canRead());
  useLayoutEffect(() => {
    const element = dialog.current;
    alive.current = true;
    element?.showModal();
    return () => { alive.current = false; generation.current++; request.current?.abort(); if (element?.open) element.close(); };
  }, []);
  useLayoutEffect(() => {
    generation.current++; request.current?.abort(); copying.current = false;
    identity.current = undefined;
    setFeedback(null); setPending(false);
    unavailable(target && !demo ? "Not requested. Refresh explicitly." : "Unavailable: recorded-only, archived, demo or unverified scope.");
  }, [lifetime?.revision, target === null, target?.request.expectedHostEpoch, target?.request.scope, target?.request.projectId,
    target?.request.projectPath, target?.request.createdAt, demo, info.id, info.createdAt, info.path, info.title,
    info.titleTruncated, info.scope, info.scopeWarning, info.provider, info.updatedAt, info.canCopyId]);
  function close() {
    if (closing.current) return;
    closing.current = true;
    generation.current++; request.current?.abort();
    onClose();
  }

  async function copy(details = false) {
    if (!info.canCopyId || copying.current || !current()) return;
    const version = generation.current;
    copying.current = true;
    setCopyDetails(details);
    setFeedback(null);
    const text = details ? canonicalInfoCopy(info, demo, observations) : info.id;
    const result = text === null ? "failed" : await copySessionId(text,
      () => navigator.clipboard ? text => navigator.clipboard.writeText(text) : undefined);
    if (version !== generation.current || !current()) return;
    copying.current = false;
    setFeedback(result);
  }

  async function refresh() {
    if (!current() || !target || demo || !canRead()) return;
    request.current?.abort(); const controller = new AbortController(); request.current = controller;
    const version = ++generation.current;
    copying.current = false; setFeedback(null); setPending(true);
    unavailable("Loading explicit observation…");
    const valid = () => current() && canRead() && version === generation.current && !controller.signal.aborted;
    try {
      const result = await readSessionInfoObservations(target, sessionRuntimeState.observe, sessionUsage.read, controller.signal, valid);
      if (valid()) {
        const prior = identity.current; const observed = result.identity;
        if (prior && observed && (prior.runtime !== observed.runtime || prior.attachment && observed.attachment && BigInt(observed.attachment) < BigInt(prior.attachment)))
          unavailable("Unavailable: runtime changed or older attachment refused.");
        else {
          if (observed) identity.current = { ...observed, attachment: observed.attachment ?? prior?.attachment ?? null };
          setObservations(result);
          setNotice(null); // Controller-produced values/outcomes stay literal.
        }
      }
    } catch { if (valid()) unavailable("Error: read failed; no observation established."); }
    finally { if (valid()) setPending(false); }
  }
  // Show the observed details as soon as the window opens (and again for a changed selection); the title-bar
  // button reads them again. Nothing is polled.
  const refreshOnOpen = useRef(refresh);
  refreshOnOpen.current = refresh;
  useEffect(() => { void refreshOnOpen.current(); }, [lifetime?.revision, target === null, target?.request.sessionId, target?.request.expectedHostEpoch, demo]);
  const recorded = (value: string) => {
    const date = new Date(value);
    return <time dateTime={value} title={value}>{Number.isNaN(date.getTime()) ? value : date.toLocaleString()}</time>;
  };
  // Rows the host knows nothing about are left out of the window (the copied details keep every row).
  const reported = (value: string) => !/^(?:Unknown|false)(?:\s*\/\s*(?:Unknown|false))*$/.test(value);
  const fields = (rows: readonly InfoField[]) => <dl className="session-info-fields" tabIndex={0}>{rows.filter(([, value], index) => !!notice || index === 0 || reported(String(value))).map(([label, value]) => {
    const key = infoFieldLabel(label);
    return <div key={label}><dt>{key ? t(key) : label}</dt><dd>{notice ? t(notice) : value}</dd></div>;
  })}</dl>;

  return <dialog ref={dialog} className="app-dialog session-info-dialog" aria-modal="true" aria-labelledby="session-info-title"
    onKeyDown={event => {
      event.stopPropagation(); // Do not run shell chords while this native modal is open.
      if ((event.key === "Enter" || event.key === " ") && (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.repeat)) {
        event.preventDefault(); return;
      }
      if (event.key !== "Escape") return;
      event.preventDefault();
      if (dismissSessionInfoOnKey({ key: event.key, isComposing: event.nativeEvent.isComposing, keyCode: event.nativeEvent.keyCode })) close();
      else composingEscape.current = true;
    }}
    onKeyUp={() => { composingEscape.current = false; }} onCompositionEnd={() => { composingEscape.current = false; }}
    onCancel={event => { event.preventDefault(); if (!composingEscape.current) close(); }}>
    <AppWindowSurface storageKey="codealta.desktop.window.session-info.v1" title={t("Session info")} titleId="session-info-title" preferredSize={viewport => ({ width: Math.min(640, viewport.width - 40), height: Math.min(720, viewport.height - 40) })}
      onClose={close} closeLabel={t("Close session info")}
      headerActions={<Button variant="minimal" size="small" icon={<AppIcon name="refresh" size={15} />} disabled={!target || demo || pending || !canRead()}
        aria-label={t("Refresh observed details")} title={t("Refresh observed details")} onClick={() => void refresh()} />}>
    <section data-info-copy aria-labelledby="session-info-saved"><h3 id="session-info-saved">{t("Saved metadata")}</h3>
    <dl className="session-info-fields" tabIndex={0} aria-label={t("Recorded session information")}>
      <div><dt>{t("Session ID")}</dt><dd><code>{info.id || t("Not recorded")}</code></dd></div>
      <div><dt>{t("Title")}</dt><dd>{info.title}{info.titleTruncated && <small>{t("Title shortened in the bounded snapshot.")}</small>}</dd></div>
      <div><dt>{t("Scope")}</dt><dd>{info.scope}{info.scopeWarning && <small>{info.scopeWarning}</small>}</dd></div>
      <div><dt>{t("Recorded working directory")}</dt><dd>{info.path ?? t("Not recorded or unverified")}</dd></div>
      <div><dt>{t("Provider")}</dt><dd>{info.provider ?? t("Not recorded or unverified")}</dd></div>
      <div><dt>{t("Saved update")}</dt><dd>{info.updatedAt ? recorded(info.updatedAt) : t("Not recorded or unverified")}</dd></div>
      <div><dt>{t("Recorded creation time")}</dt><dd>{info.createdAt ? recorded(info.createdAt) : t("Not recorded or unavailable")}</dd></div>
    </dl>
    </section>
    <section data-info-copy aria-labelledby="session-info-runtime"><h3 id="session-info-runtime">{t("Observed runtime configuration")}</h3>
      {fields(observations.runtime)}</section>
    <section data-info-copy aria-labelledby="session-info-usage"><h3 id="session-info-usage">{t("Last-observed usage")}</h3>
      {fields(observations.usage)}</section>
    {demo && <p className="session-info-note" data-info-copy>{t(infoDemoDescription)}</p>}
    <footer><span>{pending && <span role="status">{t("Reading observed details…")}</span>}{feedback && <span role={feedback === "copied" ? "status" : "alert"}>
      {t(feedback === "unavailable" ? "Clipboard unavailable; nothing copied." : copyDetails ? feedback === "copied" ? "Displayed details copied." : "Could not copy displayed details." : feedback === "copied" ? "Session ID copied." : "Could not copy session ID.")}
    </span>}</span><span><Button icon={<AppIcon name="copy" size={15} />} disabled={!info.canCopyId || pending} title={t("Copy uses canonical English labels and literal data; maximum 32768 characters.")} text={t("Copy displayed details")} onClick={() => void copy(true)} />
      <Button disabled={!info.canCopyId} text={t("Copy session ID")} onClick={() => void copy()} />
      <Button intent="primary" text={t("Close")} onClick={close} /></span></footer>
  </AppWindowSurface></dialog>;
}
