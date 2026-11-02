import { useEffect, useRef, useState, type ReactNode } from "react";
import { Button, Callout, Card, FormGroup, Section, SectionCard, Tag } from "@blueprintjs/core";
import type { startPrompts, StartPromptItem } from "#neoastra";
import { AppIcon } from "../AppIcon";
import type { MessageKey } from "../localization";
import { SettingsFileLocation, type SettingsFiles } from "../SettingsFileLocation";
import { RemoveButton, ScopeChoice, type SettingsProject } from "../SettingsPage";
import type { SettingsNotice, SettingsScope } from "../settingsEditing";
import { useShellLanguage } from "../shellLanguage";

/** The part of the host the start prompts use; a test gives its own. */
export type StartPromptsApi = Pick<typeof startPrompts, "list" | "save" | "delete">;

type Form = Readonly<{ original: StartPromptItem | null; id: string; scope: SettingsScope; content: string }>;

/** The editor of the text, which the page gives: the code editor of the window, which a test has not. */
export type StartPromptEditor = (text: Readonly<{ value: string; readOnly: boolean; onChange: (value: string) => void }>) => ReactNode;

// The keys of the start prompts in the selection of the page of prompts, beside the keys of the agent prompts.
const prefix = "start\u0000";
/** The key of a copy that is not saved yet. */
export const unsavedStartPromptKey = `${prefix}new`;
const unsaved = unsavedStartPromptKey;
export const startPromptKey = (item: Pick<StartPromptItem, "source" | "id">) => `${prefix}${item.source}\u0000${item.id}`;
export const isStartPromptKey = (key: string | null): boolean => !!key && key.startsWith(prefix);

/** What the page calls a start prompt, by its id; one CodeAlta does not know is named by its id. */
const names: Readonly<Record<string, Readonly<{ name: MessageKey; description: MessageKey; help: MessageKey }>>> = {
  "pull-request-review": {
    name: "Pull request review",
    description: "What a session is told first when you start the review of a pull request from the Issues tab.",
    help: "{reference}, {location} and {service} are replaced by the number of the pull request, its repository and its service. Its title, its link and its description follow this text.",
  },
};
const sourceLabel = (source: string): MessageKey => source === "project" ? "Project" : source === "global" ? "Global" : "Built-in";

/**
 * What the page shows once the texts were listed again: the text it showed, read again as it is now (a save, a
 * removal, a reload or another project changed it), or the one a save or a removal asks for. Undefined leaves the
 * page as it is: the page shows something else (the user moved on while the list was read), or a copy that is not
 * saved yet and still belongs to what is listed.
 */
export function reconcileStartPrompts(listed: readonly StartPromptItem[], state: Readonly<{
  /** What the page shows now. */
  selected: string | null;
  /** The text a save or a removal asks to show; "" for the one in use after a removal; null after a reload. */
  wanted: string | null;
  /** The start prompt the page showed, whose text in use is shown when the one it showed is gone. */
  id: string | null;
  /** The texts are of another project than the one the page showed: a copy that is not saved yet is left. */
  projectChanged: boolean;
}>): { key: string | null; form: Form | null } | undefined {
  if (!isStartPromptKey(state.selected)) return undefined;
  const target = state.wanted ?? state.selected;
  if (target === unsaved && !state.projectChanged) return undefined;
  const next = listed.find(item => startPromptKey(item) === target) ?? listed.find(item => !item.overridden && item.id === state.id) ?? null;
  return { key: next ? startPromptKey(next) : null, form: next ? startPromptForm(next) : null };
}

/** The form of a text as it is read, or of a copy of the user's that starts from the text of another. */
export function startPromptForm(item: StartPromptItem, copy: SettingsScope | null = null): Form {
  return copy || item.source === "builtin"
    ? { original: null, id: item.id, scope: copy ?? "Global", content: item.content }
    : { original: item, id: item.id, scope: item.source === "project" ? "Project" : "Global", content: item.content };
}

