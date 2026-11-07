// Disposable page: the dialog of the release notes against scripted releases, for releaseNotes.browser.test.ts through
// `releaseNotesFixture`: a computer that runs 4.0.0 while 4.1.0 is out, as no computer of a developer does.
import { StrictMode, useState } from "react";
import { createRoot } from "react-dom/client";
import { MarkdownLinksContext } from "../MarkdownContent";
import { ShellLanguageContext } from "../shellLanguage";
import { ReleaseNotesDialog, type ReleaseNotesApi } from "./ReleaseNotesDialog";

document.documentElement.classList.add("bp6-dark");

const notes: Record<string, string> = {
  "4.1.0": "## ✨ The 4.1.0 tour\n\n**Side questions everywhere.** Ask the model of any session a question about it, from a button beside the microphone, without adding anything to its conversation.\n\n"
    + "![The Fleet tab of Settings](https://raw.githubusercontent.com/CodeAlta/CodeAlta/main/site/img/picture.svg)\n\n"
    + "## 🛰️ Fleet in Settings\n\n- A **Fleet** tab beside General, with an Overview of your machines and your server\n- Tables that sort, filter and scale to a large fleet\n- Help buttons that open the guide\n\n"
    + "## 🪟 Windows a step at a time\n\nEvery window of CodeAlta has **Smaller** and **Larger** in its title bar.\n\n```sh\ndotnet tool update -g CodeAlta\n```\n",
  "4.0.0": "## The 4.0.0 tour\n\nYour agents. One workspace. One fleet.",
  "4.2.0-preview.1": "A preview.",
};
const releases = [
  { version: "4.2.0-preview.1", name: "4.2.0 preview 1", publishedAt: "2026-11-20T10:00:00Z", prerelease: true },
  { version: "4.1.0", name: "4.1.0 · Fleet and side questions", publishedAt: "2026-11-14T10:00:00Z", prerelease: false },
  { version: "4.0.0", name: "4.0.0", publishedAt: "2026-11-06T10:00:00Z", prerelease: false },
  { version: "3.4.1", name: "3.4.1", publishedAt: "2026-11-04T10:00:00Z", prerelease: false },
];
const svg = btoa(`<svg xmlns="http://www.w3.org/2000/svg" width="880" height="300" viewBox="0 0 880 300"><defs><linearGradient id="g" x1="0" x2="1"><stop offset="0" stop-color="#7c5cff"/><stop offset="1" stop-color="#2d9cdb"/></linearGradient></defs><rect width="880" height="300" rx="16" fill="#1c2127"/><rect x="40" y="40" width="800" height="220" rx="12" fill="url(#g)" opacity=".35"/><text x="440" y="165" text-anchor="middle" font-family="Segoe UI, sans-serif" font-size="34" fill="#fff">Fleet - Overview</text></svg>`);
// The picture of the notes: a placeholder, unless the test gives a picture of the site.
const state = { installs: 0, opened: [] as string[], hide: false, picture: { mediaType: "image/svg+xml", data: svg } };
const api: ReleaseNotesApi = {
  list: (async () => ({ status: "ok", currentVersion: "4.0.0", latestVersion: "4.1.0", isLatest: false, releases, message: null })) as unknown as ReleaseNotesApi["list"],
  read: (async ({ version }: { version: string }) => {
    const release = releases.find(item => item.version === version);
    return release ? { status: "ok", release, url: `https://github.com/CodeAlta/CodeAlta/releases/tag/${version}`, body: notes[version] ?? "", message: null }
      : { status: "not_found", release: null, url: null, body: null, message: null };
  }) as unknown as ReleaseNotesApi["read"],
  picture: (async () => ({ status: "ok", ...state.picture })) as unknown as ReleaseNotesApi["picture"],
};

function Page() {
  const [hide, setHide] = useState(false);
  const [open, setOpen] = useState(true);
  Object.assign(window, { releaseNotesFixture: { state, reopen: () => setOpen(true) } });
  return open ? <ReleaseNotesDialog api={api} update={{ version: "4.1.0", canInstall: true }} onInstallUpdate={() => { state.installs++; }}
    hideOnStartup={hide} onHideOnStartup={value => { state.hide = value; setHide(value); }} onClose={() => setOpen(false)} /> : null;
}

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <MarkdownLinksContext.Provider value={address => { state.opened.push(address); }}>
      <div className="ide-shell" style={{ height: "100%" }}><Page /></div>
    </MarkdownLinksContext.Provider>
  </ShellLanguageContext.Provider>
</StrictMode>);
