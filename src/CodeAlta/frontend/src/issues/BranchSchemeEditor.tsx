import { useEffect, useId, useRef, useState } from "react";
import { Button, Callout, InputGroup, SegmentedControl } from "@blueprintjs/core";
import type { issues as issuesService, IssueBranchSchemeRow, IssueRow, settingsFiles } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { AppWindow } from "../AppWindow";
import { useShellLanguage } from "../shellLanguage";
import { branchSchemeProblem, isTypeText, suggestBranchName, templateProblem } from "./issueBranch";

/** What the form of the scheme asks of the host. */
export type BranchSchemeApi = Pick<typeof issuesService, "branchScheme" | "saveBranchScheme">;
/** Opens the file of the scheme in the code editor. */
export type BranchSchemeFilesApi = Pick<typeof settingsFiles, "open">;

type Scope = "personal" | "team";
type Row = Readonly<{ key: number; name: string; value: string }>;
type Form = Readonly<{ template: string; titleLength: string; rows: readonly Row[]; others: string; own: boolean; path: string | null }>;

const placeholders = ["type", "id", "title"] as const;

/** The form of the scheme of the branches of a project, in a window of its own: it holds what the user types, so a press beside it leaves it open. */
export function BranchSchemeWindow({ projectName, onClose, ...form }: Parameters<typeof BranchSchemeEditor>[0] & { projectName: string; onClose: () => void }) {
  const { t } = useShellLanguage();
  const titleId = useId();
  return <AppWindow storageKey="codealta.desktop.branch-scheme-window.v1" title={t("Branch name scheme of {project}", { project: projectName })} titleId={titleId}
    className="branch-scheme-window" preferredSize={viewport => ({ width: Math.min(560, viewport.width - 80), height: Math.min(720, viewport.height - 60) })}
    onClose={onClose} closeLabel={t("Close")} keepOnOutsidePress onCancel={event => { event.preventDefault(); onClose(); }}>
    <BranchSchemeEditor {...form} />
  </AppWindow>;
}

/**
 * The form of the scheme of the branches of a project: the template, the longest title and what `{type}` is for each
 * type, for the user (`.alta/local/issues.toml`) or the team (`.alta/issues.toml`), with the name it gives the item shown.
 * It writes the whole `[branch]` table of that file, and can remove it or open the file.
 */