/** The text a scope already keeps for a start prompt: a copy for that scope would replace it. */
export function startPromptOfScope(items: readonly StartPromptItem[], id: string, scope: SettingsScope): StartPromptItem | null {
  const source = scope === "Project" ? "project" : "global";
  return items.find(item => item.id === id && item.source === source) ?? null;
}

/**
 * The start prompts on the page of agent prompts: their rows in the list of the page, and the form of the one that
 * is selected. The page owns the selection; a key of a start prompt is one `isStartPromptKey` recognizes. What ships
 * with CodeAlta is shown as it is, and a copy of the user's or of the project takes its place.
 */
export function useStartPrompts({ api, epoch, project, selected, select, setNotice, files, editor, disabled, onWritten }: {
  api: StartPromptsApi; epoch: string | null; project: SettingsProject;
  editor: StartPromptEditor;
  selected: string | null; select: (key: string | null) => void;
  setNotice: (notice: SettingsNotice | null) => void;
  /** Opens and shows the file of a text of the user or of a project. */
  files: Pick<SettingsFiles, "open" | "reveal" | "platform">;
  /** The page is busy with something else. */
  disabled: boolean;
  /** Called once a text was saved or removed. */
  onWritten?: () => void;
}): { rows: ReactNode; detail: ReactNode; busy: boolean; reload: () => void } {
  const [items, setItems] = useState<readonly StartPromptItem[]>([]);
  const [form, setForm] = useState<Form | null>(null);
  const [busy, setBusy] = useState(false);
  const [generation, setGeneration] = useState(0);
  const selectAfter = useRef<string | null>(null);
  const projectId = project?.id ?? null;
  const shown = isStartPromptKey(selected);
  // What the page shows when a list comes back, which may be later than the render that asked for it.
  const latest = useRef({ selected, id: form?.id ?? null });
  latest.current = { selected, id: form?.id ?? latest.current.id };
  const listedFor = useRef<string | null>(null);

  useEffect(() => {
    const scope = `${epoch}\u0000${projectId}`;
    const projectChanged = listedFor.current !== null && listedFor.current !== scope;
    // A text or a copy of another project is not shown, nor saved or removed, while the texts of this one are read.
    if (projectChanged && isStartPromptKey(latest.current.selected)) setForm(null);
    if (!epoch) { setItems([]); return; }
    const controller = new AbortController();
    void api.list({ expectedEpoch: epoch, projectId }, { signal: controller.signal, timeoutMilliseconds: 15_000 }).then(reply => {
      if (controller.signal.aborted) return;
      const listed = reply.status === "ok" ? reply.items : [];
      setItems(listed);
      listedFor.current = scope;
      const wanted = selectAfter.current; selectAfter.current = null;
      const next = reconcileStartPrompts(listed, { selected: latest.current.selected, id: latest.current.id, wanted, projectChanged });
      if (!next) return;
      setForm(next.form);
      select(next.key);
    }, () => { if (!controller.signal.aborted) setItems([]); });
    return () => controller.abort();
  }, [api, epoch, projectId, generation]);

  const lock = busy || disabled;
  function choose(item: StartPromptItem) { if (lock) return; setNotice(null); setForm(startPromptForm(item)); select(startPromptKey(item)); }
  // A scope that already keeps a text shows it: a copy saved there would replace it without a word.
  function copy(item: StartPromptItem, scope: SettingsScope) {
    if (lock) return;
    const kept = startPromptOfScope(items, item.id, scope);
    if (kept) { choose(kept); return; }
    setNotice(null); setForm(startPromptForm(item, scope)); select(unsaved);
  }
  async function run(action: () => Promise<{ status: string; message?: string | null }>, done: MessageKey, after: string) {
    if (!epoch || busy) return;
    setBusy(true); setNotice(null);
    try {
      const reply = await action();
      if (reply.status === "ok") { selectAfter.current = after; setGeneration(value => value + 1); setNotice({ key: done, intent: "success" }); onWritten?.(); }
      else setNotice({ key: "The operation did not complete.", intent: "danger", detail: reply.message ?? undefined });
    } catch { setNotice({ key: "The operation did not complete.", intent: "danger" }); }
    finally { setBusy(false); }
  }
  const save = (value: Form) => run(() => api.save({ expectedEpoch: epoch!, projectId: value.scope === "Project" ? projectId : null, id: value.id, content: value.content },
    { timeoutMilliseconds: 15_000 }), "Saved.", startPromptKey({ source: value.scope === "Project" ? "project" : "global", id: value.id }));
  const remove = (item: StartPromptItem) => run(() => api.delete({ expectedEpoch: epoch!, projectId: item.source === "project" ? projectId : null, id: item.id },
    { timeoutMilliseconds: 15_000 }), "Removed.", "");

  const current = shown && selected !== unsaved ? items.find(item => startPromptKey(item) === selected) ?? null : null;
  const rows = <StartPromptRows items={items} selected={selected} unsaved={selected === unsaved ? form : null} onChoose={choose} />;
  const detail = !shown || !form || (!current && selected !== unsaved) ? null
    : <StartPromptDetail form={form} builtIn={current?.source === "builtin" ? current : null} project={project} files={files} editor={editor} disabled={lock}
      taken={!form.original && !!startPromptOfScope(items, form.id, form.scope)}
      onCopy={copy} onChange={change => setForm(value => value ? { ...value, ...change } : value)} onSave={() => void save(form)}
      onCancel={() => { const back = items.find(item => item.id === form.id && item.source === "builtin"); if (back) choose(back); }}
      onRemove={() => { if (form.original) void remove(form.original); }} />;

  return { rows, detail, busy, reload: () => setGeneration(value => value + 1) };
}

