// Disposable page: Settings > Secrets and environment with long names, in the room the Settings window gives the page at
// 1600x1000, or at 900x650 after `secretsFixture.setWidth(430)`. Driven by secretsSettings.browser.test.ts, which reads
// what the page asked in `secretsFixture.calls`.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import type { CredentialProviderRpcView, CredentialTypeView, SecretsList } from "#neoastra";
import { ShellLanguageContext } from "../shellLanguage";
import { SecretsSettings, type SecretsTabFocus } from "./SecretsSettings";
import { localSecret } from "./credentials";
import type { EnvironmentList, SecretsTab } from "./secretsTabs";

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
const bitwarden: CredentialTypeView = {
  type: "bitwarden", displayName: "Bitwarden", description: "Reads items of Bitwarden or Vaultwarden through the bw CLI.", icon: "bitwarden", iconSvg: null,
  website: "https://bitwarden.com/help/cli/", referenceHint: "<item id>/password", unlockAsksPassword: true, builtIn: true, settings: [],
};
const provider = (id: string, type: CredentialTypeView, state: string, extra: Partial<CredentialProviderRpcView> = {}): CredentialProviderRpcView => ({
  id, type: type.type, displayName: type.displayName, state, message: null, typeName: type.displayName, icon: type.icon, account: null, programPath: null,
  signInCommand: null, installCommand: null, installPage: null, cache: "session", unlockAtStart: true, unlockAsksPassword: false, secrets: 1, held: 0, unlocked: false, unlocking: false, settings: [], ...extra,
});
const listing: SecretsList = {
  status: "ok", protection: "dpapi",
  secrets: [
    { ...localSecret({ name: "ANTHROPIC_API_KEY_FOR_THE_PLATFORM_TEAM_AND_ITS_AUTOMATIONS", version: 1, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-12T09:00:00Z" }),
      provider: "personal", providerName: "1Password", icon: "1password", label: "Private › Anthropic › credential", askBeforeUse: "15m", state: "locked",
      stateMessage: "Kept in 1Password, which is locked." },
    { ...localSecret({ name: "DEPLOY_TOKEN", version: 1, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-12T09:00:00Z" }),
      provider: "team", providerName: "A command", icon: "terminal", label: "deploy/prod#token" },
    { ...localSecret({ name: "GRAFANA_SERVICE_ACCOUNT_TOKEN_PRODUCTION_EU_WEST_1", version: 3, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-03T09:00:00Z" }),
      projectIds: ["acme-web"], usedBy: [{ kind: "project", name: "acme-web" }] },
    { ...localSecret({ name: "OPENAI_API_KEY", version: 1, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-01T09:00:00Z" }), usedBy: [{ kind: "provider", name: "openai" }] },
  ],
  projects: [{ id: "acme-web", name: "acme-web", icon: null }, { id: "acme-api", name: "acme-api", icon: null }, { id: "docs", name: "Docs", icon: null }],
  variables: [
    { name: "GRAFANA_URL", value: "https://grafana.example.com", version: 1, setBy: "alex (CodeAlta Desktop)", setAt: "2026-11-01T09:00:00Z" },
    { name: "PLATFORM_TEAM_OBSERVABILITY_ENDPOINT_FOR_STAGING", value: "https://otlp.staging.example.com:4318", version: 1, setBy: "alex (CodeAlta Desktop)",
      setAt: "2026-11-01T09:00:00Z" },
  ],
  providers: [
    provider("personal", onePassword, "locked", { account: "my.1password.com", message: "Unlock to let the 1Password app authorize CodeAlta." }),
    provider("team", command, "ready", { held: 1 }),
    provider("vault", bitwarden, "ready", { unlockAsksPassword: true, unlocked: true }),
  ],
  types: [onePassword, command, bitwarden],
  pending: [{ name: "ANTHROPIC_API_KEY_FOR_THE_PLATFORM_TEAM_AND_ITS_AUTOMATIONS", use: "provider anthropic", at: "2026-11-12T09:05:00Z",
    sessionId: null, awaitedUntil: null, prompt: true }],
  uses: [{ name: "DEPLOY_TOKEN", use: "MCP server deploy", at: "2026-11-12T09:04:00Z", outcome: "given" }],
  verification: { available: true, reason: null },
  canPrompt: true,
  developerInstance: false,
  developerUnlock: false,
  configurationProblems: [],
  signIns: [{ id: "github", label: "GitHub", provider: "personal", sites: ["https://github.com"], username: "op://Private/GitHub/username",
    password: "op://Private/GitHub/password", totp: "op://Private/GitHub/one-time password?attribute=otp", providerName: "1Password", icon: "1password",
    grants: [{ origin: "https://github.com", until: "2026-12-12T09:00:00Z" }] }],
};
// What the projects of the catalog set, as the host reads their configuration: one with variables, one of them naming a
// secret that is not set here; one with a CLI profile only; one with nothing, which is only offered.
const environment: EnvironmentList = {
  status: "ok",
  projects: [
    { id: "acme-web", name: "acme-web", icon: null, path: "C:\\code\\acme-web\\.alta\\config.toml", secrets: ["ACME_GH_TOKEN"], profiles: [], issues: [],
      variables: [
        { name: "GH_TOKEN", value: "${secret:ACME_GH_TOKEN}", local: true, issues: [{ kind: "secret", name: "ACME_GH_TOKEN", state: "not_set", message: null }] },
        { name: "GRAFANA_URL", value: "${var:GRAFANA_URL}", local: true, issues: [] },
        { name: "NODE_ENV", value: "development", local: false, issues: [] }] },
    { id: "acme-api", name: "acme-api", icon: null, path: "C:\\code\\acme-api\\.alta\\config.toml", secrets: [], variables: [],
      profiles: [{ name: "kubectl", variables: ["KUBECONFIG"] }], issues: [] },
    { id: "docs", name: "Docs", icon: null, path: "C:\\code\\docs\\.alta\\config.toml", secrets: [], variables: [], profiles: [], issues: [] },
  ],
  spaces: [{ id: "work", name: "Work", profiles: [{ name: "azure", variables: ["AZURE_CONFIG_DIR"] }] }],
};
// Each change the page asked, by the name of its call.
const calls: Record<string, unknown>[] = [];
const said = (name: string) => async (request: object) => {
  calls.push({ [name]: Object.fromEntries(Object.entries(request).filter(([key]) => key !== "expectedEpoch")) });
  return { status: "ok", message: null, provider: null };
};
// The files and the spaces the page asked the window to open.
const opened: Record<string, string>[] = [];
const ok = async () => ({ status: "ok", message: null });
// Each browse the page asked, and whether its signal aborted it; `slow` keeps a browse waiting, as a vault program that queues.
const browses: { id: string; parent: string | null; aborted: boolean }[] = [];
const control = { slow: false };
const api = {
  list: async () => listing, setSecret: said("setSecret"), rotateSecret: ok, deleteSecret: ok, setVariable: said("setVariable"), deleteVariable: ok,
  setSecretProjects: said("setSecretProjects"), listEnvironment: async () => environment,
  setProjectEnvironment: said("setProjectEnvironment"), deleteProjectEnvironment: said("deleteProjectEnvironment"),
  saveProvider: async () => ({ status: "ok", message: null, provider: null }), removeProvider: async () => ({ status: "ok", message: null, provider: null }),
  unlockProvider: async () => ({ status: "ok", message: null, provider: null }), lockProvider: async () => ({ status: "ok", message: null, provider: null }),
  testProvider: async () => ({ status: "ok", message: null, provider: null }),
  // Two vaults of 1Password: the search at the top finds an item in either, and an item lists its fields. Bitwarden lists its items
  // at the top, each a folder of fields, as BitwardenProvider does.
  browse: async ({ id, parent }: { id: string; parent: string | null }, options?: { signal?: AbortSignal }) => {
    const call = { id, parent, aborted: false };
    browses.push(call);
    options?.signal?.addEventListener("abort", () => { call.aborted = true; });
    if (control.slow) {
      await new Promise((resolve, reject) => {
        const timer = setTimeout(resolve, 60_000);
        options?.signal?.addEventListener("abort", () => { clearTimeout(timer); reject(new Error("aborted")); });
      });
    }
    const folder = (id: string, label: string, detail: string | null = null) => ({ id, label, kind: "folder", reference: null, detail });
    const field = (id: string, reference: string, detail: string | null) => ({ id, label: id, kind: "field", reference, detail });
    const page = (path: string | null, entries: ReturnType<typeof folder | typeof field>[]) => ({ status: "ok", message: null, path, entries });
    if (id === "vault") {
      switch (parent) {
        case null: return page(null, [folder("item:b1", "GitHub", "login"), folder("item:b2", "Server notes", "note")]);
        case "item:b1": return page("GitHub", [field("username", "b1/username", null), field("password", "b1/password", "concealed")]);
        default: return page(null, []);
      }
    }
    switch (parent) {
      case null: return page(null, [folder("vault:v1:Private", "Private"), folder("vault:v2:Work", "Work")]);
      case "vault:v1:Private": return page("Private", [folder("item:v1:i1", "Anthropic", "api credential"), folder("item:v1:i2", "GitHub", "login")]);
      case "vault:v2:Work": return page("Work", [folder("item:v2:i3", "Grafana Cloud", "api credential")]);
      case "item:v1:i2": return page("Private › GitHub", [field("username", "op://Private/GitHub/username", null), field("password", "op://Private/GitHub/password", "concealed")]);
      default: return page(null, []);
    }
  },
  bindSecret: async () => ({ status: "ok", message: null, provider: null }), setAskBeforeUse: async () => ({ status: "ok", message: null, provider: null }),
  confirmUse: async () => ({ status: "ok", message: null, provider: null }), declineUse: async () => ({ status: "ok", message: null, provider: null }),
  saveSignIn: async () => ({ status: "ok", message: null, provider: null }), removeSignIn: async () => ({ status: "ok", message: null, provider: null }),
  revokeSiteGrant: async () => ({ status: "ok", message: null, provider: null }),
  setDeveloperUnlock: async () => ({ status: "ok", message: null, provider: null }),
};
// The width of the page of the Settings window: about 1250 CSS pixels in a window of 1600, about 430 in one of 900.
function Fixture() {
  const [width, setWidth] = useState(1250);
  const [focus, setFocus] = useState<SecretsTabFocus | null>(null);
  // `open(tab)` asks for a tab as a link or `alta settings open` does.
  Object.assign(window, { secretsFixture: { setWidth, browses, control, calls, opened, open: (tab: SecretsTab) => setFocus(current => ({ tab, ticket: (current?.ticket ?? 0) + 1 })) } });
  return <div className="ide-shell settings-dialog-content" style={{ padding: 12, width, boxSizing: "border-box" }}>
    <SecretsSettings epoch="12345678-1234-1234-1234-123456789abc" api={api} focus={focus}
      onOpenFile={(projectId, path) => { opened.push({ file: projectId, path }); }} onOpenSpace={spaceId => { opened.push({ space: spaceId }); }} />
  </div>;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <Fixture />
  </ShellLanguageContext.Provider>
</StrictMode>);
