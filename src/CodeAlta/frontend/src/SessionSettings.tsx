import { lazy, Suspense } from "react";
import { Button, Card, HTMLSelect, InputGroup, Switch } from "@blueprintjs/core";
import { globalConfig, type GlobalConfigSaveAttributionSettingsRequest } from "#neoastra";
import { SettingsField as Field } from "./SettingsField";
import { SettingsFileLocations } from "./SettingsFileLocation";
import { useSettingsFiles, type SettingsFilesApi } from "./settingsFiles";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "./SettingsPage";
import { sessionTitleChoice, sessionTitleModes } from "./sessionTitles";
import { useShellLanguage } from "./shellLanguage";
import { useAttributionDraft, type ConfirmTextRemoval } from "./attributionText";
import { AttributionWhy, useAttributionConfirmation } from "./AttributionWhy";

// The Markdown editor loads with the card that shows it: the rest of the page does not wait for Monaco.
const AttributionMarkdown = lazy(() => import("./AttributionMarkdown").then(module => ({ default: module.AttributionMarkdown })));

/**
 * Settings page for the sessions: how a session nobody named is titled, whether the model suggests the next prompt,
 * whether the user reviews the changes of the sessions before an agent commits them, whether agents name CodeAlta as
 * a co-author of their commits and say that what they write elsewhere comes from CodeAlta (with the texts the user
 * writes for both), and whether the sessions share a single tab. The title, the suggestion, the review and the
 * attribution choices are saved at once, in the configuration file of the user; a session takes them with its next
 * prompt (the review at once for its commits), unless it has a choice of its own. The single tab is a preference of
 * the window, which keeps it.
 */
