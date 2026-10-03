import { AppWindowSurface } from "./AppWindow";
import { useLayoutEffect, useRef } from "react";
import type { BootStatus } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { useShellLanguage } from "./shellLanguage";
import type { MessageKey } from "./localization";

function recordedText(value: unknown, maxLength: number): string | null {
  return typeof value === "string" && value.length <= maxLength && value.trim().length > 0 &&
    !/[\u0000-\u001f\u007f-\u009f\u202a-\u202e\u2066-\u2069]/u.test(value) ? value : null;
}

type About = { product: string | null; version: string | null; build: string | null; mode: MessageKey };

/** What the running host reported about itself; nothing is inferred when it reported nothing. */
function aboutFacts(status: BootStatus | undefined, bootError: boolean, demo: boolean): About {
  const browserDemo = demo || status?.state === "demo";
  const verified = !browserDemo && !bootError && !!status;
  const version = verified ? recordedText(status.version, 256) : null;
  const versionKnown = version && version !== "development" ? version : null;
  const buildSuffix = versionKnown?.match(/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\+([0-9A-Za-z.-]+)$/u)?.[1];
  return { product: verified ? recordedText(status.productName, 128) : null, version: versionKnown, build: recordedText(buildSuffix, 128),
    mode: browserDemo ? "Browser demo" : bootError ? "Desktop host unavailable" : !status ? "Starting…"
      : status.hostAvailable && recordedText(status.hostEpoch, 256) ? "Desktop app" : "Catalog only" };
}

/** The About page of Settings: product, version and build of the running app. */
export function AboutSettings({ status, bootError, demo }: { status: BootStatus | undefined; bootError: boolean; demo: boolean }) {
  const { t } = useShellLanguage();
  const about = aboutFacts(status, bootError, demo);
  return <main className="configuration-page settings-editor about-settings" aria-label={t("About")}>
    <header className="page-heading"><span className="eyebrow">{t("Diagnostics")}</span><h1>{t("About")}</h1></header>
    <section className="about-settings-card">
      <span className="about-settings-mark" aria-hidden="true">A</span>
      <div><strong>{about.product ?? "CodeAlta"}</strong><span>{about.version ?? t("Development build")}</span></div>
    </section>
    <dl className="about-settings-facts" aria-label={t("Running host build information")}>
      <div><dt>{t("Version")}</dt><dd>{about.version ?? t("Development build")}</dd></div>
      {about.build && <div><dt>{t("Build metadata")}</dt><dd>{about.build}</dd></div>}
      <div><dt>{t("Mode")}</dt><dd>{t(about.mode)}</dd></div>
    </dl>
  </main>;
}

export function AboutDialog({ status, bootError, demo, onClose }: {
  status: BootStatus | undefined; bootError: boolean; demo: boolean; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const dialog = useRef<HTMLDialogElement>(null);
  const closing = useRef(false);
  const composingEscape = useRef(false);
  useLayoutEffect(() => {
    const element = dialog.current;
    element?.showModal();
    return () => { if (element?.open) element.close(); };
  }, []);
  function close() { if (!closing.current) { closing.current = true; onClose(); } }

  const about = aboutFacts(status, bootError, demo);
  return <dialog ref={dialog} className="app-dialog session-info-dialog about-dialog" aria-modal="true"
    aria-labelledby="about-title"
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key !== "Escape") return;
      event.preventDefault();
      if (!event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229 && !event.repeat) close();
      else composingEscape.current = true;
    }} onKeyUp={() => { composingEscape.current = false; }} onCompositionEnd={() => { composingEscape.current = false; }}
    onCancel={event => { event.preventDefault(); if (!composingEscape.current) close(); }}>
    <AppWindowSurface storageKey="codealta.desktop.window.about.v1" title={t("About CodeAlta")} titleId="about-title" preferredSize={viewport => ({ width: Math.min(560, viewport.width - 40), height: Math.min(480, viewport.height - 40) })}
      onClose={close} closeLabel={t("Close About")}>
    <dl className="session-info-fields" tabIndex={0} aria-label={t("Running host build information")}>
      <div><dt>{t("Product")}</dt><dd>{about.product ?? "CodeAlta"}</dd></div>
      <div><dt>{t("Version")}</dt><dd>{about.version ?? t("Development build")}</dd></div>
      {about.build && <div><dt>{t("Build metadata")}</dt><dd>{about.build}</dd></div>}
      <div><dt>{t("Mode")}</dt><dd>{t(about.mode)}</dd></div>
    </dl>
  </AppWindowSurface></dialog>;
}
