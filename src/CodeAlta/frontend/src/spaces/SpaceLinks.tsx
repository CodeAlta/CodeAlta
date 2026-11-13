import { useEffect, useRef, useState } from "react";
import { Button, HTMLSelect, InputGroup } from "@blueprintjs/core";
import { spaceLinks, type SpaceLinkBrowserItem, type SpaceLinksResponse } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { SettingsField as Field } from "../SettingsField";
import { useShellLanguage } from "../shellLanguage";
import type { SpaceSectionProps } from "./spaceSection";
import { linksChoice, profileForBrowser, type LinksSetting } from "./spaceLinksChoice";

export type SpaceLinksApi = Pick<typeof spaceLinks, "get" | "update">;

/**
 * Where the links followed in the sessions and projects of a space open, in its section "General": the default browser of
 * the system unless one is chosen, and a profile of it, for the browsers that let it be chosen (Chromium browsers and
 * Firefox). Each choice is saved as soon as it is made.
 */
export function SpaceLinks({ id, space, epoch, canEdit, run, api = spaceLinks }: Readonly<{
  id: string; space: SpaceSectionProps["space"]; epoch: string; canEdit: boolean; run: SpaceSectionProps["run"]; api?: SpaceLinksApi;
}>) {
  const { t } = useShellLanguage();
  const [setting, setSetting] = useState<LinksSetting>({ browser: "", profile: "" });
  const [browsers, setBrowsers] = useState<readonly SpaceLinkBrowserItem[]>([]);
  const [available, setAvailable] = useState(false);
  const [typed, setTyped] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const settingRef = useRef(setting);
  settingRef.current = setting;

  useEffect(() => {
    let current = true;
    void api.get({ expectedEpoch: epoch, spaceId: space.id }, { timeoutMilliseconds: 30_000 }).then((reply: SpaceLinksResponse) => {
      if (!current) return;
      setAvailable(reply.status === "ok");
      if (reply.status !== "ok") return;
      setSetting({ browser: reply.links?.browser ?? "", profile: reply.links?.profile ?? "" });
      setBrowsers(reply.browsers);
    }, () => { if (current) setAvailable(false); });
    return () => { current = false; };
  }, [api, epoch, space.id]);

  if (!available) return null;
  const choice = linksChoice(setting, browsers);

  async function copy(text: string) {
    try { await navigator.clipboard.writeText(text); setCopied(true); setTimeout(() => setCopied(false), 1500); } catch { /* Nothing was copied: the address stays to select. */ }
  }

  async function save(next: LinksSetting) {
    const before = settingRef.current;
    setSetting(next);
    await run(async () => {
      const reply = await api.update({ expectedEpoch: epoch, spaceId: space.id, browser: next.browser || null, profile: next.profile || null }, { timeoutMilliseconds: 30_000 });
      if (reply.status === "ok") setSetting({ browser: reply.links?.browser ?? "", profile: reply.links?.profile ?? "" });
      else setSetting(before);
      return { ok: reply.status === "ok", status: reply.status, message: reply.message, space: null };
    });
  }

  const defaultLabel = choice.defaultBrowser ? t("Default browser ({name})", { name: choice.defaultBrowser.name }) : t("Default browser");
  const browserOptions = [
    { value: "", label: defaultLabel },
    ...browsers.map(browser => ({ value: browser.id, label: browser.name })),
    // A browser the file names that this computer does not have stays shown, so that the choice is not lost unseen.
    ...(choice.browserMissing ? [{ value: setting.browser, label: t("{name} (not installed)", { name: setting.browser }) }] : []),
  ];
  const profileOptions = [
    { value: "", label: t("No profile (as the browser opens)") },
    ...(choice.target?.profiles ?? []).map(profile => ({ value: profile, label: profile })),
    ...(choice.profileMissing ? [{ value: setting.profile, label: t("{name} (not found)", { name: setting.profile }) }] : []),
  ];
  const firefox = !!choice.target?.id.startsWith("firefox");
  const notice = choice.browserMissing
    ? <p className="settings-field-help space-links-problem" role="alert">{t("{name} is not installed on this computer: links open in the default browser.", { name: setting.browser })}</p>
    : choice.profileNotFolder
      ? <p className="settings-field-help space-links-problem" role="alert">{t("The folders of the profiles always follow the scheme Default, Profile 1, Profile 2…: {name} is none of them, so links open without a profile.", { name: setting.profile })}</p>
    : choice.profilesUnreadable && firefox
      ? <p className="settings-field-help">{t("CodeAlta cannot read the profiles of {name}: type the name of the profile.", { name: choice.target!.name })}</p>
    : choice.profilesUnreadable
      ? <p className="settings-field-help">{t("CodeAlta cannot read the profiles of {name}. To find the folder of a profile:", { name: choice.target?.name ?? "" })}</p>
    : choice.target && !choice.target.supportsProfiles
      ? <p className="settings-field-help">{t("{name} does not let a profile be chosen: links open in the window used last.", { name: choice.target.name })}</p>
    : choice.profileMissing
      ? <p className="settings-field-help space-links-problem" role="alert">{t("{browser} has no profile named {name}: links open without a profile.", { browser: choice.target?.name ?? "", name: setting.profile })}</p>
      : <p className="settings-field-help">{t("For the links you follow in the sessions and the projects of this space. A profile can be chosen in Chrome, Edge and the other Chromium browsers, and in Firefox.")}</p>;
  const guide = choice.profilesUnreadable && !firefox && <ol className="space-links-guide">
    <li>{t("In {name}, open the profile the links should open in.", { name: choice.target?.name ?? "" })}</li>
    <li>
      <span>{t("Type this address in its address bar:")}</span>
      <span className="space-links-address">
        <code>chrome://version</code>
        <Button size="small" variant="minimal" icon={<AppIcon name={copied ? "check" : "copy"} size={13} />} aria-label={t("Copy command")}
          title={t(copied ? "Copied" : "Copy command")} onClick={() => void copy("chrome://version")} />
      </span>
    </li>
    <li>{t("Under Profile Path, the last part is the folder, such as Profile 1: type it in the field above.")}</li>
  </ol>;

  return <div className="space-links" data-part="links">
    <Field label={t("Links open in")} htmlFor={`${id}-links-browser`}>
      <HTMLSelect id={`${id}-links-browser`} value={setting.browser} disabled={!canEdit}
        onChange={event => { const browser = event.currentTarget.value; void save({ browser, profile: profileForBrowser(settingRef.current.profile, browser, browsers) }); }}
        options={browserOptions} />
    </Field>
    <Field label={t("Browser profile")} htmlFor={`${id}-links-profile`} notice={<>{notice}{guide}</>}>
      {choice.profilesUnreadable
        // The system keeps the list of the profiles out of reach: the profile is typed.
        ? <InputGroup id={`${id}-links-profile`} value={typed ?? setting.profile} disabled={!canEdit} spellCheck={false} autoComplete="off"
          placeholder={t("Empty: without a profile")} onChange={event => setTyped(event.target.value)}
          onBlur={() => { if (typed !== null && typed.trim() !== setting.profile) void save({ browser: settingRef.current.browser, profile: typed.trim() }); setTyped(null); }}
          onKeyDown={event => { if (event.key === "Enter") event.currentTarget.blur(); else if (event.key === "Escape") { event.stopPropagation(); setTyped(null); } }} />
        : <HTMLSelect id={`${id}-links-profile`} value={choice.profileSelectable ? choice.profile : ""} disabled={!canEdit || !choice.profileSelectable}
          onChange={event => void save({ browser: settingRef.current.browser, profile: event.currentTarget.value })} options={profileOptions} />}
    </Field>
  </div>;
}
