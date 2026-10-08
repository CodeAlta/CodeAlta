import { useEffect, useRef, useState } from "react";
import { Button, Callout, Card, CardList, FormGroup, HTMLSelect, InputGroup, Section, SectionCard, Tag, TextArea } from "@blueprintjs/core";
import type { pullRequestPrompts, PullRequestPromptItem } from "#neoastra";
import { AppIcon } from "../AppIcon";
import type { MessageKey } from "../localization";
import { SettingsFileLocation, SettingsFileLocations } from "../SettingsFileLocation";
import { useSettingsFiles, type SettingsFilesApi } from "../settingsFiles";
import { RemoveButton, SettingsPage, SettingsUnavailable } from "../SettingsPage";
import type { SettingsNotice } from "../settingsEditing";
import { useShellLanguage } from "../shellLanguage";

/** The part of the host the page uses; a test gives its own. */
export type PullRequestSettingsApi = Pick<typeof pullRequestPrompts, "list" | "save" | "delete">;

type Form = Readonly<{ original: PullRequestPromptItem | null; id: string; scope: "global" | "project"; name: string; description: string; content: string }>;
const keyOf = (item: Pick<PullRequestPromptItem, "source" | "id">) => `${item.source}\n${item.id}`;
const sourceLabel = (source: string): MessageKey => source === "project" ? "Project" : source === "global" ? "Global" : "Built-in";

/** The form of an item as it is read, or of a new kind that starts from the text of another. */
export function pullRequestForm(item: PullRequestPromptItem | null, from: PullRequestPromptItem | null, scope: "global" | "project"): Form {
  return item && item.source !== "builtin"
    ? { original: item, id: item.id, scope: item.source === "project" ? "project" : "global", name: item.name, description: item.description ?? "", content: item.content }
    : { original: null, id: from ? from.id : "", scope, name: from ? from.name : "", description: from?.description ?? "", content: from?.content ?? "" };
}

/**
 * Settings page for the instructions a session is sent when it is asked to create a pull request. The kind that
 * ships with CodeAlta is shown as it is; the user's kinds and those of the selected project are written here. A
 * kind with the name of another one nearer to the project takes its place: a `default` of your own replaces the
 * built-in one.
 */
