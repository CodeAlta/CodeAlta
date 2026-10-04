import type { BootStatus } from "#neoastra";
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
export function AboutSettings({ status, bootError, demo, logo }: { status: BootStatus | undefined; bootError: boolean; demo: boolean; logo?: string }) {
  const { t } = useShellLanguage();
  const about = aboutFacts(status, bootError, demo);
  return <main className="configuration-page settings-editor about-settings" aria-label={t("About")}>
    <header className="page-heading"><span className="eyebrow">{t("Diagnostics")}</span><h1>{t("About")}</h1></header>
    <section className="about-settings-card">
      {logo && <img className="about-settings-mark" src={logo} alt="" draggable={false} />}
      <div><strong>{about.product ?? "CodeAlta"}</strong><span>{about.version ?? t("Development build")}</span></div>
    </section>
    <dl className="about-settings-facts" aria-label={t("Running host build information")}>
      <div><dt>{t("Version")}</dt><dd>{about.version ?? t("Development build")}</dd></div>
      {about.build && <div><dt>{t("Build metadata")}</dt><dd>{about.build}</dd></div>}
      <div><dt>{t("Mode")}</dt><dd>{t(about.mode)}</dd></div>
    </dl>
  </main>;
}
