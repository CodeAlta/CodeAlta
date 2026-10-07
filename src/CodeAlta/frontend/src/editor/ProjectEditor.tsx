import { useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import { Button, ButtonGroup, Callout, Menu, MenuDivider, MenuItem, NonIdealState, PopoverNext } from "@blueprintjs/core";
import { projectFiles, projectGit } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { showToast } from "../appToaster";
import { changeListReply } from "../changes/projectChanges";
import { fileAppearance } from "../fileAppearance";
import { fileTabKey, type FileTab } from "../fileTabs";
import type { MessageKey } from "../localization";
import { MarkdownContent } from "../MarkdownContent";
import { SessionTabMenu, type SessionMenuEntry } from "../SessionTabMenu";
import { useShellLanguage } from "../shellLanguage";
import { canSaveDocument, documentChecked, documentConflictDismissed, documentEdited, documentImageRead, documentPreview, documentRead, documentSaved,
  documentSaveUnknown, documentSaving, documentStatus, documentTooLarge, formatFileSize, newDocument, type EditorDocument } from "./editorDocuments";
import { EditorExplorer, type ExplorerEntry, type ExplorerHandle } from "./EditorExplorer";
import { EditorSearch, type SearchHandle } from "./EditorSearch";
import { EditorSurface, type EditorSurfaceHandle, type SurfaceCursor, type SurfaceInfo } from "./EditorSurface";
import { EditorTabs } from "./EditorTabs";
import { activateEditorFile, closeEditorFiles, cycleEditorFile, editorSideWidth, editorStorageKey, moveEditorFile, movedPath, openEditorFile, pinEditorFile, removeEditorPath,
  renameEditorPath, restoreEditorStorage, storedEditor, underPath, updateEditorStorage, type EditorFiles, type EditorPreferences, type EditorSide } from "./editorWorkbench";
import { fileReadFailure } from "./fileEditorState";
import type { FileEditors } from "./fileEditors";
import { emptySearchOptions, searchable, searchEvent, searchKey, startSearch, type SearchMatch, type SearchOptions, type SearchResults } from "./fileSearch";
import { applyTreeListing, collapseTree, emptyFileTree, expandTreeFolders, joinTreePath, noTreeDecorations, parentTreePath, removeTreePath, renameTreePath, toggleTreeFolder,
  treeAncestors, treeBaseName, treeDecorations, treeEntryRows, treeQueries, treeRows, type FileTree, type TreeDecorations, type TreeEdit } from "./fileTree";
import { ImageView } from "./ImageView";
import { DeleteEntryDialog, UnsavedFileDialog } from "./UnsavedDialogs";

/** What the shell asks of a project's editor: a file to open, a place in it, and whether its files are shown. */
export type EditorRequest = Readonly<{ path: string | null; line: number | null; column: number | null;
  /** True shows the files of the project; false leaves them hidden when the editor is first opened; null changes nothing. */
  explorer: boolean | null }>;

type EditorApi = Pick<typeof projectFiles, "read" | "write" | "list" | "stat" | "create" | "rename" | "delete" | "image" | "reveal" | "search">;
type Point = Readonly<{ x: number; y: number }>;
type Pending = Readonly<{ path: string; line: number; column: number; length: number }>;
type Deleting = Readonly<{ path: string; directory: boolean; permanent: boolean; trashFailed: boolean; busy: boolean }>;

const treeRefreshMilliseconds = 4000, statMilliseconds = 2500, decorationMilliseconds = 5000, narrowWidth = 560;
const modalOpen = () => !!document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]');
const unread = { status: "read_failed", content: null, revision: null, readOnly: false, stamp: null, encoding: null, length: 0 } as const;

/**
 * The code editor of one project in a tab: its files as a tree and a search through them on the side, the open
 * files as tabs, and the text of the file shown in Monaco. Files are read through `projectFiles` and saved
 * (Ctrl+S) against the revision that was read. A file that something else changed is read again when it holds no
 * edit, and is never replaced without a choice when it does. Pictures are shown, SVG and Markdown as a drawing
 * or a page as well as text.
 */
