import { Radio, RadioGroup } from "@blueprintjs/core";
import { useState } from "react";
import type { WorkspaceSession } from "#neoastra";
import { ConfirmPopover } from "../ConfirmPopover";
import { plainTitle } from "../sessionTitle";
import type { useShellLanguage } from "../shellLanguage";

type ArchiveOrigin = "explorer" | "composer";

/** Keeps one archive question, with its choice of child sessions, at whichever surface opened it. */
export function useSessionArchiveQuestion({ busy, onArchive, t }: {
  busy: boolean; onArchive: (session: WorkspaceSession, withChildren: boolean) => void;
  t: ReturnType<typeof useShellLanguage>["t"];
}) {
  const [target, setTarget] = useState<Readonly<{ id: string; count: number; withChildren: boolean; from: ArchiveOrigin }> | null>(null);
  const [error, setError] = useState("");
  const ask = (id: string, count: number, from: ArchiveOrigin) => setTarget({ id, count, withChildren: true, from });
  const close = () => setTarget(null);

  function render(session: WorkspaceSession, from: ArchiveOrigin) {
    if (target?.id !== session.id || target.from !== from) return null;
    return <ConfirmPopover always title={t("Archive this session?")} subject={plainTitle(session.title)}
      placement={from === "explorer" ? "right-start" : "top-end"}
      detail={t("It leaves the Explorer and keeps its history. Saved sessions and the search find it, to restore it.")}
      confirmLabel={t("Archive")} busy={busy} error={error || null}
      onConfirm={() => onArchive(session, target.withChildren)}
      onCancel={() => { close(); setError(""); }}>
      <RadioGroup className="confirm-choices" selectedValue={target.withChildren ? "tree" : "alone"} disabled={busy}
        onChange={event => { const withChildren = event.currentTarget.value === "tree"; setTarget(value => value && { ...value, withChildren }); }}>
        <Radio value="tree" label={target.count === 1 ? t("Archive with its child session") : t("Archive with its {count} child sessions", { count: target.count })} />
        <Radio value="alone" label={t("Archive this session only")} />
      </RadioGroup>
    </ConfirmPopover>;
  }

  return { ask, close, setError, render };
}
