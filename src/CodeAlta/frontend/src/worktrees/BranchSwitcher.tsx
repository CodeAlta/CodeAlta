import { useEffect, useState, type ReactNode } from "react";
import { Callout, InputGroup, Menu, MenuDivider, MenuItem, PopoverNext, type PopoverNextPlacement } from "@blueprintjs/core";
import { worktrees as worktreesApi } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { branchesReply, filterBranches, newBranchName, worktreeFailure, type BranchList } from "./worktrees";

export type BranchApi = Pick<typeof worktreesApi, "branches" | "switch">;

/**
 * The branch of a checkout as a button that opens the branches it can move to: the local ones, the ones of the
 * remotes, and a new one named by what is typed. A checkout a session is working in stays on its branch, and
 * a branch another checkout is on is not offered.
 */
export function BranchSwitcher({ epoch, projectId, worktree, className, title, placement = "top-start", children, onSwitched, api = worktreesApi }: {
  epoch: string; projectId: string;
  /** The folder of the project in a worktree of its repository; null for the folder of the project. */
  worktree: string | null;
  className?: string; title?: string; placement?: PopoverNextPlacement;
  /** The branch as the button shows it. */
  children: ReactNode;
  /** The checkout is on another branch now. */
  onSwitched?: () => void;
  api?: BranchApi;
}) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const [list, setList] = useState<BranchList | string | null>(null);
  const [filter, setFilter] = useState("");
  const [moving, setMoving] = useState<string | null>(null);
  const [failure, setFailure] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    const abort = new AbortController();
    setList(null); setFailure(null); setFilter(""); setMoving(null);
    void api.branches({ expectedEpoch: epoch, projectId, worktree }, { signal: abort.signal, timeoutMilliseconds: 30_000 })
      .then(branchesReply, () => "read_failed").then(value => { if (!abort.signal.aborted) setList(value); });
    return () => abort.abort();
  }, [open, epoch, projectId, worktree, api]);

  const branches = list && typeof list !== "string" ? list.branches : [];
  const busy = !!list && typeof list !== "string" && list.busy;
  const shown = filterBranches(branches, filter);
  const created = list && typeof list !== "string" ? newBranchName(filter, branches) : null;

  function move(name: string, create: boolean) {
    if (moving || busy) return;
    setMoving(name); setFailure(null);
    void api.switch({ expectedEpoch: epoch, projectId, worktree, branch: name, create }, { timeoutMilliseconds: 600_000 })
      .then(reply => reply?.status === "ok" ? null : worktreeFailure(reply?.status ?? "failed", reply?.message, t), () => t("Git could not do it."))
      .then(problem => {
        setMoving(null);
        if (problem) { setFailure(problem); return; }
        setOpen(false);
        onSwitched?.();
      });
  }

  const content = <div className="branch-switcher" role="group" aria-label={t("Switch branch")}>
    <InputGroup size="small" type="search" autoFocus leftIcon={<AppIcon name="search" size={14} className="bp6-icon" />} placeholder={t("Find or create a branch")}
      aria-label={t("Find or create a branch")} value={filter} onValueChange={setFilter} disabled={!!moving}
      onKeyDown={event => {
        if (event.key !== "Enter" || event.nativeEvent.isComposing) return;
        event.preventDefault();
        const first = shown.find(branch => !branch.current && !branch.worktree);
        if (first) move(first.name, false);
        else if (created) move(created, true);
      }} />
    {typeof list === "string" ? <Callout intent="warning" compact>{worktreeFailure(list, null, t)}</Callout>
      : !list ? <div className="branch-switcher-loading"><ActivitySpinner size={18} /></div>
      : <Menu className="branch-switcher-list" aria-label={t("Branches")}>
        {shown.map(branch => {
          // A branch is used by one checkout at a time: the one that has it is named.
          const elsewhere = !branch.current && !!branch.worktree;
          return <MenuItem key={`${branch.remote ? "r" : "l"}:${branch.name}`} roleStructure="listoption" selected={branch.current} shouldDismissPopover={false}
            disabled={busy || !!moving || branch.current || elsewhere}
            icon={moving === branch.name ? <ActivitySpinner size={14} /> : <AppIcon name={elsewhere ? "worktree" : "branch"} size={14} />}
            text={branch.name} title={elsewhere ? t("Used by the worktree {name}", { name: branch.worktreeName ?? "" }) : branch.name}
            label={elsewhere ? branch.worktreeName ?? undefined : branch.remote ? t("remote") : undefined}
            onClick={() => move(branch.name, false)} />;
        })}
        {!shown.length && !created && <MenuItem disabled text={t("No branch matches.")} />}
        {created && <>{shown.length > 0 && <MenuDivider />}
          <MenuItem shouldDismissPopover={false} disabled={busy || !!moving} icon={moving === created ? <ActivitySpinner size={14} /> : <AppIcon name="branchPlus" size={14} />}
            text={t("Create branch “{name}”", { name: created })} onClick={() => move(created, true)} /></>}
      </Menu>}
    {busy && <p className="branch-switcher-note">{t("A session is working here: the branch stays.")}</p>}
    {failure && <Callout intent="danger" compact className="branch-switcher-failure">{failure}</Callout>}
  </div>;

  return <PopoverNext content={content} placement={placement} popoverClassName="branch-switcher-popover" isOpen={open}
    onInteraction={next => { if (!moving) setOpen(next); }}>
    <button type="button" className={className} title={title} aria-label={t("Switch branch")} aria-haspopup="dialog" aria-expanded={open}>{children}</button>
  </PopoverNext>;
}
