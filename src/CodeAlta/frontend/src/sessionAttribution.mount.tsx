// Disposable page: Settings > Sessions over a host that answers at once and checks the texts of attribution as the
// host does. Driven by sessionAttribution.browser.test.ts through `attributionFixture`.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { GlobalConfigAttributionSettingsResponse, GlobalConfigSaveAttributionSettingsRequest } from "#neoastra";
import { SessionSettings } from "./SessionSettings";
import { ShellLanguageContext } from "./shellLanguage";

document.documentElement.classList.add("bp6-dark");

const defaultCoAuthorTrailer = "Co-authored-by: CodeAlta <noreply@codealta.io>";
const defaultExternalWritingInstruction = "When you write in an external system on the user's behalf, end the text with a line saying that it comes from CodeAlta.";
// What the host holds; an empty text is the one of CodeAlta.
const held = { coAuthor: true, coAuthorTrailer: defaultCoAuthorTrailer, externalWriting: true, externalWritingInstruction: defaultExternalWritingInstruction };
const state = { saved: [] as GlobalConfigSaveAttributionSettingsRequest[] };
const reply = (status = "ok", message: string | null = null): GlobalConfigAttributionSettingsResponse =>
  ({ status, ...held, defaultCoAuthorTrailer, defaultExternalWritingInstruction, message });
const api = {
  sessionTitles: async () => ({ status: "ok", mode: "model" }),
  saveSessionTitles: async () => ({ status: "ok", mode: "model" }),
  reviewSettings: async () => ({ status: "ok", beforeCommit: false }),
  saveReviewSettings: async () => ({ status: "ok", beforeCommit: false }),
  promptSuggestionSettings: async () => ({ status: "ok", enabled: true }),
  savePromptSuggestionSettings: async () => ({ status: "ok", enabled: true }),
  attributionSettings: async () => reply(),
  saveAttributionSettings: async (request: GlobalConfigSaveAttributionSettingsRequest) => {
    state.saved.push(request);
    // The host refuses a trailer without a colon, as Git reads none as a trailer.
    if (request.coAuthorTrailer && !request.coAuthorTrailer.includes(":")) return reply("invalid", "The co-author trailer is a Git trailer, such as `Co-authored-by: Name <email>`.");
    if (request.coAuthor !== null) held.coAuthor = request.coAuthor;
    if (request.coAuthorTrailer !== null) held.coAuthorTrailer = request.coAuthorTrailer || defaultCoAuthorTrailer;
    if (request.externalWriting !== null) held.externalWriting = request.externalWriting;
    if (request.externalWritingInstruction !== null) held.externalWritingInstruction = request.externalWritingInstruction || defaultExternalWritingInstruction;
    return reply();
  },
};
const filesApi = { list: async () => ({ status: "ok", platform: "windows", locations: [] }), open: async () => ({ status: "ok" }), reveal: async () => ({ status: "ok" }) };
Object.assign(window, { attributionFixture: { saved: state.saved, held } });

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <div className="ide-shell" style={{ padding: 12, overflow: "auto", height: "100%", boxSizing: "border-box" }}>
      <SessionSettings epoch="epoch" api={api} filesApi={filesApi} />
    </div>
  </ShellLanguageContext.Provider>
</StrictMode>);
