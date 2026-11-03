// Disposable page: Settings > Secrets with long names, in the room the Settings window gives the page at 1600x1000, or
// at 900x650 after `secretsFixture.setWidth(430)`. Driven by secretsSettings.browser.test.ts.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { SecretsList } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { SecretsSettings } from "./SecretsSettings";

document.documentElement.classList.add("bp6-dark");

const listing: SecretsList = {
  status: "ok", protection: "dpapi",
  secrets: [
    { name: "GRAFANA_SERVICE_ACCOUNT_TOKEN_PRODUCTION_EU_WEST_1", version: 3, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-03T09:00:00Z" },
    { name: "JIRA_API_TOKEN_FOR_THE_PLATFORM_TEAM_AUTOMATIONS", version: 1, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-02T09:00:00Z" },
    { name: "OPENAI_API_KEY", version: 1, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-01T09:00:00Z" },
  ],
  variables: [
    { name: "GRAFANA_URL", value: "https://grafana.example.com", version: 1, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-01T09:00:00Z" },
    { name: "PLATFORM_TEAM_OBSERVABILITY_ENDPOINT_FOR_STAGING", value: "https://otlp.staging.example.com:4318", version: 1, setBy: "alex (CodeAlta Desktop)",
      setAt: "2026-11-01T09:00:00Z" },
  ],
};
const ok = async () => ({ status: "ok", message: null });
const api = { list: async () => listing, setSecret: ok, rotateSecret: ok, deleteSecret: ok, setVariable: ok, deleteVariable: ok };
// The width of the page of the Settings window: about 1250 CSS pixels in a window of 1600, about 430 in one of 900.
function Fixture() {
  const [width, setWidth] = useState(1250);
  Object.assign(window, { secretsFixture: { setWidth } });
  return <div className="ide-shell settings-dialog-content" style={{ padding: 12, width, boxSizing: "border-box" }}>
    <SecretsSettings epoch="12345678-1234-1234-1234-123456789abc" api={api} />
  </div>;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <Fixture />
  </ShellLanguageContext.Provider>
</StrictMode>);
