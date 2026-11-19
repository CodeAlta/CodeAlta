import { useEffect, useId, useState } from "react";
import { Button, HTMLSelect, PopoverNext, SegmentedControl } from "@blueprintjs/core";
import { worktrees as worktreesApi } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { folderTargets } from "./sessionFolder";
import { branchesReply, cloudPlace, existingPlace, sameFolder, worktreesReply, type Branch, type WorkPlace, type Worktree } from "./worktrees";

export type WorkPlaceApi = Pick<typeof worktreesApi, "branches" | "list">;

/** The session a worktree is known by, as the choice names it. */
export type WorkPlaceSession = Readonly<{ id: string; title: string }>;

/** Asks the choice to open; `done` is called once it did. */
export type WorkPlaceOpenRequest = Readonly<{ done: () => void }>;

/**
 * Where the next session of a project works: in the folder of the project, or in a git worktree, a checkout of its own.
 * The worktree is a new one, on a new branch, or one that exists, beside the sessions that already work there. The
 * choice is a chip beside the folder, or a part of the Workspace chip of the prompt; it opens the places, then for a
 * worktree which one: what a new one starts from and where it is created, or the branch and the folder of one that exists.
 * A worktree that exists is named with the session it was made for, which the choice leads to.
 */
export function WorkPlaceChoice({ epoch, projectId, branch, place, onPlace, onOpenSettings, api = worktreesApi, chip = false, cloud = false, openRequest = null, madeAhead = null, sessionOf, onOpenSession }: {
  epoch: string; projectId: string;
  /** The branch the folder of the project is on: what a worktree starts from unless another one is chosen. Null when it is not known. */
  branch: string | null;
  place: WorkPlace; onPlace: (place: WorkPlace) => void;
  /** Opens the setting of where worktrees are created. */
  onOpenSettings?: () => void;
  api?: WorkPlaceApi;
  /** Drawn as a part of the Workspace chip of the prompt, which opens its choice below it. */
  chip?: boolean;
  /** Whether the session can run in the cloud: the space has a Claude Code provider. */
  cloud?: boolean;
  /** Opens the choice, as a press on it does: a command asked where the next session works. */
  openRequest?: WorkPlaceOpenRequest | null;
  /** The branch of the worktree git makes ahead for this session: it is removed when the place changes, so it is not offered. */
  madeAhead?: string | null;
  /** The session a worktree is known by: the first one that worked in it. Its title tells the worktrees apart. */
  sessionOf?: (worktree: Worktree) => WorkPlaceSession | null;
  /** Shows a session: the one of the worktree that is chosen. */
  onOpenSession?: (sessionId: string) => void;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const [open, setOpen] = useState(false);
  const [branches, setBranches] = useState<readonly Branch[]>([]);
  const [folder, setFolder] = useState<string | null>(null);
  // The worktrees a session can join: every checkout of the repository whose folder is there, but the one of the project.
  const [worktrees, setWorktrees] = useState<readonly Worktree[]>([]);
  useEffect(() => { if (openRequest) { setOpen(true); openRequest.done(); } }, [openRequest]);

  // What a worktree can start from, where it goes and the ones that exist, read while the choice is open.
  useEffect(() => {
    if (!open || !place.worktree) return;
    const abort = new AbortController();
    void api.branches({ expectedEpoch: epoch, projectId, worktree: null }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
      .then(branchesReply, () => "read_failed").then(value => { if (!abort.signal.aborted) setBranches(typeof value === "string" ? [] : value.branches); });
    void api.list({ expectedEpoch: epoch, projectId }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
      .then(reply => worktreesReply(reply, projectId), () => "read_failed")
      .then(value => {
        if (abort.signal.aborted) return;
        setFolder(typeof value === "string" ? null : value.newFolder);
        setWorktrees(typeof value === "string" ? [] : folderTargets(value.worktrees, null));
      });
    return () => abort.abort();
  }, [open, place.worktree, epoch, projectId, api]);

  // The branch that was chosen stays offered while the list is read, and when it is gone from it.
  const bases = place.base && !branches.some(value => value.name === place.base) ? [place.base, ...branches.map(value => value.name)] : branches.map(value => value.name);
  const existing = place.existing ?? null;
  const offered = madeAhead ? worktrees.filter(worktree => worktree.branch !== madeAhead) : worktrees;
  const chosen = existing ? worktrees.find(worktree => sameFolder(worktree.folder, existing.folder)) ?? null : null;
  const choices = [{ value: "", label: t("New worktree") },
    // The worktree that was chosen stays offered while the list is read, and when it is gone from it.
    ...(existing && !chosen ? [{ value: existing.folder, label: existing.name }] : []),
    // A worktree is told apart by the session it was made for, else by its branch when its name does not say it.
    ...offered.map(worktree => {
      const session = sessionOf?.(worktree);
      return { value: worktree.folder, label: session ? `${worktree.name} — ${session.title}`
        : worktree.branch && worktree.branch !== worktree.name && !worktree.branch.endsWith(`/${worktree.name}`) ? `${worktree.name} (${worktree.branch})` : worktree.name };
    })];
  const chosenSession = chosen ? sessionOf?.(chosen) ?? null : null;
  const choose = (value: string) => {
    const worktree = worktrees.find(item => item.folder === value);
    // The only other value is the worktree that was chosen and is not listed: it stays.
    if (worktree) onPlace(existingPlace(worktree.folder, worktree.name));
    else if (!value) onPlace({ worktree: true, base: null });
  };
  const content = <div className="work-place-form" role="group" aria-label={t("Where to work")}>
    <div className="work-place-row">
      <span id={`${id}-place`}>{t("Work in")}</span>
      <SegmentedControl size="small" aria-labelledby={`${id}-place`} value={place.cloud ? "cloud" : place.worktree ? "worktree" : "project"}
        options={[{ label: t("Project folder"), value: "project" }, { label: t("Worktree"), value: "worktree" },
          ...(cloud || place.cloud ? [{ label: t("Cloud"), value: "cloud" }] : [])]}
        onValueChange={value => onPlace(value === "cloud" ? cloudPlace : value === "worktree" ? place.worktree ? place : { worktree: true, base: null } : { worktree: false, base: null })} />
    </div>
    {place.cloud && <p className="work-place-cloud">{t("Claude Code runs the session on Anthropic's machines, from the branch as it is on GitHub. It goes on while this computer sleeps.")}</p>}
    {place.worktree && <div className="work-place-row">
      <label htmlFor={`${id}-worktree`}>{t("Worktree")}</label>
      <HTMLSelect id={`${id}-worktree`} value={chosen?.folder ?? existing?.folder ?? ""} onChange={event => choose(event.currentTarget.value)} options={choices} />
    </div>}
    {existing && <>
      {chosenSession && <div className="work-place-row">
        <span>{t("Session")}</span>
        {onOpenSession
          ? <button type="button" className="work-place-session" title={t("Open the session {title}", { title: chosenSession.title })}
            onClick={() => { setOpen(false); onOpenSession(chosenSession.id); }}><AppIcon name="chat" size={13} /><span>{chosenSession.title}</span></button>
          : <span className="work-place-session"><AppIcon name="chat" size={13} /><span>{chosenSession.title}</span></span>}
      </div>}
      <div className="work-place-row">
        <span>{t("Branch")}</span>
        <span className="work-place-folder"><AppIcon name="branch" size={13} /><code>{chosen ? chosen.branch ?? chosen.head?.slice(0, 8) ?? "…" : "…"}</code></span>
      </div>
      <div className="work-place-row">
        <span>{t("Folder")}</span>
        <span className="work-place-folder"><code title={existing.folder}>{existing.folder}</code></span>
      </div>
    </>}
    {place.worktree && !existing && <>
      <div className="work-place-row">
        <label htmlFor={`${id}-base`}>{t("Start from")}</label>
        <HTMLSelect id={`${id}-base`} value={place.base ?? ""} onChange={event => onPlace({ worktree: true, base: event.currentTarget.value || null })}
          options={[{ value: "", label: branch ? t("Current commit ({branch})", { branch }) : t("Current commit") }, ...bases.map(name => ({ value: name, label: name }))]} />
      </div>
      <div className="work-place-row">
        <span>{t("Created in")}</span>
        <span className="work-place-folder">
          <code title={folder ?? undefined}>{folder ?? "…"}</code>
          {onOpenSettings && <Button variant="minimal" size="small" icon={<AppIcon name="settings" size={14} />} aria-label={t("Change where worktrees are created")}
            title={t("Change where worktrees are created")} onClick={() => { setOpen(false); onOpenSettings(); }} />}
        </span>
      </div>
    </>}
  </div>;

  const label = place.cloud ? t("Cloud") : existing ? t("Worktree {name}", { name: existing.name })
    : place.worktree ? place.base ? t("New worktree from {branch}", { branch: place.base }) : t("New worktree") : t("Project folder");
  return <PopoverNext content={content} placement={chip ? "bottom-end" : "top-start"} popoverClassName="work-place-popover" isOpen={open} onInteraction={setOpen}>
    <button type="button" className={chip ? "workspace-chip-part workspace-chip-work-place" : "project-context-place"} data-worktree={place.worktree || undefined}
      data-cloud={place.cloud || undefined} aria-haspopup="dialog" aria-expanded={open} aria-label={chip ? `${t("Where to work")}: ${label}` : t("Where to work")}
      title={existing ? `${t("The next session works in the worktree {name}", { name: existing.name })}\n${existing.folder}`
        : t(place.cloud ? "The next session runs in the cloud, with Claude Code" : place.worktree ? "The next session works in a new git worktree" : "The next session works in the folder of the project")}>
      <AppIcon name={place.cloud ? "cloud" : place.worktree ? "worktree" : "folder"} size={chip ? 14 : 13} />
      <span className={chip ? "workspace-chip-name" : undefined}>{label}</span>
      <AppIcon name="chevronDown" size={chip ? 13 : 12} className={chip ? "workspace-chip-caret" : undefined} />
    </button>
  </PopoverNext>;
}
