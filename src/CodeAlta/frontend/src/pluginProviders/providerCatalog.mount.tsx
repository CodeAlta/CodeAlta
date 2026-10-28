// Disposable page: the catalog of the agents of the ACP registry over a host that answers at once, in a window whose
// links the host opens (recorded here). Driven by providerCatalog.browser.test.ts through `catalogFixture`.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { GlobalConfigPluginCatalogResponse } from "#neoastra";
import { MarkdownLinksContext } from "../MarkdownContent";
import { ShellLanguageContext } from "../shellLanguage";
import { ProviderCatalogDialog, type ProviderCatalogApi } from "./ProviderCatalogDialog";

document.documentElement.classList.add("bp6-dark");

const catalog: GlobalConfigPluginCatalogResponse = { status: "ok", error: null, fetchedAt: "2026-10-27T10:00:00Z", title: "ACP registry", entries: [
  { id: "cursor", name: "Cursor", version: "2026.10.22", authors: "Anysphere", description: "The agent of Cursor.", distribution: "binary",
    iconData: null, installedKey: null, unavailable: null, website: "https://cursor.com/cli" },
] };
const api = { pluginCatalog: async () => catalog, installPluginProvider: async () => { throw new Error("Not in this page."); } } as unknown as ProviderCatalogApi;
const state = { opened: [] as string[] };
Object.assign(window, { catalogFixture: state });

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <MarkdownLinksContext.Provider value={address => state.opened.push(address)}>
      <ProviderCatalogDialog epoch="epoch" type="acp" title="ACP agents" api={api} onAdded={() => {}} onOpen={() => {}} onClose={() => {}} />
    </MarkdownLinksContext.Provider>
  </ShellLanguageContext.Provider>
</StrictMode>);
