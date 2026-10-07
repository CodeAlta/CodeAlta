import { useEffect, useId, useState } from "react";
import { Button, HTMLSelect, PopoverNext, SegmentedControl } from "@blueprintjs/core";
import { worktrees as worktreesApi } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { branchesReply, worktreesReply, type Branch, type WorkPlace } from "./worktrees";

export type WorkPlaceApi = Pick<typeof worktreesApi, "branches" | "list">;

/**
 * Where the next session of a project works: in the folder of the project, or in a new git worktree, a
 * checkout of its own on a new branch. The choice is a chip beside the folder; it opens the two places, what a
 * worktree starts from and where it is created.
 */
export function WorkPlaceChoice({ epoch, projectId, branch, place, onPlace, onOpenSettings, api = worktreesApi }: {
  epoch: string; projectId: string;
  /** The branch the folder of the project is on: what a worktree starts from unless another one is chosen. Null when it is not known. */
  branch: string | null;
  place: WorkPlace; onPlace: (place: WorkPlace) => void;
  /** Opens the setting of where worktrees are created. */
  onOpenSettings?: () => void;
  api?: WorkPlaceApi;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const [open, setOpen] = useState(false);
  const [branches, setBranches] = useState<readonly Branch[]>([]);
  const [folder, setFolder] = useState<string | null>(null);

  // What a worktree can start from and where it goes, read while the choice is open.
  useEffect(() => {
    if (!open || !place.worktree) return;
    const abort = new AbortController();
    void api.branches({ expectedEpoch: epoch, projectId, worktree: null }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
      .then(branchesReply, () => "read_failed").then(value => { if (!abort.signal.aborted) setBranches(typeof value === "string" ? [] : value.branches); });
    void api.list({ expectedEpoch: epoch, projectId }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
      .then(reply => worktreesReply(reply, projectId), () => "read_failed")
      .then(value => { if (!abort.signal.aborted) setFolder(typeof value === "string" ? null : value.newFolder); });
    return () => abort.abort();
  }, [open, place.worktree, epoch, projectId, api]);

  // The branch that was chosen stays offered while the list is read, and when it is gone from it.
  const bases = place.base && !branches.some(value => value.name === place.base) ? [place.base, ...branches.map(value => value.name)] : branches.map(value => value.name);
  const content = <div className="work-place-form" role="group" aria-label={t("Where to work")}>
    <div className="work-place-row">
      <span id={`${id}-place`}>{t("Work in")}</span>
      <SegmentedControl size="small" aria-labelledby={`${id}-place`} value={place.worktree ? "worktree" : "project"}
        options={[{ label: t("Project folder"), value: "project" }, { label: t("New worktree"), value: "worktree" }]}
        onValueChange={value => onPlace(value === "worktree" ? { worktree: true, base: place.base } : { worktree: false, base: null })} />
    </div>
    {place.worktree && <>
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

  return <PopoverNext content={content} placement="top-start" popoverClassName="work-place-popover" isOpen={open} onInteraction={setOpen}>
    <button type="button" className="project-context-place" data-worktree={place.worktree || undefined} aria-haspopup="dialog" aria-expanded={open}
      title={t(place.worktree ? "The next session works in a new git worktree" : "The next session works in the folder of the project")} aria-label={t("Where to work")}>
      <AppIcon name={place.worktree ? "worktree" : "folder"} size={13} />
      <span>{place.worktree ? place.base ? t("New worktree from {branch}", { branch: place.base }) : t("New worktree") : t("Project folder")}</span>
      <AppIcon name="chevronDown" size={12} />
    </button>
  </PopoverNext>;
}
