// The reorganized preference pages, in a frame of a chosen width.
import { useState } from "react";
import { createRoot } from "react-dom/client";
import { createAppearancePreview } from "./appearancePreview";
import type { ColorSchemeLibrary } from "./colorSchemeLibrary";
import { colorSchemeOf, defaultColorScheme } from "./colorSchemes";
import { GeneralSettings } from "./GeneralSettings";
import { AppearanceSettings } from "./AppearanceSettings";
import { PermissionSettings } from "./PermissionSettings";
import type { Locale } from "./localization";
import { ShellLanguageContext } from "./shellLanguage";
import type { ExternalEditorsResponse } from "#neoastra";

const library: ColorSchemeLibrary = { status: "ok", directory: null, problems: [], reload: async () => { }, reveal: async () => ({ status: "unavailable", message: null }),
  save: async () => ({ status: "unavailable", id: null, message: null }), remove: async () => ({ status: "unavailable", message: null }) };

// The editors of a machine that has Visual Studio Code and Cursor.
const found: ExternalEditorsResponse = { status: "ok", platform: "windows", chosen: null, customProgram: null, customArguments: null,
  editors: [{ id: "vscode", name: "Visual Studio Code", program: "C:\\Program Files\\Microsoft VS Code\\Code.exe" }, { id: "cursor", name: "Cursor", program: "C:\\Cursor\\Cursor.exe" }],
  current: { id: "vscode", name: "Visual Studio Code", program: "C:\\Program Files\\Microsoft VS Code\\Code.exe" } };
const editors = { list: async () => found, set: async () => found };

function Fixture() {
  const [locale, setLocale] = useState<Locale>("en");
  const [width, setWidth] = useState(900);
  const [page, setPage] = useState("appearance");
  const [preview] = useState(createAppearancePreview);
  Object.assign(window, { appearanceLayout: { setLocale, setWidth, setPage } });
  return <ShellLanguageContext.Provider value={{ locale, choice: locale, setLanguage: value => setLocale(value as Locale) }}>
    <style>{`.settings-card-page.configuration-page { padding: 0; overflow: visible; }`}</style>
    <div className="settings-card-page" style={{ width }}><div className="settings-grid">
      {page === "general" ? <GeneralSettings closing={{ behavior: "ask", platform: "windows", set: () => {} }} remoteControl={{ reconnect: true, set: () => {} }}
        externalEditor={{ api: editors }} />
        : page === "permissions" ? <PermissionSettings permissions={null} confirms={{ sessionDelete: true, projectArchive: true, pluginStop: true, set: () => {} }} />
        : <AppearanceSettings theme="dark" setTheme={() => { }} darker={false} setDarker={() => { }} sort="name" setSort={() => { }}
        desktopCollapsed={false} setDesktopCollapsed={() => { }} recentSessionCount={6} setRecentSessionCount={() => { }} subAgentCount={4} setSubAgentCount={() => { }} notices={{}}
        schemes={{ colorScheme: defaultColorScheme, setColorScheme: () => { }, shownScheme: colorSchemeOf(defaultColorScheme), variant: "dark", customSchemes: [],
          library, preview, platform: "windows" }} />}
    </div></div>
  </ShellLanguageContext.Provider>;
}
document.documentElement.classList.add("bp6-dark");
createRoot(document.getElementById("root")!).render(<Fixture />);
