// Disposable page: Settings > Secrets with long names, in the room the Settings window gives the page at 1600x1000, or
// at 900x650 after `secretsFixture.setWidth(430)`. Driven by secretsSettings.browser.test.ts.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { CredentialProviderRpcView, CredentialTypeView, SecretsList } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { SecretsSettings } from "./SecretsSettings";
import { localSecret } from "./credentials";

document.documentElement.classList.add("bp6-dark");

const onePassword: CredentialTypeView = {
  type: "1password", displayName: "1Password", description: "Reads items of 1Password through its op CLI.", icon: "1password", iconSvg: null,
  website: "https://developer.1password.com/docs/cli/get-started/", referenceHint: "op://Private/Anthropic/credential", unlockAsksPassword: false, builtIn: true,
  settings: [{ key: "account", label: "Account", kind: "text", description: null, placeholder: "my.1password.com", required: false }],
};
const command: CredentialTypeView = {
  type: "command", displayName: "A command", description: "Runs a command line that prints the secret.", icon: "terminal", iconSvg: null, website: null,
  referenceHint: "item#field", unlockAsksPassword: false, builtIn: true,
  settings: [{ key: "read", label: "Command that prints a secret", kind: "list", description: null, placeholder: "pass-cli item view {ref}", required: true }],
};
const provider = (id: string, type: CredentialTypeView, state: string, extra: Partial<CredentialProviderRpcView> = {}): CredentialProviderRpcView => ({
  id, type: type.type, displayName: type.displayName, state, message: null, typeName: type.displayName, icon: type.icon, account: null, programPath: null,
  signInCommand: null, installCommand: null, installPage: null, cache: "session", unlockAtStart: true, unlockAsksPassword: false, secrets: 1, held: 0, unlocked: false, settings: [], ...extra,
});
const listing: SecretsList = {
  status: "ok", protection: "dpapi",
  secrets: [
    { ...localSecret({ name: "ANTHROPIC_API_KEY_FOR_THE_PLATFORM_TEAM_AND_ITS_AUTOMATIONS", version: 1, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-12T09:00:00Z" }),
      provider: "personal", providerName: "1Password", icon: "1password", label: "Private › Anthropic › credential", askBeforeUse: "15m", state: "locked",
      stateMessage: "Kept in 1Password, which is locked." },
    { ...localSecret({ name: "DEPLOY_TOKEN", version: 1, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-12T09:00:00Z" }),
      provider: "team", providerName: "A command", icon: "terminal", label: "deploy/prod#token" },
    localSecret({ name: "GRAFANA_SERVICE_ACCOUNT_TOKEN_PRODUCTION_EU_WEST_1", version: 3, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-03T09:00:00Z" }),
    localSecret({ name: "OPENAI_API_KEY", version: 1, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-01T09:00:00Z" }),
  ],
  variables: [
    { name: "GRAFANA_URL", value: "https://grafana.example.com", version: 1, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-01T09:00:00Z" },
    { name: "PLATFORM_TEAM_OBSERVABILITY_ENDPOINT_FOR_STAGING", value: "https://otlp.staging.example.com:4318", version: 1, setBy: "alex (CodeAlta Desktop)",
      setAt: "2026-11-01T09:00:00Z" },
  ],
  providers: [
    provider("personal", onePassword, "locked", { account: "my.1password.com", message: "Unlock to let the 1Password app authorize CodeAlta." }),
    provider("team", command, "ready", { held: 1 }),
  ],
  types: [onePassword, command],
  pending: [{ name: "ANTHROPIC_API_KEY_FOR_THE_PLATFORM_TEAM_AND_ITS_AUTOMATIONS", use: "provider anthropic", at: "2026-11-12T09:05:00Z" }],
  uses: [{ name: "DEPLOY_TOKEN", use: "MCP server deploy", at: "2026-11-12T09:04:00Z", outcome: "given" }],
  verification: { available: true, reason: null },
  canPrompt: true,
  configurationProblems: [],
  signIns: [{ id: "github", label: "GitHub", provider: "personal", sites: ["https://github.com"], username: "op://Private/GitHub/username",
    password: "op://Private/GitHub/password", totp: "op://Private/GitHub/one-time password?attribute=otp", providerName: "1Password", icon: "1password",
    grants: [{ origin: "https://github.com", until: "2026-12-12T09:00:00Z" }] }],
};
const ok = async () => ({ status: "ok", message: null });
const api = {
  list: async () => listing, setSecret: ok, rotateSecret: ok, deleteSecret: ok, setVariable: ok, deleteVariable: ok,
  saveProvider: async () => ({ status: "ok", message: null, provider: null }), removeProvider: async () => ({ status: "ok", message: null, provider: null }),
  unlockProvider: async () => ({ status: "ok", message: null, provider: null }), lockProvider: async () => ({ status: "ok", message: null, provider: null }),
  testProvider: async () => ({ status: "ok", message: null, provider: null }),
  browse: async () => ({ status: "ok", message: null, path: null, entries: [{ id: "vault:v1:Private", label: "Private", kind: "folder", reference: null, detail: null }] }),
  bindSecret: async () => ({ status: "ok", message: null, provider: null }), setAskBeforeUse: async () => ({ status: "ok", message: null, provider: null }),
  confirmUse: async () => ({ status: "ok", message: null, provider: null }), declineUse: async () => ({ status: "ok", message: null, provider: null }),
  saveSignIn: async () => ({ status: "ok", message: null, provider: null }), removeSignIn: async () => ({ status: "ok", message: null, provider: null }),
  revokeSiteGrant: async () => ({ status: "ok", message: null, provider: null }),
};
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
