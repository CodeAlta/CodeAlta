import type { plugins, pseudonymizer, settings, workspace, PseudonymizerModelsResponse } from "#neoastra";
import type { Locale, MessageKey } from "../localization";

/** The calls that read and change the security of a project. */
export type ProjectSecurityApi = Readonly<{
  setProjectSecure: typeof workspace.setProjectSecure;
  setEnabled: typeof plugins.setEnabled;
  getSetting: typeof settings.get;
  setSetting: typeof settings.set;
  models: typeof pseudonymizer.models;
  install: typeof pseudonymizer.install;
}>;

/** The id of the built-in plugin that pseudonymizes what the sessions of a secure project send. */
export const pseudonymizerPluginId = "pseudonymizer";

/** The languages of the documents a secure project chooses, in the order the window offers them. */
export const securityLanguages = Object.freeze(["french", "english", "multilingual", "identifiers"] as const);
export type SecurityLanguage = typeof securityLanguages[number];

const languageNames: Readonly<Record<string, MessageKey>> = {
  french: "French", english: "English", multilingual: "Several languages", identifiers: "Identifiers only",
};

const languageHelp: Readonly<Record<string, MessageKey>> = {
  french: "Names of people, places and organizations in French, and the identifiers of France (NIR, SIRET…).",
  english: "Names of people, places and organizations in English, and the identifiers of the UK and the US (NI, NHS, SSN…).",
  multilingual: "Ten languages, French and English among them, and the identifiers of France, the UK and the US.",
  identifiers: "Emails, phone numbers, IBAN, social security and company numbers of France, the UK and the US. Names are not looked for.",
};

/** The name of a language, as the window and Settings show it; a language this window does not know is the multilingual one. */
export const securityLanguageName = (id: string): MessageKey => languageNames[id] ?? languageNames.multilingual;

/** What a language finds, as the window and Settings say it. */
export const securityLanguageHelp = (id: string): MessageKey => languageHelp[id] ?? languageHelp.multilingual;

/** What a secure project chooses: the language of its documents, the names it always hides and the words it never hides. */
export type ProjectSecurityChoice = Readonly<{ language: string; alwaysHide: readonly string[]; neverHide: readonly string[] }>;

/** The outcome of a change: done, with whether a model is being downloaded for it; or the step that refused it. */
export type ProjectSecurityOutcome = Readonly<{ status: "ok"; downloading: boolean }> | Readonly<{ status: string }>;

const setting = (key: string) => `plugins.${pseudonymizerPluginId}.${key}`;
const timeout = { timeoutMilliseconds: 30_000 };

/** The language a project is offered first: the one of CodeAlta for French and English, several languages otherwise. */
export function defaultSecurityLanguage(locale: Locale): SecurityLanguage {
  return locale === "fr" ? "french" : locale === "en" ? "english" : "multilingual";
}

/** The lines of a text area as a list: trimmed, without empty lines or repeats. */
export function securityLines(text: string): string[] {
  return [...new Set(text.split(/\r?\n/u).map(line => line.trim()).filter(line => line.length > 0))];
}

/** Reads the choices of a secure project; null when they cannot be read (the plugin is off, the host changed). */
export async function readProjectSecurity(api: ProjectSecurityApi, epoch: string, projectId: string): Promise<ProjectSecurityChoice | null> {
  try {
    const [language, always, never] = await Promise.all(["language", "always_hide", "never_hide"].map(key =>
      api.getSetting({ expectedEpoch: epoch, id: setting(key), projectId }, timeout)));
    if ([language, always, never].some(value => value.status !== "ok")) return null;
    const list = (value: string | null) => { const parsed: unknown = value ? JSON.parse(value) : []; return Array.isArray(parsed) ? parsed.filter((item): item is string => typeof item === "string") : []; };
    const chosen: unknown = language.value ? JSON.parse(language.value) : null;
    return { language: typeof chosen === "string" ? chosen : "multilingual", alwaysHide: list(always.value), neverHide: list(never.value) };
  } catch {
    return null;
  }
}