const label = (t: ReturnType<typeof useShellLanguage>["t"], id: string) => names[id] ? t(names[id].name) : id;

/** The rows of the start prompts in the list of the page: one for each text, then a copy that is not saved yet. */
export function StartPromptRows({ items, selected, unsaved: copy, onChoose }: {
  items: readonly StartPromptItem[]; selected: string | null;
  /** The copy that is being written, before it is saved; null when there is none. */
  unsaved: Form | null;
  onChoose: (item: StartPromptItem) => void;
}) {
  const { t } = useShellLanguage();
  if (items.length === 0) return null;
  return <>
    <h3 className="settings-editor-group">{t("Start prompts")}</h3>
    {items.map(item => { const key = startPromptKey(item);
      return <Card key={key} interactive selected={selected === key} aria-current={selected === key ? "true" : undefined} onClick={() => onChoose(item)}>
        <span className="settings-editor-name"><strong>{label(t, item.id)}</strong><small>{item.source === "builtin" ? item.id : `${item.id}.md`}</small></span>
        <span className="provider-settings-tags">{item.overridden && <Tag minimal round intent="warning">{t("Overridden")}</Tag>}
          <Tag minimal round intent={item.source === "builtin" ? "none" : "primary"}>{t(sourceLabel(item.source))}</Tag></span>
      </Card>; })}
    {copy && <Card interactive selected><span className="settings-editor-name"><strong>{label(t, copy.id)}</strong><small>{copy.id}.md</small></span>
      <Tag minimal round intent="warning">{t("Unsaved changes")}</Tag></Card>}
  </>;
}

/**
 * The form of a start prompt. The text that ships is only read, with the buttons that make a copy of it; a copy says
 * where it is kept until it is saved, and a saved text is changed, reverted or removed.
 */