export function PullRequestSettings({ api, epoch, project, onOpenFile, filesApi }: {
  api: PullRequestSettingsApi; epoch: string | null | undefined;
  /** Called once the code editor was asked to show a file of instructions: the window leaves Settings. */
  onOpenFile?: () => void;
  /** Says where the files of instructions are and opens them; the host by default. */
  filesApi?: SettingsFilesApi;
  /** The project selected in the window: its kinds are listed and can be written. */
  project: Readonly<{ id: string; name: string }> | null;
}) {
  const { t } = useShellLanguage();
  const [items, setItems] = useState<readonly PullRequestPromptItem[] | null>(null);
  const [available, setAvailable] = useState(true);
  const [selected, setSelected] = useState<string | null>(null);
  const [form, setForm] = useState<Form | null>(null);
  const [busy, setBusy] = useState(false);
  const [notice, setNotice] = useState<SettingsNotice | null>(null);
  const [generation, setGeneration] = useState(0);
  const selectAfter = useRef<string | null>(null);
  const projectId = project?.id ?? null;
  const files = useSettingsFiles({ page: "pullRequests", epoch, projectId, revision: items, onOpened: onOpenFile, setNotice, api: filesApi });
  useEffect(() => {
    if (!epoch) { setAvailable(false); setItems([]); return; }
    const controller = new AbortController();
    void api.list({ expectedEpoch: epoch, projectId, all: true }, { signal: controller.signal, timeoutMilliseconds: 15_000 }).then(reply => {
      if (controller.signal.aborted) return;
      setAvailable(reply.status !== "unavailable");
      setItems(reply.items);
      const wanted = selectAfter.current; selectAfter.current = null;
      const next = reply.items.find(item => keyOf(item) === (wanted ?? selected)) ?? reply.items[0] ?? null;
      setSelected(next ? keyOf(next) : null);
      setForm(next ? pullRequestForm(next, null, "global") : null);
    }, () => { if (!controller.signal.aborted) { setItems([]); setNotice({ key: "The operation did not complete.", intent: "danger" }); } });
    return () => controller.abort();
  }, [api, epoch, projectId, generation]);

  const current = items?.find(item => keyOf(item) === selected) ?? null;
  function choose(item: PullRequestPromptItem) { setSelected(keyOf(item)); setForm(pullRequestForm(item, null, "global")); setNotice(null); }
  // A new kind starts from the one that is shown: most kinds are the default with a few lines changed.
  function startNew(from: PullRequestPromptItem | null, scope: "global" | "project", sameName: boolean) {
    setSelected(null); setNotice(null);
    setForm({ ...pullRequestForm(null, from, scope), id: sameName && from ? from.id : "", name: sameName && from ? from.name : "" });
  }
  async function save() {
    if (!form || !epoch || busy) return;
    setBusy(true); setNotice(null);
    try {
      const reply = await api.save({ expectedEpoch: epoch, projectId: form.scope === "project" ? projectId : null, id: form.id.trim(), name: form.name.trim() || null,
        description: form.description.trim() || null, content: form.content }, { timeoutMilliseconds: 15_000 });
      if (reply.status === "ok" && reply.item) { selectAfter.current = keyOf(reply.item); setGeneration(value => value + 1); setNotice({ key: "Saved.", intent: "success" }); }
      else setNotice({ key: "The operation did not complete.", intent: "danger", detail: reply.message ?? undefined });
    } catch { setNotice({ key: "The operation did not complete.", intent: "danger" }); }
    finally { setBusy(false); }
  }
  async function remove() {
    if (!form?.original || !epoch || busy) return;
    setBusy(true); setNotice(null);
    try {
      const reply = await api.delete({ expectedEpoch: epoch, projectId: form.original.source === "project" ? projectId : null, id: form.original.id }, { timeoutMilliseconds: 15_000 });
      if (reply.status === "ok") { setGeneration(value => value + 1); setNotice({ key: "Removed.", intent: "success" }); }
      else setNotice({ key: "The operation did not complete.", intent: "danger" });
    } catch { setNotice({ key: "The operation did not complete.", intent: "danger" }); }
    finally { setBusy(false); }
  }
  const builtIn = !!current && current.source === "builtin" && !!form && form.original === null && selected !== null;
  const valid = !!form && /^[A-Za-z0-9_-]{1,64}$/.test(form.id.trim()) && form.content.trim().length > 0 && (form.scope === "global" || !!projectId);
  const edit = (change: Partial<Form>) => setForm(value => value ? { ...value, ...change } : value);

  return <SettingsPage className="pull-request-settings" label={t("Pull requests")} group="Agent & models" title="Pull requests"
    description="What a session is told when you ask it to create a pull request." notice={notice} loading={items === null} busy={busy}
    onReload={() => setGeneration(value => value + 1)}
    actions={available ? <Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={busy || items === null} onClick={() => startNew(null, "global", false)}>{t("New kind")}</Button> : undefined}>
    {!available ? <SettingsUnavailable loading={items === null} icon="pullRequest" title="Pull request instructions are unavailable in this window." />
      : <><SettingsFileLocations files={files} disabled={busy} /><div className="provider-settings-layout pull-request-layout">
        <CardList compact className="provider-settings-list" aria-label={t("Kinds of pull request")}>
          {(items ?? []).map(item => <Card key={keyOf(item)} interactive selected={selected === keyOf(item)} aria-current={selected === keyOf(item) ? "true" : undefined} onClick={() => choose(item)}>
            <span className="provider-settings-name"><strong>{item.name}</strong><small>{item.id}.pr.md</small></span>
            <span className="provider-settings-tags">{item.overridden && <Tag minimal round intent="warning">{t("Overridden")}</Tag>}
              <Tag minimal round intent={item.source === "builtin" ? "none" : "primary"}>{t(sourceLabel(item.source))}</Tag></span>
          </Card>)}
          {form && selected === null && <Card interactive selected><span className="provider-settings-name"><strong>{form.name || t("New kind")}</strong>
            <small>{form.id || "…"}.pr.md</small></span><Tag minimal round intent="warning">{t("Unsaved changes")}</Tag></Card>}
        </CardList>
        {form && <Section className="provider-settings-form" title={builtIn ? current!.name : form.original?.name ?? t("New kind")}
          subtitle={builtIn ? t("Ships with CodeAlta") : form.original ? undefined : t("Not saved yet")}>
          {form.original?.file && <SectionCard className="settings-editor-file">
            <SettingsFileLocation path={form.original.file} platform={files.platform} disabled={busy}
              onOpen={files.open ? () => files.open!({ kind: "pullRequest", scope: form.original!.source, id: form.original!.id }) : undefined}
              onReveal={() => files.reveal({ kind: "pullRequest", scope: form.original!.source, id: form.original!.id })} /></SectionCard>}
          <SectionCard className="pull-request-fields">
            {builtIn
              ? <Callout compact icon={null}>
                  {t("These are the instructions CodeAlta ships. To change them, make a copy of your own: it takes their place.")}
                  <div className="pull-request-copy">
                    <Button size="small" disabled={busy} onClick={() => startNew(current, "global", true)}>{t("Customize for me")}</Button>
                    {project && <Button size="small" disabled={busy} onClick={() => startNew(current, "project", true)}>{t("Customize for {project}", { project: project.name })}</Button>}
                  </div>
                </Callout>
              : <div className="pull-request-identity">
                  <FormGroup label={t("File name")} labelFor="pull-request-id" helperText={form.original ? undefined : t("Letters, digits, - or _. The name default replaces the built-in instructions.")}>
                    <InputGroup id="pull-request-id" value={form.id} disabled={busy || !!form.original} maxLength={64} spellCheck={false} placeholder="release-notes"
                      rightElement={<Tag minimal>.pr.md</Tag>} onChange={event => edit({ id: event.target.value })} /></FormGroup>
                  <FormGroup label={t("Kept with")} labelFor="pull-request-scope">
                    <HTMLSelect id="pull-request-scope" value={form.scope} disabled={busy || !!form.original} onChange={event => edit({ scope: event.target.value as Form["scope"] })}>
                      <option value="global">{t("Me, for every project")}</option>
                      {project && <option value="project">{t("The project {project}", { project: project.name })}</option>}
                    </HTMLSelect></FormGroup>
                  <FormGroup label={t("Name")} labelFor="pull-request-name">
                    <InputGroup id="pull-request-name" value={form.name} disabled={busy} maxLength={200} onChange={event => edit({ name: event.target.value })} /></FormGroup>
                  <FormGroup label={t("Description")} labelFor="pull-request-description">
                    <InputGroup id="pull-request-description" value={form.description} disabled={busy} maxLength={200} onChange={event => edit({ description: event.target.value })} /></FormGroup>
                </div>}
            <FormGroup label={t("Instructions")} labelFor="pull-request-content" className="pull-request-content">
              <TextArea id="pull-request-content" value={builtIn ? current!.content : form.content} readOnly={builtIn} disabled={busy} fill spellCheck={false}
                onChange={event => edit({ content: event.target.value })} /></FormGroup>
            {!builtIn && <div className="pull-request-buttons">
              <Button intent="primary" disabled={busy || !valid} onClick={() => void save()}>{t("Save")}</Button>
              {form.original && <RemoveButton text name={form.original.name} disabled={busy} onRemove={() => void remove()} />}
            </div>}
          </SectionCard>
        </Section>}
      </div></>}
  </SettingsPage>;
}