export function SessionSettings({ epoch, api = globalConfig, onOpenFile, filesApi, tabs }: {
  epoch: string | null;
  api?: Pick<typeof globalConfig, "sessionTitles" | "saveSessionTitles" | "reviewSettings" | "saveReviewSettings" | "attributionSettings" | "saveAttributionSettings"
    | "promptSuggestionSettings" | "savePromptSuggestionSettings">;
  /** Whether the sessions share a single tab, and what changes it; absent where the window has no tabs to set. */
  tabs?: { single: boolean; setSingle: (value: boolean) => void };
  /** Called once the code editor was asked to show the configuration file: the window leaves Settings. */
  onOpenFile?: () => void;
  /** Says where the configuration file is and opens it; the host by default. */
  filesApi?: SettingsFilesApi;
}) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, setNotice, reload, mutate } = useSettingsEditor(async options => {
    // The choices are in the configuration file of the user; the suggestion, the review and the attribution ones are
    // null where the host has none.
    const [titles, suggestions, review, attribution] = await Promise.all([api.sessionTitles({ expectedEpoch: epoch }, options),
      api.promptSuggestionSettings({ expectedEpoch: epoch }, options).catch(() => null),
      api.reviewSettings({ expectedEpoch: epoch }, options).catch(() => null),
      api.attributionSettings({ expectedEpoch: epoch }, options).catch(() => null)]);
    return { ...titles, promptSuggestions: suggestions?.status === "ok" ? suggestions.enabled : null,
      beforeCommit: review?.status === "ok" ? review.beforeCommit : null, attribution: attribution?.status === "ok" ? attribution : null };
  }, `${epoch}`);
  // The choice is kept in the configuration file of the user: the page says where that file is.
  const files = useSettingsFiles({ page: "config", epoch, projectId: null, revision: listing, onOpened: onOpenFile, setNotice, api: filesApi });
  const choice = sessionTitleChoice(listing?.mode);
  // Saves one attribution choice; the others stay as the file has them.
  const saveAttribution = (change: Partial<Omit<GlobalConfigSaveAttributionSettingsRequest, "expectedEpoch">>) =>
    void mutate(() => api.saveAttributionSettings({ expectedEpoch: epoch, coAuthor: null, coAuthorTrailer: null, externalWriting: null, externalWritingInstruction: null, ...change },
      { timeoutMilliseconds: 30_000 }), "Saved.");
  const attribution = listing?.attribution ?? null;
  // Turning off a line of CodeAlta, or saving a text that leaves it out, asks once first.
  const confirmation = useAttributionConfirmation();
  const confirmText: ConfirmTextRemoval = (proceed, keep) => confirmation.confirm("text", proceed, keep);

  return <SettingsPage className="session-settings" label={t("Sessions")} group="Agent & models" title="Sessions"
    description="How CodeAlta titles your sessions, reviews their changes and shows them in tabs." notice={notice} loading={loading} busy={busy} onReload={reload}>
    {/* The tabs are the window's own: they are set also where the configuration file cannot be read. */}
    {!listing && (loading || !tabs) ? <SettingsUnavailable loading={loading} icon="chat" title="Session settings unavailable" /> : <>
      {listing && <SettingsFileLocations files={files} disabled={busy} />}
      <div className="settings-grid">{listing && <Card className="appearance-settings" aria-labelledby="settings-session-titles-title">
        <h2 id="settings-session-titles-title">{t("Session titles")}</h2>
        <Field setting="sessions.title" label={t("Title of a session you did not name")} htmlFor="settings-session-title-mode"
          notice={<p className="settings-field-help">{choice && <>{t(choice.help)} </>}{t("A title you give stays.")}</p>}>
          <HTMLSelect id="settings-session-title-mode" value={choice?.value ?? ""} disabled={busy}
            onChange={event => {
              const mode = event.target.value;
              void mutate(() => api.saveSessionTitles({ expectedEpoch: epoch, mode }, { timeoutMilliseconds: 30_000 }), "Saved.");
            }}>
            {!choice && <option value="" disabled>{listing.mode}</option>}
            {sessionTitleModes.map(mode => <option key={mode.value} value={mode.value}>{t(mode.label)}</option>)}
          </HTMLSelect>
        </Field>
      </Card>}
      {listing && listing.promptSuggestions !== null && <Card className="appearance-settings" aria-labelledby="settings-session-suggestions-title">
        <h2 id="settings-session-suggestions-title">{t("Prompt suggestions")}</h2>
        <Field setting="sessions.prompt_suggestions" label={t("Suggest the next prompt")} htmlFor="settings-prompt-suggestions"
          notice={<p className="settings-field-help">{t("After a turn, the model suggests what you may write next, in the empty prompt: Tab or → takes it.")}</p>}>
          <Switch id="settings-prompt-suggestions" className="settings-checkbox" checked={listing.promptSuggestions} disabled={busy}
            onChange={event => {
              const enabled = event.currentTarget.checked;
              void mutate(() => api.savePromptSuggestionSettings({ expectedEpoch: epoch, enabled }, { timeoutMilliseconds: 30_000 }), "Saved.");
            }} />
        </Field>
      </Card>}
      {listing && listing.beforeCommit !== null && <Card className="appearance-settings" aria-labelledby="settings-session-review-title">
        <h2 id="settings-session-review-title">{t("Reviews")}</h2>
        <Field setting="review.before_commit" label={t("Review changes before commit")} htmlFor="settings-review-before-commit"
          notice={<p className="settings-field-help">{t("An agent asks for your review of its changes and commits only what you approve. A session can turn this on or off for itself, in its composer.")}</p>}>
          <Switch id="settings-review-before-commit" className="settings-checkbox" checked={listing.beforeCommit} disabled={busy}
            onChange={event => {
              const beforeCommit = event.currentTarget.checked;
              void mutate(() => api.saveReviewSettings({ expectedEpoch: epoch, beforeCommit }, { timeoutMilliseconds: 30_000 }), "Saved.");
            }} />
        </Field>
      </Card>}
      {tabs && <Card className="appearance-settings" aria-labelledby="settings-session-tabs-title">
        <h2 id="settings-session-tabs-title">{t("Session tabs")}</h2>
        <Field setting="sessions.single_tab" label={t("Single tab for sessions")} htmlFor="settings-single-session-tab"
          notice={<p className="settings-field-help">{t("A session you open takes the place of the one shown.")}</p>}>
          <Switch id="settings-single-session-tab" className="settings-checkbox" checked={tabs.single} onChange={event => tabs.setSingle(event.currentTarget.checked)} />
        </Field>
      </Card>}
      {/* The two lines of CodeAlta come last, together, under why they matter. */}
      {attribution && <AttributionWhy />}
      {attribution && <Card className="appearance-settings" aria-labelledby="settings-session-commits-title">
        <h2 id="settings-session-commits-title">{t("Commits")}</h2>
        <Field setting="commits.co_author" label={t("Add CodeAlta as co-author")} htmlFor="settings-commit-co-author"
          notice={<p className="settings-field-help">{t("Agents end the message of each commit with a Co-authored-by line for CodeAlta, beside the lines their provider adds.")}</p>}>
          <Switch id="settings-commit-co-author" className="settings-checkbox" checked={attribution.coAuthor} disabled={busy}
            onChange={event => {
              const coAuthor = event.currentTarget.checked;
              if (coAuthor) saveAttribution({ coAuthor });
              else confirmation.confirm("co-author", () => saveAttribution({ coAuthor }));
            }} />
        </Field>
        <Field setting="commits.co_author_trailer" label={t("Co-author line")} htmlFor="settings-commit-co-author-trailer"
          notice={<p className="settings-field-help">{t("The trailer agents add, such as Co-authored-by: Name <email>. Empty for the one of CodeAlta.")}</p>}>
          <AttributionText id="settings-commit-co-author-trailer" value={attribution.coAuthorTrailer} defaultValue={attribution.defaultCoAuthorTrailer}
            maxLength={200} disabled={busy || !attribution.coAuthor} onSave={coAuthorTrailer => saveAttribution({ coAuthorTrailer })} confirm={confirmText} />
        </Field>
      </Card>}
      {attribution && <Card className="appearance-settings" aria-labelledby="settings-session-external-writing-title">
        <h2 id="settings-session-external-writing-title">{t("Comments and messages")}</h2>
        <Field setting="external_writing.attribution" label={t("Say that CodeAlta wrote it")} htmlFor="settings-external-writing-attribution"
          notice={<p className="settings-field-help">{t("When an agent writes a pull request comment, an issue, a chat message or an email for you, it ends the text with a line saying that it comes from CodeAlta.")}</p>}>
          <Switch id="settings-external-writing-attribution" className="settings-checkbox" checked={attribution.externalWriting} disabled={busy}
            onChange={event => {
              const externalWriting = event.currentTarget.checked;
              if (externalWriting) saveAttribution({ externalWriting });
              else confirmation.confirm("external-writing", () => saveAttribution({ externalWriting }));
            }} />
        </Field>
        {/* The instruction is Markdown, often long: its editor is as wide as the card, under its label and its help. */}
        <div className="settings-field attribution-instruction" data-setting="external_writing.instruction">
          <span className="settings-field-label">{t("Instruction to agents")}</span>
          <p className="settings-field-help">{t("What agents are told about that line, in Markdown. Its line breaks become spaces.")}</p>
          <Suspense fallback={<div className="settings-field-wide attribution-markdown"><div className="attribution-markdown-editor" /></div>}>
            <AttributionMarkdown label={t("Instruction to agents")} value={attribution.externalWritingInstruction} defaultValue={attribution.defaultExternalWritingInstruction}
              maxLength={1000} disabled={busy || !attribution.externalWriting} onSave={externalWritingInstruction => saveAttribution({ externalWritingInstruction })}
              confirm={confirmText} />
          </Suspense>
        </div>
      </Card>}
</div>
      {attribution && confirmation.dialog}
    </>}
  </SettingsPage>;
}