export function StartPromptDetail({ form, builtIn, project, files, editor, disabled, taken = false, onCopy, onChange, onSave, onCancel, onRemove }: {
  form: Form;
  /** The text that ships, when it is the one shown; null for a copy or a text of the user or of a project. */
  builtIn: StartPromptItem | null;
  project: SettingsProject; files: Pick<SettingsFiles, "open" | "reveal" | "platform">; editor: StartPromptEditor; disabled: boolean;
  /** A copy not saved yet whose scope already keeps a text: saving it would replace that text, so it is not saved. */
  taken?: boolean;
  onCopy: (item: StartPromptItem, scope: SettingsScope) => void;
  onChange: (change: Partial<Pick<Form, "scope" | "content">>) => void;
  onSave: () => void;
  /** Leaves a copy that is not saved yet. */
  onCancel: () => void;
  onRemove: () => void;
}) {
  const { t } = useShellLanguage();
  const known = names[form.id];
  const dirty = form.original ? form.content !== form.original.content : form.content.trim().length > 0;
  const replaces = !form.original && taken;
  const valid = form.content.trim().length > 0 && (form.scope === "Global" || !!project) && !replaces;
  const original = form.original;
  return <Section className="settings-editor-form prompt-settings-form settings-split-detail" title={label(t, form.id)}
    subtitle={builtIn ? t("Ships with CodeAlta") : original ? t(sourceLabel(original.source)) : t("Not saved yet")}
    rightElement={builtIn ? <Tag minimal round>{t("Built-in")}</Tag> : undefined}>
    {original?.file && <SectionCard className="settings-editor-file">
      <SettingsFileLocation path={original.file} platform={files.platform} disabled={disabled}
        onOpen={files.open ? () => files.open!({ kind: "startPrompt", scope: original.source, id: original.id }) : undefined}
        onReveal={() => files.reveal({ kind: "startPrompt", scope: original.source, id: original.id })} /></SectionCard>}
    <SectionCard className="settings-editor-fields">
      {known && <p className="settings-editor-wide">{t(known.description)}</p>}
      {builtIn
        ? <Callout compact icon={null} className="settings-editor-wide">{t("This is the text CodeAlta ships. To change it, make a copy of your own: it takes its place.")}</Callout>
        : !original && project && <FormGroup label={t("Stored in")}><ScopeChoice value={form.scope} project={project} disabled={disabled} onChange={scope => onChange({ scope })} /></FormGroup>}
      {!builtIn && replaces && <Callout compact intent="warning" icon={null} className="settings-editor-wide">{t("A prompt with this name already exists in that scope.")}</Callout>}
      {known && <p className="bp6-form-helper-text settings-editor-wide">{t(known.help)}</p>}
    </SectionCard>
    <SectionCard className="prompt-settings-editor">{editor({ value: form.content, readOnly: disabled || !!builtIn, onChange: content => onChange({ content }) })}</SectionCard>
    {/* The footer stays in view when the fields above the text scroll: the copy of what ships is made from it too. */}
    {builtIn ? <SectionCard className="settings-editor-footer settings-split-footer">
      <Button intent="primary" icon={<AppIcon name="copy" size={15} />} disabled={disabled} onClick={() => onCopy(builtIn, "Global")}>{t("Customize for me")}</Button>
      {project && <Button icon={<AppIcon name="copy" size={15} />} disabled={disabled} onClick={() => onCopy(builtIn, "Project")}>{t("Customize for {project}", { project: project.name })}</Button>}
    </SectionCard> : <SectionCard className="settings-editor-footer settings-split-footer">
      <Button intent="primary" disabled={!dirty || !valid || disabled} onClick={onSave}>{t("Save")}</Button>
      <Button disabled={!!original && !dirty || disabled} onClick={() => { if (original) onChange({ content: original.content }); else onCancel(); }}>
        {t(original ? "Revert" : "Cancel")}</Button>
      <span className="settings-editor-spacer" />
      {original && <RemoveButton text name={`${label(t, form.id)} (${t(sourceLabel(original.source))})`} disabled={disabled} onRemove={onRemove} />}
    </SectionCard>}
  </Section>;
}