export function ProjectEditor({ tab, projectName, epoch, visible, active, platform, editors, request, onActivate, onPickFile, api = projectFiles, git = projectGit }: {
  tab: FileTab;
  /** The name of the project while it is still open; undefined once it is gone. */
  projectName: string | undefined;
  /** The owned host's epoch; null without an owned host, undefined until the host has answered. */
  epoch: string | null | undefined;
  /** The tab is the selected one of its pane. Nothing is read from the disk while it is not. */
  visible: boolean;
  /** This tab is the one commands and the keyboard act on. */
  active: boolean;
  /** "windows", "macos" or "linux": how the trash and the file manager are called. */
  platform: string;
  editors: FileEditors;
  /** What was last asked of this editor; a new object for each request. */
  request?: EditorRequest | null;
  onActivate: () => void;
  /** Opens the file picker of this project. */
  onPickFile?: () => void;
  api?: EditorApi; git?: Pick<typeof projectGit, "changes">;
}) {
  const { t, locale } = useShellLanguage();
  const key = fileTabKey(tab);
  const stored = () => localStorage.getItem(editorStorageKey), store = (value: string) => localStorage.setItem(editorStorageKey, value);
  const [restored] = useState(() => { const storage = restoreEditorStorage(stored); return { preferences: storage.preferences, editor: storedEditor(storage, tab.projectId) }; });
  const [preferences, setPreferences] = useState<EditorPreferences>(restored.preferences);
  // Opened to show one file, the editor starts without its side; asked for the project, with its files.
  const [side, setSide] = useState<EditorSide | null>(() => request?.explorer === true ? "files" : request?.explorer === false ? null : restored.editor.side);
  const [files, setFiles] = useState<EditorFiles>(() => ({ open: restored.editor.files.map(path => ({ path, preview: false })), active: restored.editor.active,
    recent: restored.editor.active ? [restored.editor.active] : [] }));
  const [documents, setDocuments] = useState<ReadonlyMap<string, EditorDocument>>(() => new Map());
  const [modes, setModes] = useState<ReadonlyMap<string, "source" | "preview">>(() => new Map());
  const [tree, setTree] = useState<FileTree>(() => expandTreeFolders(emptyFileTree, restored.editor.expanded));
  const [treeFailure, setTreeFailure] = useState<string | null>(null);
  const [capabilities, setCapabilities] = useState({ trash: false, reveal: false });
  const [decorations, setDecorations] = useState<TreeDecorations>(noTreeDecorations);
  const [selected, setSelected] = useState<string | null>(null);
  const [edit, setEdit] = useState<TreeEdit | null>(null);
  const [searchOptions, setSearchOptions] = useState<SearchOptions>(emptySearchOptions);
  const [submitted, setSubmitted] = useState(0);
  const [results, setResults] = useState<SearchResults | null>(null);
  const [cursor, setCursor] = useState<SurfaceCursor>({ line: 1, column: 1, selected: 0 });
  const [info, setInfo] = useState<SurfaceInfo | null>(null);
  const [imageSize, setImageSize] = useState<Readonly<{ path: string; width: number; height: number }> | null>(null);
  const [previewText, setPreviewText] = useState<Readonly<{ path: string; generation: number; text: string }> | null>(null);
  const [pending, setPending] = useState<Pending | null>(null);
  const [closing, setClosing] = useState<Readonly<{ paths: readonly string[]; busy: boolean }> | null>(null);
  const [deleting, setDeleting] = useState<Deleting | null>(null);
  const [menu, setMenu] = useState<Readonly<{ at: Point; anchor: HTMLElement; title: string; items: SessionMenuEntry[] }> | null>(null);
  const [narrow, setNarrow] = useState(false);

  const root = useRef<HTMLElement>(null);
  const surface = useRef<EditorSurfaceHandle | null>(null);
  const explorer = useRef<ExplorerHandle | null>(null);
  const search = useRef<SearchHandle | null>(null);
  const alive = useRef(true);
  const nextId = useRef(1);
  const latest = useRef({ documents, files, tree, preferences, epoch, narrow });
  latest.current = { documents, files, tree, preferences, epoch, narrow };
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);

  const held = (path: string) => !!latest.current.documents.get(path)?.dirty;
  const pathOf = (id: number) => { for (const [path, document] of latest.current.documents) if (document.id === id) return path; return null; };
  // Applies a change to the document with this id, wherever a rename has put it since.
  function change(id: number, update: (document: EditorDocument) => EditorDocument) {
    setDocuments(current => {
      for (const [path, document] of current) {
        if (document.id !== id) continue;
        const next = update(document);
        return next === document ? current : new Map(current).set(path, next);
      }
      return current;
    });
  }
  function prefer(value: Partial<EditorPreferences>) {
    setPreferences(current => {
      const next = { ...current, ...value };
      updateEditorStorage(stored, store, { preferences: next });
      return next;
    });
  }
  const fail = (message: MessageKey) => showToast({ intent: "danger", icon: "error", timeout: 8000, message: t(message) });

  // Reads a file from the disk: when it is first shown, and again when something else changed it. With `discard`
  // what is read replaces what was typed.
  function load(id: number, path: string, image: boolean, reload: boolean, discard = false) {
    const host = latest.current.epoch;
    if (!host) return;
    if (image) {
      void api.image({ expectedEpoch: host, projectId: tab.projectId, path, reload }, { timeoutMilliseconds: 60_000 }).then(reply => {
        if (alive.current) change(id, document => documentImageRead(document, reply, reply.status === "ok" && reply.base64 && reply.mediaType ? `data:${reply.mediaType};base64,${reply.base64}` : null));
      }, () => { if (alive.current) change(id, document => documentImageRead(document, { status: "read_failed", mediaType: null, stamp: null, length: 0 }, null)); });
      return;
    }
    void api.read({ expectedEpoch: host, projectId: tab.projectId, path, reload }, { timeoutMilliseconds: 15_000 }).then(reply => {
      if (alive.current) change(id, document => documentRead(document, reply, discard));
    }, () => { if (alive.current) change(id, document => documentRead(document, unread, discard)); });
  }

  // The file shown is read the first time it is shown; the other open files wait until they are.
  useEffect(() => {
    const path = files.active;
    if (path === null || epoch === undefined || documents.has(path)) return;
    const created = newDocument(path, nextId.current++);
    setDocuments(current => current.has(path) ? current : new Map(current).set(path, epoch === null ? { ...created, phase: "failed", failure: fileReadFailure("unavailable") } : created));
    if (epoch !== null) load(created.id, path, created.kind === "image", false);
  }, [files.active, epoch, documents]);
  // A closed file gives up its text.
  useEffect(() => {
    const open = new Set(files.open.map(file => file.path));
    if ([...documents.keys()].some(path => !open.has(path))) setDocuments(current => new Map([...current].filter(([path]) => open.has(path))));
    if ([...modes.keys()].some(path => !open.has(path))) setModes(current => new Map([...current].filter(([path]) => open.has(path))));
  }, [files.open, documents, modes]);

  // What the shell shows of this editor, and what it can ask of it from outside.
  const unsaved = useMemo(() => [...documents].filter(([, document]) => document.dirty).map(([path]) => path), [documents]);
  useEffect(() => { editors.setUnsaved(key, unsaved.map(treeBaseName)); }, [editors, key, unsaved]);
  const actions = useRef<{ save: () => Promise<boolean>; closeFile: () => boolean }>({ save: async () => true, closeFile: () => false });
  useEffect(() => editors.attach(key, { save: () => actions.current.save(), closeFile: () => actions.current.closeFile() }), [editors, key]);

  // What is restored the next time: the open files, the side and the open folders.
  const expandedKey = useMemo(() => [...tree.expanded].sort().join("\n"), [tree.expanded]);
  useEffect(() => {
    updateEditorStorage(stored, store, { projectId: tab.projectId,
      editor: { files: files.open.map(file => file.path), active: files.active, side, expanded: expandedKey ? expandedKey.split("\n") : [] } });
  }, [files.open, files.active, side, expandedKey, tab.projectId]);

  useLayoutEffect(() => {
    const node = root.current;
    if (!node) return;
    const measure = () => setNarrow(node.clientWidth > 0 && node.clientWidth < narrowWidth);
    measure();
    const resized = new ResizeObserver(measure);
    resized.observe(node);
    return () => resized.disconnect();
  }, []);

  // The folders that are open are listed again every few seconds while the files are shown, and after each change made here.
  const listing = useRef({ running: false, again: false });
  const refreshTree = useRef(() => { });
  useEffect(() => {
    if (!epoch) { if (epoch === null) setTreeFailure("unavailable"); return; }
    const abort = new AbortController();
    refreshTree.current = () => {
      if (listing.current.running) { listing.current.again = true; return; }
      listing.current.running = true;
      const folders = treeQueries(latest.current.tree);
      void api.list({ expectedEpoch: epoch, projectId: tab.projectId, folders, includeIgnored: latest.current.preferences.ignored }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
        .then(reply => {
          if (abort.signal.aborted) return;
          if (reply.status !== "ok" || reply.folders.length !== folders.length) { setTreeFailure(reply.status === "ok" ? "read_failed" : reply.status); return; }
          setTreeFailure(null);
          setCapabilities(current => current.trash === reply.trash && current.reveal === reply.reveal ? current : { trash: reply.trash, reveal: reply.reveal });
          setTree(current => applyTreeListing(current, reply.folders));
        }, () => { if (!abort.signal.aborted) setTreeFailure("read_failed"); })
        .finally(() => {
          listing.current.running = false;
          if (abort.signal.aborted || !listing.current.again) return;
          listing.current.again = false; refreshTree.current();
        });
    };
    return () => { abort.abort(); listing.current = { running: false, again: false }; refreshTree.current = () => { }; };
  }, [api, epoch, tab.projectId]);
  useEffect(() => {
    if (!visible || !epoch || side !== "files") return;
    refreshTree.current();
    const focused = () => refreshTree.current();
    window.addEventListener("focus", focused);
    const timer = window.setInterval(() => { if (document.visibilityState !== "hidden") refreshTree.current(); }, treeRefreshMilliseconds);
    return () => { window.removeEventListener("focus", focused); window.clearInterval(timer); };
  }, [visible, epoch, side, tree.expanded, preferences.ignored]);

  // The git status of the files, for the colors and letters of the tree.
  useEffect(() => {
    if (!visible || !epoch || side !== "files") return;
    const abort = new AbortController();
    let known: string | null = null, running = false;
    const read = () => {
      if (running || document.visibilityState === "hidden") return;
      running = true;
      void git.changes({ expectedEpoch: epoch, projectId: tab.projectId, comparison: "head", commit: null, worktree: null, knownRevision: known }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
        .then(reply => changeListReply(reply, tab.projectId, known), () => null)
        .then(reply => {
          if (abort.signal.aborted || !reply || reply.kind === "unchanged") return;
          known = reply.kind === "list" ? reply.list.revision : null;
          setDecorations(reply.kind === "list" ? treeDecorations(reply.list.files, reply.list.prefix) : noTreeDecorations);
        }).finally(() => { running = false; });
    };
    read();
    const timer = window.setInterval(read, decorationMilliseconds);
    return () => { abort.abort(); window.clearInterval(timer); };
  }, [git, visible, epoch, side, tab.projectId]);

  // The open files are compared with the disk: one changed elsewhere is read again, or is in conflict when it holds edits.
  useEffect(() => {
    if (!visible || !epoch) return;
    const abort = new AbortController();
    let running = false;
    const check = () => {
      const ready = [...latest.current.documents].filter(([, document]) => document.phase === "ready" && !document.saving && !document.reloading).slice(0, 128);
      if (running || !ready.length || document.visibilityState === "hidden") return;
      running = true;
      void api.stat({ expectedEpoch: epoch, projectId: tab.projectId, paths: ready.map(([path]) => path) }, { signal: abort.signal, timeoutMilliseconds: 15_000 }).then(reply => {
        if (abort.signal.aborted || reply.status !== "ok") return;
        const stats = new Map(reply.files.map(file => [file.path, file]));
        for (const [path, known] of ready) {
          const current = latest.current.documents.get(path), stat = stats.get(path);
          if (!current || current.id !== known.id || !stat) continue;
          const checked = documentChecked(current, stat);
          if (checked === current) continue;
          change(current.id, document => documentChecked(document, stat));
          if (checked.reloading) load(current.id, path, current.kind === "image", true);
        }
      }, () => { /* Asked again at the next tick. */ }).finally(() => { running = false; });
    };
    const focused = () => check();
    window.addEventListener("focus", focused);
    const timer = window.setInterval(check, statMilliseconds);
    return () => { abort.abort(); window.removeEventListener("focus", focused); window.clearInterval(timer); };
  }, [api, visible, epoch, tab.projectId]);

  // The search runs a moment after the typing stops, or at once on Enter; its matches come in as they are found.
  const searched = useRef<{ name: string | null; submitted: number }>({ name: null, submitted: 0 });
  const searchName = `${searchKey(searchOptions)}#${submitted}`;
  useEffect(() => {
    if (!epoch || !visible || side !== "search") return;
    if (!searchable(searchOptions)) { searched.current.name = null; setResults(null); return; }
    if (searched.current.name === searchName) return;
    const abort = new AbortController();
    const key = searchKey(searchOptions);
    const immediate = searched.current.submitted !== submitted;
    searched.current.submitted = submitted;
    let running = false;
    const timer = window.setTimeout(() => {
      searched.current.name = searchName;
      running = true;
      setResults(startSearch(searchOptions));
      let batch: Parameters<typeof searchEvent>[1][] = [], frame = 0;
      const flush = () => { frame = 0; const events = batch; batch = []; setResults(current => current?.key === key ? events.reduce(searchEvent, current) : current); };
      const failed = () => setResults(current => current?.key === key && current.state === "searching" ? { ...current, state: "failed", status: "read_failed" } : current);
      void (async () => {
        try {
          const stream = await api.search({ expectedEpoch: epoch, projectId: tab.projectId, query: searchOptions.query, regex: searchOptions.regex, matchCase: searchOptions.matchCase,
            wholeWord: searchOptions.wholeWord, include: searchOptions.include.trim() || null, exclude: searchOptions.exclude.trim() || null }, { signal: abort.signal });
          for await (const event of stream) {
            if (abort.signal.aborted) return;
            batch.push(event);
            frame ||= requestAnimationFrame(flush);
          }
          if (abort.signal.aborted) return;
          if (frame) { cancelAnimationFrame(frame); flush(); }
          failed(); // Still searching once the host is done means that the end never came.
        } catch { if (!abort.signal.aborted) failed(); }
        finally { running = false; }
      })();
    }, immediate ? 0 : 320);
    return () => {
      window.clearTimeout(timer);
      abort.abort();
      // A search cut short is started again when it is shown again.
      if (running) searched.current.name = null;
    };
  }, [api, epoch, visible, side, searchName]);

  const activePath = files.active;
  const shownDocument = activePath === null ? undefined : documents.get(activePath);
  const previewKind = activePath === null ? null : documentPreview(activePath);
  // A file opens in the view last chosen for its kind: at first a drawing for SVG, the text for a page.
  const mode = activePath !== null && previewKind ? modes.get(activePath) ?? (preferences.previews.includes(previewKind) ? "preview" : "source") : "source";
  function view(path: string, next: "source" | "preview") {
    const kind = documentPreview(path);
    setModes(current => new Map(current).set(path, next));
    if (kind && preferences.previews.includes(kind) !== (next === "preview"))
      prefer({ previews: next === "preview" ? [...preferences.previews, kind] : preferences.previews.filter(value => value !== kind) });
    if (next === "source") focusEditor();
  }
  const textReady = shownDocument?.kind === "text" && shownDocument.phase === "ready";
  const showSurface = textReady && mode === "source";
  const surfaceDocuments = useMemo(() => [...documents].filter(([, document]) => document.kind === "text" && document.phase === "ready")
    .map(([path, document]) => ({ id: document.id, path, text: document.text, generation: document.generation })), [documents]);
  const rows = useMemo(() => treeRows(tree, edit), [tree, edit]);

  // A drawing or a page is made from the text as the editor holds it, unsaved edits included.
  useEffect(() => {
    if (activePath === null || !textReady || mode !== "preview") return;
    const text = surface.current?.read(shownDocument.id)?.text ?? shownDocument.text;
    setPreviewText({ path: activePath, generation: shownDocument.generation, text });
  }, [activePath, textReady, mode, shownDocument?.generation, shownDocument?.id]);

  // The file shown is shown in the tree too: the folders above it open.
  useEffect(() => {
    if (activePath === null || side !== "files") return;
    setTree(current => expandTreeFolders(current, treeAncestors(activePath)));
    setSelected(activePath);
  }, [activePath, side]);

  // A place asked for in a file is gone to once the file is there, as text.
  useEffect(() => {
    if (!pending || pending.path !== activePath || !shownDocument || shownDocument.phase === "loading") return;
    if (shownDocument.kind !== "text" || shownDocument.phase !== "ready") { setPending(null); return; }
    if (mode !== "source") { setModes(current => new Map(current).set(pending.path, "source")); return; }
    const frame = requestAnimationFrame(() => { surface.current?.reveal(pending.line, pending.column, pending.length); setPending(null); });
    return () => cancelAnimationFrame(frame);
  }, [pending, activePath, shownDocument?.phase, shownDocument?.kind, mode]);

  // The text takes the keyboard once it is there: a file asked for is still being read when it is asked for.
  const [focusAsked, setFocusAsked] = useState(0);
  const focusWanted = useRef(false);
  const focusEditor = () => { focusWanted.current = true; setFocusAsked(value => value + 1); };
  useEffect(() => {
    if (!focusWanted.current || !showSurface) return;
    focusWanted.current = false;
    const frame = requestAnimationFrame(() => { if (!modalOpen()) surface.current?.focus(); });
    return () => cancelAnimationFrame(frame);
  }, [focusAsked, showSurface, activePath]);
  const focusFiles = () => requestAnimationFrame(() => { if (!modalOpen()) explorer.current?.focus(); });
  function open(path: string, options: Readonly<{ keep?: boolean; focus?: boolean; line?: number; column?: number; length?: number }> = {}) {
    setFiles(current => openEditorFile(current, path, !options.keep, held));
    if (options.line !== undefined) setPending({ path, line: options.line, column: options.column ?? 1, length: options.length ?? 0 });
    else if (options.focus) focusEditor();
    // In a pane too narrow for both, the file takes the place of the side.
    if (latest.current.narrow) setSide(null);
  }
  function show(next: EditorSide | null) {
    setSide(next);
    if (next === "files") focusFiles();
    else if (next === "search") requestAnimationFrame(() => search.current?.focus());
    else focusEditor();
  }

  // What the shell asked: a file, a place in it, the files of the project.
  const handled = useRef<EditorRequest | null>(null);
  useEffect(() => {
    if (!request || handled.current === request) return;
    handled.current = request;
    if (request.explorer === true) { setSide("files"); if (request.path === null) focusFiles(); }
    if (request.path !== null) open(request.path, { keep: true, focus: true, line: request.line ?? undefined, column: request.column ?? undefined });
  }, [request]);

  // The active tab takes the keyboard: in the text when there is one, in the files otherwise.
  useEffect(() => {
    // On the editor itself the keyboard is nowhere yet: its text, or its files, take it.
    if (!active || modalOpen() || root.current?.contains(document.activeElement) && document.activeElement !== root.current) return;
    if (showSurface) surface.current?.focus(); else if (side === "files") explorer.current?.focus(); else root.current?.focus();
  }, [active, showSurface]);

  async function save(path: string, overwrite = false): Promise<boolean> {
    const current = latest.current.documents.get(path), host = latest.current.epoch;
    if (!current || !host) return false;
    // Nothing to write: the disk already holds this text.
    if (!canSaveDocument(current, overwrite)) return current.phase === "ready" && !current.dirty && !current.conflict && !current.saving;
    const content = surface.current?.read(current.id);
    if (!content) return false;
    if (documentTooLarge(content.text)) { change(current.id, document => documentSaved(document, { status: "too_large", revision: null, stamp: null })); return false; }
    change(current.id, documentSaving);
    try {
      const result = await api.write({ expectedEpoch: host, projectId: tab.projectId, path, content: content.text, expectedRevision: current.revision, overwrite }, { timeoutMilliseconds: 30_000 });
      if (!alive.current) return false;
      change(current.id, document => documentSaved(document, result));
      if (result.status !== "ok" || !result.revision) return false;
      surface.current?.saved(current.id, content.version);
      return true;
    } catch {
      if (alive.current) change(current.id, documentSaveUnknown);
      return false;
    }
  }
  async function saveAll(paths: readonly string[] = [...latest.current.documents].filter(([, document]) => document.dirty).map(([path]) => path)): Promise<boolean> {
    for (const path of paths) {
      if (await save(path)) continue;
      // A refused save shows its file: the reason is in its notice.
      if (alive.current) setFiles(current => activateEditorFile(current, pathOf(latest.current.documents.get(path)?.id ?? -1) ?? path));
      return false;
    }
    return true;
  }
  function reload(path: string) {
    const current = latest.current.documents.get(path);
    if (!current) return;
    // The text on the disk replaces what was typed: the choice was made in the notice or the menu.
    change(current.id, document => ({ ...document, reloading: true }));
    load(current.id, path, current.kind === "image", true, true);
  }
  function close(paths: readonly string[], discard = false) {
    const edited = paths.filter(held);
    if (edited.length && !discard) { setFiles(current => activateEditorFile(current, edited[0])); setClosing({ paths, busy: false }); return; }
    setClosing(null);
    setFiles(current => closeEditorFiles(current, paths));
  }
  async function saveAndClose(paths: readonly string[]) {
    setClosing({ paths, busy: true });
    const saved = await saveAll(paths.filter(held));
    if (!alive.current) return;
    if (saved) close(paths, true); else setClosing(null);
  }
  actions.current = { save: () => saveAll(), closeFile: () => { if (latest.current.files.active === null) return false; close([latest.current.files.active]); return true; } };

  const absolute = (path: string) => { const separator = tab.projectPath.includes("\\") ? "\\" : "/"; return path ? `${tab.projectPath.replace(/[\\/]+$/u, "")}${separator}${path.split("/").join(separator)}` : tab.projectPath; };
  const copy = (text: string) => void navigator.clipboard.writeText(text).catch(() => { /* The clipboard is not available: nothing is copied. */ });
  function reveal(path: string) {
    if (!epoch) return;
    void api.reveal({ expectedEpoch: epoch, projectId: tab.projectId, path }, { timeoutMilliseconds: 15_000 })
      .then(reply => { if (reply.status !== "ok") fail("The file manager could not be opened."); }, () => fail("The file manager could not be opened."));
  }
  const revealLabel = t(platform === "windows" ? "Reveal in File Explorer" : platform === "macos" ? "Reveal in Finder" : "Open containing folder");

  function startCreate(parent: string, directory: boolean) {
    setSide("files");
    setTree(current => expandTreeFolders(current, parent ? [...treeAncestors(parent), parent] : []));
    setEdit({ parent, directory, path: null });
  }
  // A new entry goes into the selected folder, or beside the selected file.
  function createHere(directory: boolean) {
    const row = selected === null ? undefined : treeEntryRows(rows).find(entry => entry.path === selected);
    startCreate(row ? row.directory ? row.path : parentTreePath(row.path) : "", directory);
  }
  async function rename(from: string, to: string) {
    if (!epoch) return;
    const reply = await api.rename({ expectedEpoch: epoch, projectId: tab.projectId, path: from, newPath: to }, { timeoutMilliseconds: 30_000 }).catch(() => null);
    if (!alive.current) return;
    if (reply?.status !== "ok" || !reply.path) {
      fail(reply?.status === "exists" ? "A file or folder with this name already exists." : reply?.status === "invalid" || reply?.status === "outside_root"
        ? "This name is not valid." : reply?.status === "not_found" ? "This file no longer exists." : "The entry could not be renamed.");
      refreshTree.current();
      return;
    }
    const target = reply.path;
    setFiles(current => renameEditorPath(current, from, target));
    setDocuments(current => new Map([...current].map(([path, document]) => [movedPath(path, from, target), document])));
    setModes(current => new Map([...current].map(([path, value]) => [movedPath(path, from, target), value])));
    setPending(current => current && { ...current, path: movedPath(current.path, from, target) });
    setTree(current => expandTreeFolders(renameTreePath(current, from, target), treeAncestors(target)));
    setSelected(target);
    refreshTree.current();
  }
  async function commit(value: TreeEdit, name: string) {
    setEdit(null);
    focusFiles();
    if (!epoch) return;
    if (value.path !== null) { await rename(value.path, joinTreePath(value.parent, name)); return; }
    const reply = await api.create({ expectedEpoch: epoch, projectId: tab.projectId, path: joinTreePath(value.parent, name), directory: value.directory }, { timeoutMilliseconds: 30_000 }).catch(() => null);
    if (!alive.current) return;
    if (reply?.status !== "ok" || !reply.path) {
      fail(reply?.status === "exists" ? "A file or folder with this name already exists." : reply?.status === "invalid" || reply?.status === "outside_root"
        ? "This name is not valid." : value.directory ? "The folder could not be created." : "The file could not be created.");
      refreshTree.current();
      return;
    }
    const created = reply.path;
    setTree(current => expandTreeFolders(current, value.directory ? [...treeAncestors(created), created] : treeAncestors(created)));
    setSelected(created);
    refreshTree.current();
    if (!value.directory) open(created, { keep: true, focus: true });
  }
  async function remove(target: Deleting) {
    if (!epoch) return;
    setDeleting({ ...target, busy: true });
    const reply = await api.delete({ expectedEpoch: epoch, projectId: tab.projectId, path: target.path, permanent: target.permanent }, { timeoutMilliseconds: 120_000 }).catch(() => null);
    if (!alive.current) return;
    if (reply?.status === "ok") {
      setDeleting(null);
      setFiles(current => removeEditorPath(current, target.path));
      setTree(current => removeTreePath(current, target.path));
      setSelected(parentTreePath(target.path) || null);
      refreshTree.current(); focusFiles();
      return;
    }
    // The trash did not take it: nothing is removed for good without asking.
    if (!target.permanent && (reply?.status === "trash_failed" || reply?.status === "trash_unavailable")) { setDeleting({ ...target, permanent: true, trashFailed: true, busy: false }); return; }
    setDeleting(null);
    fail(reply?.status === "not_found" ? "This file no longer exists." : "The entry could not be deleted.");
    refreshTree.current();
  }
  const askDelete = (path: string, directory: boolean) => setDeleting({ path, directory, permanent: !capabilities.trash, trashFailed: false, busy: false });
  function findIn(folder: string) {
    setSearchOptions(current => ({ ...current, include: folder }));
    show("search");
  }

  function entryMenu(row: ExplorerEntry | null, at: Point) {
    const folder = row ? row.directory ? row.path : parentTreePath(row.path) : "";
    const items: SessionMenuEntry[] = [
      ...(row && !row.directory ? [{ key: "open", label: t("Open"), icon: "fileGeneric" as const, onSelect: () => open(row.path, { keep: true, focus: true }) }, { key: "d0", divider: true as const }] : []),
      { key: "file", label: `${t("New file")}…`, icon: "newFile", onSelect: () => startCreate(folder, false) },
      { key: "folder", label: `${t("New folder")}…`, icon: "newFolder", onSelect: () => startCreate(folder, true) },
      ...(row ? [{ key: "d1", divider: true as const },
        { key: "rename", label: `${t("Rename")}…`, icon: "edit" as const, onSelect: () => setEdit({ parent: parentTreePath(row.path), directory: row.directory, path: row.path }) },
        { key: "delete", label: `${t("Delete")}…`, icon: "trash" as const, danger: true, onSelect: () => askDelete(row.path, row.directory) }] : []),
      { key: "d2", divider: true },
      { key: "path", label: t("Copy path"), icon: "copy", onSelect: () => copy(absolute(row?.path ?? "")) },
      ...(row ? [{ key: "relative", label: t("Copy relative path"), icon: "copy" as const, onSelect: () => copy(row.path) }] : []),
      ...(capabilities.reveal ? [{ key: "reveal", label: revealLabel, icon: "openExternal" as const, onSelect: () => reveal(row?.path ?? "") }] : []),
      { key: "d3", divider: true },
      { key: "find", label: `${t(row && !row.directory ? "Find in files" : "Find in folder")}…`, icon: "search", onSelect: () => row && !row.directory ? show("search") : findIn(folder) },
      ...(row ? [] : [{ key: "refresh", label: t("Refresh"), icon: "refresh" as const, onSelect: () => refreshTree.current() }]),
    ];
    setMenu({ at, anchor: root.current!, title: row?.name ?? projectName ?? t("Files"), items });
  }
  function tabMenu(path: string, at: Point) {
    const others = files.open.filter(file => file.path !== path).map(file => file.path);
    const clean = files.open.filter(file => !held(file.path)).map(file => file.path);
    const items: SessionMenuEntry[] = [
      { key: "close", label: t("Close"), icon: "close", onSelect: () => close([path]) },
      { key: "others", label: t("Close others"), disabled: !others.length, onSelect: () => close(others) },
      { key: "saved", label: t("Close saved"), disabled: !clean.length, onSelect: () => close(clean) },
      { key: "all", label: t("Close all"), onSelect: () => close(files.open.map(file => file.path)) },
      { key: "d1", divider: true },
      ...(files.open.some(file => file.path === path && file.preview) ? [{ key: "keep", label: t("Keep open"), onSelect: () => setFiles(current => pinEditorFile(current, path)) }] : []),
      { key: "path", label: t("Copy path"), icon: "copy", onSelect: () => copy(absolute(path)) },
      { key: "relative", label: t("Copy relative path"), icon: "copy", onSelect: () => copy(path) },
      { key: "files", label: t("Show in the files"), icon: "locate", onSelect: () => { setFiles(current => activateEditorFile(current, path)); setSelected(path); setTree(current => expandTreeFolders(current, treeAncestors(path))); show("files"); } },
      ...(capabilities.reveal ? [{ key: "reveal", label: revealLabel, icon: "openExternal" as const, onSelect: () => reveal(path) }] : []),
    ];
    setMenu({ at, anchor: root.current!, title: treeBaseName(path), items });
  }

  // Ctrl+S saves, wherever the keyboard is in the editor; the other keys of the editor are its own.
  function keyDown(event: KeyboardEvent<HTMLElement>) {
    if (event.defaultPrevented || event.nativeEvent.isComposing) return;
    const key = event.key.toLowerCase(), control = (event.ctrlKey || event.metaKey) && !event.altKey;
    if (control && key === "s") { if (event.shiftKey) void saveAll(); else if (activePath !== null) void save(activePath); }
    else if (control && !event.shiftKey && key === "b") show(side === null ? "files" : null);
    else if (control && event.shiftKey && key === "e") show("files");
    else if (control && event.shiftKey && key === "f") {
      const picked = surface.current?.selection() ?? "";
      if (picked) setSearchOptions(current => ({ ...current, query: picked }));
      show("search");
    }
    else if (control && key === "tab") { const next = cycleEditorFile(files, event.shiftKey ? -1 : 1); if (next !== null) { setFiles(current => activateEditorFile(current, next)); focusEditor(); } }
    else if (event.altKey && !event.ctrlKey && !event.metaKey && !event.shiftKey && key === "z") prefer({ wrap: !preferences.wrap });
    else return;
    event.preventDefault(); event.stopPropagation();
  }
  function resize(event: PointerEvent<HTMLDivElement>) {
    if (event.button !== 0) return;
    event.preventDefault();
    const handle = event.currentTarget, start = event.clientX, width = preferences.width;
    handle.setPointerCapture(event.pointerId);
    const moved = (value: globalThis.PointerEvent) => prefer({ width: editorSideWidth(width + value.clientX - start) });
    const done = () => { handle.removeEventListener("pointermove", moved); handle.removeEventListener("pointerup", done); handle.removeEventListener("pointercancel", done); };
    handle.addEventListener("pointermove", moved); handle.addEventListener("pointerup", done); handle.addEventListener("pointercancel", done);
  }

  const name = activePath === null ? null : treeBaseName(activePath);
  const look = activePath === null ? null : fileAppearance(activePath, false);
  const busy = !!shownDocument && (shownDocument.saving || shownDocument.phase === "loading");
  const preview = previewText && previewText.path === activePath ? previewText.text : shownDocument?.text ?? "";
  const size = imageSize && imageSize.path === activePath ? imageSize : null;
  const closingNames = closing ? closing.paths.filter(held).map(treeBaseName).join(", ") : "";
  const sideToggle = <Button variant="minimal" size="small" className="editor-side-toggle" icon={<AppIcon name="sidebar" size={15} />} active={side !== null} aria-pressed={side !== null}
    aria-label={t(side === null ? "Show the files" : "Hide the files")} title={`${t(side === null ? "Show the files" : "Hide the files")} (Ctrl+B)`} onClick={() => show(side === null ? "files" : null)} />;
  const fileActions = activePath !== null && <span className="editor-tab-actions">
    {previewKind && textReady && <ButtonGroup className="editor-mode">
      <Button variant="minimal" size="small" active={mode === "preview"} aria-pressed={mode === "preview"} icon={<AppIcon name="eye" size={14} />}
        aria-label={t("Preview")} title={t("Preview")} onClick={() => view(activePath, "preview")} />
      <Button variant="minimal" size="small" active={mode === "source"} aria-pressed={mode === "source"} icon={<AppIcon name="code" size={14} />}
        aria-label={t("Text")} title={t("Text")} onClick={() => view(activePath, "source")} />
    </ButtonGroup>}
    <PopoverNext placement="bottom-end" content={<Menu className="editor-options">
      <MenuItem icon={<AppIcon name="goto" size={15} />} text={`${t("Go to line")}…`} label="Ctrl+G" disabled={!showSurface} onClick={() => surface.current?.run("editor.action.gotoLine")} />
      <MenuItem icon={<AppIcon name="search" size={15} />} text={`${t("Find")}…`} label="Ctrl+F" disabled={!showSurface} onClick={() => surface.current?.run("actions.find")} />
      <MenuItem icon={<AppIcon name="replace" size={15} />} text={`${t("Replace")}…`} label="Ctrl+H" disabled={!showSurface || !!shownDocument?.readOnly}
        onClick={() => surface.current?.run("editor.action.startFindReplaceAction")} />
      <MenuDivider />
      <MenuItem roleStructure="listoption" selected={preferences.wrap} shouldDismissPopover={false} icon={<AppIcon name="wrap" size={15} />} text={t("Wrap lines")} label="Alt+Z"
        onClick={() => prefer({ wrap: !preferences.wrap })} />
      <MenuItem roleStructure="listoption" selected={preferences.minimap} shouldDismissPopover={false} icon={<AppIcon name="minimap" size={15} />} text={t("Minimap")}
        onClick={() => prefer({ minimap: !preferences.minimap })} />
      <MenuDivider />
      <MenuItem icon={<AppIcon name="refresh" size={15} />} text={t("Reload from disk")} disabled={!shownDocument || shownDocument.phase === "loading" || busy}
        onClick={() => { if (shownDocument?.dirty) setClosing(null); reload(activePath); }} />
      <MenuItem icon={<AppIcon name="copy" size={15} />} text={t("Copy path")} onClick={() => copy(absolute(activePath))} />
      {capabilities.reveal && <MenuItem icon={<AppIcon name="openExternal" size={15} />} text={revealLabel} onClick={() => reveal(activePath)} />}
    </Menu>}>
      <Button variant="minimal" size="small" icon={<AppIcon name="ellipsis" size={15} />} aria-label={t("Editor options")} title={t("Editor options")} />
    </PopoverNext>
  </span>;

  return <section ref={root} className="project-editor" data-active={active} data-side={side ?? "none"} data-narrow={narrow} tabIndex={-1}
    aria-label={`${t("Editor")} · ${projectName ?? tab.projectPath}`} onFocusCapture={onActivate} onPointerDownCapture={onActivate} onKeyDownCapture={keyDown}>
    {side !== null && <>
      <aside className="editor-side" style={{ width: preferences.width }}>
        <div className="editor-side-views" role="tablist" aria-label={t("Editor")}>
          <button type="button" role="tab" className="editor-side-view" aria-selected={side === "files"} title={`${t("Files")} (Ctrl+Shift+E)`} onClick={() => show("files")}>
            <AppIcon name="files" size={14} /><span>{t("Files")}</span></button>
          <button type="button" role="tab" className="editor-side-view" aria-selected={side === "search"} title={`${t("Search in files")} (Ctrl+Shift+F)`} onClick={() => show("search")}>
            <AppIcon name="search" size={14} /><span>{t("Search")}</span></button>
          {narrow && <Button variant="minimal" size="small" icon={<AppIcon name="close" size={15} />} aria-label={t("Hide the files")} title={t("Hide the files")} onClick={() => show(null)} />}
        </div>
        {side === "files" && <header className="editor-side-header">
          <strong className="editor-side-title" title={tab.projectPath}>{projectName ?? t("Unavailable project")}</strong>
          <span className="editor-side-actions">
            <Button variant="minimal" size="small" icon={<AppIcon name="newFile" size={15} />} aria-label={t("New file")} title={t("New file")} disabled={!epoch} onClick={() => createHere(false)} />
            <Button variant="minimal" size="small" icon={<AppIcon name="newFolder" size={15} />} aria-label={t("New folder")} title={t("New folder")} disabled={!epoch} onClick={() => createHere(true)} />
            <Button variant="minimal" size="small" icon={<AppIcon name="collapseAll" size={15} />} aria-label={t("Collapse all folders")} title={t("Collapse all folders")}
              disabled={!tree.expanded.size} onClick={() => setTree(collapseTree)} />
            <PopoverNext placement="bottom-end" content={<Menu>
              <MenuItem icon={<AppIcon name="refresh" size={15} />} text={t("Refresh")} onClick={() => refreshTree.current()} />
              <MenuItem roleStructure="listoption" selected={preferences.ignored} shouldDismissPopover={false} icon={<AppIcon name="eye" size={15} />} text={t("Show ignored files")}
                onClick={() => prefer({ ignored: !preferences.ignored })} />
              {capabilities.reveal && <MenuItem icon={<AppIcon name="openExternal" size={15} />} text={revealLabel} onClick={() => reveal("")} />}
            </Menu>}>
              <Button variant="minimal" size="small" icon={<AppIcon name="ellipsis" size={15} />} aria-label={t("More actions")} title={t("More actions")} />
            </PopoverNext>
          </span>
        </header>}
        {side === "search" ? <EditorSearch options={searchOptions} results={results} handle={search} onOptions={value => setSearchOptions(current => ({ ...current, ...value }))}
            onSubmit={() => setSubmitted(value => value + 1)}
            onOpen={(path, match: SearchMatch, keep) => open(path, { keep, line: match.line, column: match.column, length: match.length })} />
          : !tree.folders.has("") ? <NonIdealState className="editor-side-empty" icon={treeFailure ? <AppIcon name="folder" size={30} /> : <ActivitySpinner size={22} />}
              title={treeFailure ? t("The files could not be read.") : t("Loading…")} description={treeFailure ? t(fileReadFailure(treeFailure)) : undefined}
              action={treeFailure ? <Button size="small" icon={<AppIcon name="refresh" size={14} />} disabled={!epoch} onClick={() => refreshTree.current()}>{t("Refresh")}</Button> : undefined} />
          : <EditorExplorer tree={tree} rows={rows} selected={selected} active={activePath} decorations={decorations} label={t("Files")} handle={explorer}
              onSelect={setSelected} onOpen={(path, keep) => open(path, { keep, focus: keep })} onToggle={path => setTree(current => toggleTreeFolder(current, path))}
              onCommit={(value, typed) => void commit(value, typed)} onCancel={() => { setEdit(null); focusFiles(); }} onMenu={entryMenu}
              onAction={(action, row) => action === "delete" ? askDelete(row.path, row.directory) : setEdit({ parent: parentTreePath(row.path), directory: row.directory, path: row.path })}
              onMove={(path, folder) => void rename(path, joinTreePath(folder, treeBaseName(path)))} />}
      </aside>
      <div className="editor-side-splitter" role="separator" aria-orientation="vertical" aria-label={t("Resize the files")} onPointerDown={resize}
        onDoubleClick={() => prefer({ width: 264 })} />
    </>}
    <div className="editor-main">
      <EditorTabs files={files.open} active={activePath} leading={sideToggle} trailing={fileActions}
        state={path => { const document = documents.get(path); return { dirty: !!document?.dirty, missing: !!document?.missing }; }}
        onSelect={path => { setFiles(current => activateEditorFile(current, path)); focusEditor(); }} onClose={path => close([path])}
        onPin={path => setFiles(current => pinEditorFile(current, path))} onMove={(path, index) => setFiles(current => moveEditorFile(current, path, index))} onMenu={tabMenu} />
      {shownDocument?.conflict && activePath !== null && <Callout className="file-editor-notice" intent="warning" compact role="alert">
        <span>{t("The file changed on disk since it was opened.")}</span>
        <span className="file-editor-notice-actions">
          <Button size="small" disabled={busy} onClick={() => reload(activePath)}>{t("Reload")}</Button>
          <Button size="small" intent="warning" disabled={!canSaveDocument(shownDocument, true)} onClick={() => void save(activePath, true)}>{t("Overwrite")}</Button>
          <Button size="small" variant="minimal" disabled={busy} onClick={() => change(shownDocument.id, documentConflictDismissed)}>{t("Cancel")}</Button>
        </span>
      </Callout>}
      {shownDocument && !shownDocument.conflict && shownDocument.missing && activePath !== null && <Callout className="file-editor-notice" intent="warning" compact role="alert">
        <span>{t("This file no longer exists.")}</span>
        <span className="file-editor-notice-actions"><Button size="small" onClick={() => close([activePath], true)}>{t("Close")}</Button></span>
      </Callout>}
      {shownDocument && !shownDocument.conflict && !shownDocument.missing && shownDocument.notice
        && <Callout className="file-editor-notice" intent={shownDocument.notice.intent} compact role="alert">{t(shownDocument.notice.key)}</Callout>}
      <div className="editor-stage">
        <EditorSurface documents={surfaceDocuments} active={showSurface ? shownDocument.id : null} hidden={!showSurface} readOnly={!!shownDocument?.readOnly || !!shownDocument?.saving}
          wrap={preferences.wrap} minimap={preferences.minimap} label={activePath ?? t("Editor")} handle={surface}
          onDirty={(id, dirty) => { change(id, document => documentEdited(document, dirty)); const path = dirty ? pathOf(id) : null; if (path !== null) setFiles(current => pinEditorFile(current, path)); }}
          onCursor={setCursor} onInfo={setInfo} />
        {activePath === null ? <NonIdealState className="editor-empty" icon={<AppIcon name="code" size={40} />} title={t("No file is open")}
            description={<span className="editor-empty-keys">
              <span><kbd>Ctrl+E</kbd>{t("Open a file by name")}</span>
              <span><kbd>Ctrl+Shift+E</kbd>{t("Browse the files of the project")}</span>
              <span><kbd>Ctrl+Shift+F</kbd>{t("Search in files")}</span>
            </span>}
            action={<span className="editor-empty-actions">
              {onPickFile && <Button icon={<AppIcon name="search" size={15} />} disabled={!epoch} onClick={onPickFile}>{`${t("Open file")}…`}</Button>}
              {side !== "files" && <Button icon={<AppIcon name="files" size={15} />} onClick={() => show("files")}>{t("Show the files")}</Button>}
            </span>} />
          : !shownDocument || shownDocument.phase === "loading" ? <NonIdealState className="editor-empty" icon={<ActivitySpinner size={28} />} title={t("Loading…")} />
          : shownDocument.phase === "failed" ? <NonIdealState className="editor-empty" icon={<AppIcon name={look!.icon} size={36} />} title={name!}
              description={t(shownDocument.failure ?? "The file could not be read.")}
              action={<Button icon={<AppIcon name="refresh" size={15} />} disabled={!epoch} onClick={() => setDocuments(current => new Map([...current].filter(([path]) => path !== activePath)))}>{t("Reload")}</Button>} />
          : shownDocument.kind === "image" && shownDocument.image ? <ImageView key={`${activePath}:${shownDocument.generation}`} url={shownDocument.image.url} label={activePath}
              onSize={value => setImageSize(value && { path: activePath, ...value })} />
          : mode === "preview" && previewKind === "svg" ? <ImageView vector url={`data:image/svg+xml;charset=utf-8,${encodeURIComponent(preview)}`} label={activePath}
              onSize={value => setImageSize(value && { path: activePath, ...value })} />
          : mode === "preview" ? <div className="editor-markdown"><div className="markdown-content"><MarkdownContent source={preview} /></div></div>
          : null}
      </div>
      <footer className="editor-status">
        {shownDocument && activePath !== null ? <>
          <span className="file-editor-status" data-state={shownDocument.conflict || shownDocument.missing ? "conflict" : shownDocument.dirty ? "modified" : "clean"} role="status">
            {(busy || shownDocument.reloading) && <ActivitySpinner size={12} />}{t(documentStatus(shownDocument))}</span>
          <span className="editor-status-path" title={activePath}><span className="file-tab-icon" data-file-tone={look!.tone}><AppIcon name={look!.icon} size={13} /></span><span>{activePath}</span></span>
          {showSurface && <button type="button" className="editor-status-item" title={`${t("Go to line")} (Ctrl+G)`} onClick={() => surface.current?.run("editor.action.gotoLine")}>
            {t("Ln {line}, Col {column}", cursor)}{cursor.selected > 0 && ` (${t("{count} selected", { count: cursor.selected.toLocaleString(locale) })})`}</button>}
          {showSurface && info && <span className="editor-status-item">{info.spaces ? t("Spaces: {count}", { count: info.tabSize }) : t("Tab size: {count}", { count: info.tabSize })}</span>}
          {showSurface && info && <span className="editor-status-item">{info.eol}</span>}
          {textReady && shownDocument.encoding && <span className="editor-status-item">{shownDocument.encoding}</span>}
          {showSurface && info && <span className="editor-status-item">{info.language}</span>}
          {(shownDocument.kind === "image" || mode === "preview" && previewKind === "svg") && size && <span className="editor-status-item">{size.width} × {size.height}</span>}
          {shownDocument.phase === "ready" && shownDocument.length > 0 && shownDocument.kind === "image" && <span className="editor-status-item">{formatFileSize(shownDocument.length, locale)}</span>}
          {shownDocument.kind === "image" && shownDocument.image && <span className="editor-status-item">{shownDocument.image.mediaType.replace("image/", "").replace("x-icon", "ico").toUpperCase()}</span>}
          {shownDocument.kind === "text" && <Button variant="minimal" size="small" icon={<AppIcon name="save" size={14} />} disabled={!canSaveDocument(shownDocument)}
            title={`${t("Save")} (Ctrl+S)`} onClick={() => void save(activePath)}>{t("Save")}</Button>}
        </> : <span className="editor-status-path"><span>{tab.projectPath}</span></span>}
      </footer>
    </div>
    {closing && <UnsavedFileDialog name={closingNames} mode="close" busy={closing.busy} onSave={() => void saveAndClose(closing.paths)}
      onDiscard={() => close(closing.paths, true)} onCancel={() => setClosing(null)} />}
    {deleting && <DeleteEntryDialog name={treeBaseName(deleting.path)} directory={deleting.directory} permanent={deleting.permanent} trashFailed={deleting.trashFailed}
      unsaved={unsaved.filter(path => underPath(path, deleting.path)).length} platform={platform} busy={deleting.busy}
      onDelete={() => void remove(deleting)} onCancel={() => { setDeleting(null); focusFiles(); }} />}
    {menu && <SessionTabMenu anchor={menu.anchor} at={menu.at} items={menu.items} title={menu.title} container={document.body} current={() => true}
      onClose={() => queueMicrotask(() => setMenu(null))} />}
  </section>;
}