/**
 * A text of the user on one line that takes the place of the one of CodeAlta. The field starts with the text agents get,
 * the one of CodeAlta until the user writes another, so that a change is a tweak of it; the button at its end goes back
 * to the one of CodeAlta. It is saved on Enter or when the field loses the focus, if it changed; an empty field, or the
 * text of CodeAlta, saves no text of the user.
 */
export function AttributionText({ id, value, defaultValue, maxLength, disabled, onSave, confirm }: {
  id: string; value: string; defaultValue: string; maxLength: number; disabled: boolean; onSave: (text: string) => void;
  /** Asks before a text that no longer names CodeAlta is saved. */
  confirm?: ConfirmTextRemoval;
}) {
  const { t } = useShellLanguage();
  const text = useAttributionDraft(value, defaultValue, onSave, confirm);
  const restore = text.own || text.draft !== defaultValue
    ? <Button variant="minimal" icon="reset" aria-label={t("Use CodeAlta's text")} title={t("Use CodeAlta's text")} disabled={disabled}
      // The press comes before the blur of the field, which would save the draft first.
      onMouseDown={event => event.preventDefault()} onClick={text.restore} />
    : undefined;
  return <InputGroup id={id} value={text.draft} placeholder={defaultValue} maxLength={maxLength} disabled={disabled} fill rightElement={restore}
    onValueChange={text.setDraft} onBlur={text.save}
    onKeyDown={event => { if (event.key === "Enter") { event.preventDefault(); text.save(); } }} />;
}