// Saves the choices of a project; false when one of them is not saved.
async function saveChoice(api: ProjectSecurityApi, epoch: string, projectId: string, choice: ProjectSecurityChoice): Promise<boolean> {
  for (const [key, value] of [["language", choice.language], ["always_hide", choice.alwaysHide], ["never_hide", choice.neverHide]] as const) {
    const saved = await api.setSetting({ expectedEpoch: epoch, id: setting(key), projectId, value: JSON.stringify(value) }, timeout).catch(() => null);
    if (saved?.status !== "ok") return false;
  }
  return true;
}

// Downloads the model of a language when it is missing: whether a download of it runs now.
async function installModel(api: ProjectSecurityApi, epoch: string, language: string): Promise<ProjectSecurityOutcome> {
  const models = await api.install({ expectedEpoch: epoch, language }, timeout).catch(() => null);
  return { status: "ok", downloading: models?.download?.state === "downloading" && models.download.language === language };
}

/**
 * Makes a project secure with its choices. The plugin that pseudonymizes is turned on first, globally (its settings are
 * then known), the choices are saved, and only then is the project secure: a step that fails leaves it normal.
 */
export async function secureProject(api: ProjectSecurityApi, epoch: string, projectId: string, choice: ProjectSecurityChoice): Promise<ProjectSecurityOutcome> {
  const plugin = await api.setEnabled({ expectedEpoch: epoch, projectId: null, scope: "Global", id: pseudonymizerPluginId, enabled: true },
    { timeoutMilliseconds: 120_000 }).catch(() => null);
  if (plugin?.status !== "ok") return { status: "plugin_failed" };
  if (!await saveChoice(api, epoch, projectId, choice)) return { status: "settings_failed" };
  const secured = await api.setProjectSecure({ expectedHostEpoch: epoch, projectId, secure: true }, timeout).catch(() => null);
  // A host that answers ok and did not make the project secure did not do what was asked: that is no success.
  if (secured?.status !== "ok") return { status: secured?.status ?? "unavailable" };
  if (!secured.secure) return { status: "not_secured" };
  return installModel(api, epoch, choice.language);
}

/** Saves the choices of a project that is secure already, and downloads the model of a language it changed to. */
export async function saveProjectSecurity(api: ProjectSecurityApi, epoch: string, projectId: string, choice: ProjectSecurityChoice): Promise<ProjectSecurityOutcome> {
  if (!await saveChoice(api, epoch, projectId, choice)) return { status: "settings_failed" };
  return installModel(api, epoch, choice.language);
}

/** Makes a project normal again: its next sessions send what they hold; those that were confidential stay so. */
export async function makeProjectNormal(api: ProjectSecurityApi, epoch: string, projectId: string): Promise<ProjectSecurityOutcome> {
  const answer = await api.setProjectSecure({ expectedHostEpoch: epoch, projectId, secure: false }, timeout).catch(() => null);
  return answer?.status === "ok" ? { status: "ok", downloading: false } : { status: answer?.status ?? "unavailable" };
}

/** Whether the choices shown differ from the saved ones. */
export function projectSecurityChanged(saved: ProjectSecurityChoice, choice: ProjectSecurityChoice): boolean {
  return saved.language !== choice.language || saved.alwaysHide.join("\n") !== choice.alwaysHide.join("\n") || saved.neverHide.join("\n") !== choice.neverHide.join("\n");
}

/**
 * Whether the choices of a secure project can be saved: they differ from the saved ones, or their model is to download.
 * Never while the saved ones are not read (`null`): saving would replace them with what the window shows before it reads them.
 */
export function canSaveProjectSecurity(saved: ProjectSecurityChoice | null, choice: ProjectSecurityChoice, download: boolean): boolean {
  return !!saved && (download || projectSecurityChanged(saved, choice));
}

/** Whether the models answer says that a download runs. */
export const downloading = (models: PseudonymizerModelsResponse | null) => models?.download?.state === "downloading";