export function BranchSchemeEditor({ api, files, epoch, projectId, item, onSaved, onOpenedFile }: {
  api: BranchSchemeApi; files: BranchSchemeFilesApi | null; epoch: string; projectId: string;
  /** The item whose branch the preview names. */
  item: IssueRow;
  /** The scheme that applies once the form wrote or removed a table. */
  onSaved: (scheme: IssueBranchSchemeRow) => void;
  onOpenedFile: () => void;
}) {
  const { t } = useShellLanguage();
  const [scope, setScope] = useState<Scope>("personal");
  const [form, setForm] = useState<Form | null>(null);
  const [loadProblem, setLoadProblem] = useState<string | null>(null);
  const [message, setMessage] = useState<{ text: string; intent: "danger" | "success" } | null>(null);
  const [busy, setBusy] = useState(false);
  const nextKey = useRef(0);
  // Read again once a table is removed, saying so: the form then shows what applies without it.
  const [generation, setGeneration] = useState(0);
  const afterLoad = useRef<{ text: string; intent: "danger" | "success" } | null>(null);
  const templateInput = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    setForm(null); setMessage(afterLoad.current); setLoadProblem(null);
    afterLoad.current = null;
    void api.branchScheme({ expectedEpoch: epoch, projectId, scope }, { signal: controller.signal }).then(reply => {
      if (controller.signal.aborted) return;
      if (reply.status !== "ok" || !reply.scheme) { setMessage({ text: t("The scheme could not be read."), intent: "danger" }); return; }
      const scheme = reply.scheme;
      setLoadProblem(scheme.problem);
      setForm({
        template: scheme.template, titleLength: String(scheme.titleLength), own: reply.own, path: reply.path,
        rows: scheme.types.filter(type => type.name !== "*").map(type => ({ key: nextKey.current++, name: type.name, value: type.value })),
        others: scheme.types.find(type => type.name === "*")?.value ?? "feature",
      });
    }, () => { if (!controller.signal.aborted) setMessage({ text: t("The scheme could not be read."), intent: "danger" }); });
    return () => controller.abort();
  }, [api, epoch, projectId, scope, generation]);

  const change = (update: Partial<Form>) => { setForm(value => value && { ...value, ...update }); setMessage(null); };
  const changeRow = (key: number, update: Partial<Row>) => form && change({ rows: form.rows.map(row => row.key === key ? { ...row, ...update } : row) });
  // The order of the rows is their priority: the first that matches the type, else a label, wins.
  const moveRow = (index: number, by: -1 | 1) => {
    if (!form || index + by < 0 || index + by >= form.rows.length) return;
    const rows = [...form.rows];
    [rows[index], rows[index + by]] = [rows[index + by]!, rows[index]!];
    change({ rows });
  };
  const insert = (placeholder: string) => {
    if (!form) return;
    const input = templateInput.current;
    const start = input?.selectionStart ?? form.template.length, end = input?.selectionEnd ?? start;
    const text = `{${placeholder}}`;
    change({ template: form.template.slice(0, start) + text + form.template.slice(end) });
    requestAnimationFrame(() => { input?.focus(); input?.setSelectionRange(start + text.length, start + text.length); });
  };

  const scheme = form && {
    template: form.template.trim(), titleLength: Number(form.titleLength),
    types: [...form.rows.map(row => ({ name: row.name.trim(), value: row.value.trim() })), { name: "*", value: form.others.trim() }],
  };
  const schemeProblem = scheme ? branchSchemeProblem(scheme) : null;
  const problem = schemeProblem ? t(schemeProblem.key, schemeProblem.values) : null;
  const preview = scheme && !problem ? suggestBranchName(item, scheme) : null;

  async function write(remove: boolean) {
    if (!form || !scheme || busy || (!remove && problem)) return;
    setBusy(true); setMessage(null);
    try {
      const reply = await api.saveBranchScheme({ expectedEpoch: epoch, projectId, scope, remove,
        template: remove ? null : scheme.template, titleLength: remove ? null : scheme.titleLength, types: remove ? null : scheme.types });
      if (reply.status === "ok" && reply.branch) {
        onSaved(reply.branch);
        if (remove) { afterLoad.current = { text: t("Removed."), intent: "success" }; setGeneration(value => value + 1); }
        else { setLoadProblem(reply.branch.problem); change({ own: true }); setMessage({ text: t("Saved."), intent: "success" }); }
      } else setMessage({ text: reply.message ?? t("The operation did not complete."), intent: "danger" });
    } catch { setMessage({ text: t("The operation did not complete."), intent: "danger" }); }
    finally { setBusy(false); }
  }
  const open = () => {
    if (!files) return;
    void files.open({ expectedEpoch: epoch, projectId, kind: "issues", scope: scope === "team" ? "Project" : "Local", id: null, part: null }).then(
      reply => reply.status === "ok" ? onOpenedFile() : setMessage({ text: t("The file could not be opened."), intent: "danger" }),
      () => setMessage({ text: t("The file could not be opened."), intent: "danger" }));
  };

  // While a write runs, nothing of the form changes: neither the file it is for nor what it holds, so what the answer says is about what is shown.
  return <fieldset className="branch-scheme" disabled={busy} aria-busy={busy} aria-label={t("Branch name scheme")}>
    <header className="branch-scheme-head">
      <SegmentedControl size="small" value={scope} onValueChange={value => setScope(value as Scope)}
        options={[{ value: "personal", label: t("For me") }, { value: "team", label: t("For the team") }]} />
    </header>
    <small className="branch-scheme-note">{t(scope === "team" ? "Written in .alta/issues.toml: commit it to share it with your team." : "Written in .alta/local/issues.toml, which git ignores.")}
      {form && !form.own && <> {t(scope === "team" ? "It has no scheme yet: this is the default." : "It has no scheme yet: this is the one of the team.")}</>}</small>
    {!form ? (message ? <Callout intent="danger" compact>{message.text}</Callout> : <p className="branch-scheme-loading">{t("Loading…")}</p>) : <>
      <label className="branch-scheme-field">
        <span>{t("Template")}</span>
        <InputGroup size="small" inputRef={templateInput} value={form.template} spellCheck={false} intent={templateProblem(form.template.trim()) ? "danger" : "none"}
          aria-label={t("Template")} onChange={event => change({ template: event.target.value })} />
      </label>
      <div className="branch-scheme-placeholders" role="group" aria-label={t("Insert a placeholder")}>
        {placeholders.map(name => <Button key={name} size="small" variant="outlined" onClick={() => insert(name)}><code>{`{${name}}`}</code></Button>)}
      </div>
      <label className="branch-scheme-field branch-scheme-length">
        <span>{t("Longest title")}</span>
        <InputGroup size="small" type="number" min={1} max={120} value={form.titleLength} aria-label={t("Longest title")} onChange={event => change({ titleLength: event.target.value })} />
      </label>
      {/* A template without {type} has no use for the types: they stay in the file, out of the way. */}
      {!form.template.includes("{type}") ? <p className="branch-scheme-note">{t("The template does not use the type placeholder: the types are not used.")}</p> : <>
      <table className="branch-scheme-types">
        <thead><tr><th>{t("Type or label")}</th><th><code>{"{type}"}</code></th><th /></tr></thead>
        <tbody>
          {form.rows.map((row, index) => <tr key={row.key}>
            <td><InputGroup size="small" value={row.name} placeholder="Bug" aria-label={t("Type or label")} onChange={event => changeRow(row.key, { name: event.target.value })} /></td>
            <td><InputGroup size="small" value={row.value} placeholder="fix" spellCheck={false} intent={isTypeText(row.value.trim()) ? "none" : "danger"} aria-label={t("Text for the type")}
              onChange={event => changeRow(row.key, { value: event.target.value })} /></td>
            <td className="branch-scheme-row-actions">
              <Button variant="minimal" size="small" icon={<AppIcon name="arrowUp" size={13} />} disabled={index === 0} aria-label={t("Move the type {type} up", { type: row.name || "…" })}
                title={t("Move up")} onClick={() => moveRow(index, -1)} />
              <Button variant="minimal" size="small" icon={<AppIcon name="arrowDown" size={13} />} disabled={index === form.rows.length - 1}
                aria-label={t("Move the type {type} down", { type: row.name || "…" })} title={t("Move down")} onClick={() => moveRow(index, 1)} />
              <Button variant="minimal" size="small" icon={<AppIcon name="close" size={13} />} aria-label={t("Remove the type {type}", { type: row.name || "…" })}
                onClick={() => change({ rows: form.rows.filter(other => other.key !== row.key) })} />
            </td>
          </tr>)}
          <tr className="branch-scheme-others">
            <td>{t("All others")}</td>
            <td><InputGroup size="small" value={form.others} spellCheck={false} intent={isTypeText(form.others.trim()) ? "none" : "danger"} aria-label={t("Text for all other types")}
              onChange={event => change({ others: event.target.value })} /></td>
            <td />
          </tr>
        </tbody>
      </table>
      <Button className="branch-scheme-add" size="small" variant="minimal" icon={<AppIcon name="plus" size={13} />}
        onClick={() => change({ rows: [...form.rows, { key: nextKey.current++, name: "", value: "" }] })}>{t("Add a type")}</Button>
      <p className="branch-scheme-note">{t("The first row that matches the type of the item, else one of its labels, wins.")}</p>
      </>}
      <p className="branch-scheme-preview">{t("Preview")}: {preview ? <code>{preview}</code> : <span className="branch-scheme-problem">{problem}</span>}</p>
      {loadProblem && <Callout intent="warning" compact className="branch-scheme-callout">{loadProblem}</Callout>}
      {message && <Callout intent={message.intent} compact className="branch-scheme-callout" role={message.intent === "danger" ? "alert" : "status"}>{message.text}</Callout>}
      <footer className="branch-scheme-actions">
        <Button size="small" intent="primary" disabled={busy || !!problem} icon={<AppIcon name="save" size={13} />} onClick={() => void write(false)}>{t("Save")}</Button>
        {form.own && <Button size="small" disabled={busy} icon={<AppIcon name="reset" size={13} />} onClick={() => void write(true)}
          title={t(scope === "team" ? "Removes the scheme of this file: the default applies again." : "Removes the scheme of this file: the one of the team applies again.")}>
          {t("Remove from this file")}</Button>}
        {form.own && files && <Button size="small" variant="minimal" icon={<AppIcon name="file" size={13} />} onClick={open}>{t("Open file")}</Button>}
      </footer>
    </>}
  </fieldset>;
}
