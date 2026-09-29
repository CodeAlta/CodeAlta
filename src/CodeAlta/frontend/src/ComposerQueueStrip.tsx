import { useState, useSyncExternalStore } from "react";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { ExpandedPromptEditor } from "./ExpandedPromptEditor";
import type { ComposerQueueItem, createComposerQueue } from "./composerQueue";
import { useShellLanguage } from "./shellLanguage";

export function ComposerQueueStrip({ owner, epoch, sessionId, disabled, retry, cancel, steer }: {
  owner: ReturnType<typeof createComposerQueue>; epoch: string; sessionId: string; disabled: boolean;
  retry: (item: ComposerQueueItem) => void; cancel: (item: ComposerQueueItem) => void; steer: (item: ComposerQueueItem) => void;
}) {
  useSyncExternalStore(owner.subscribe, owner.getSnapshot);
  const { t } = useShellLanguage();
  const items = owner.list(epoch, sessionId);
  const [editing, setEditing] = useState<string | null>(null);
  const edited = items.find(item => item.id === editing && item.state === "waiting");
  if (!items.length) return null;
  return <div className="composer-queue-strip" aria-label={t("Queued and steering messages")}>
    {items.map(item => {
      const editable = !disabled && item.state === "waiting";
      return <div className="composer-queue-row" data-kind={item.kind} key={item.id}>
        <AppIcon name={item.kind === "Queue" ? "queue" : "steer"} size={15} />
        <span className="composer-queue-kind">{t(item.kind === "Queue" ? "Queue" : "Steer")}</span>
        <span className="composer-queue-preview" title={item.request.text}>{item.request.text}</span>
        <span className="composer-queue-actions">
          {item.state === "failed" && <span className="error-text" title={item.code}>{t("Failed")}</span>}
          {item.kind === "Queue" && <>
            <input type="number" min={1} max={2147483647} aria-label={t("Repeat count")} title={t("Repeat count")} value={item.count} disabled={!editable}
              onChange={event => owner.edit(item, item.request.text, Number(event.target.value))} />
            <Button size="small" variant="minimal" icon={<AppIcon name="edit" size={14} />} aria-label={t("Edit queued prompt")} title={t("Edit queued prompt")} disabled={!editable} onClick={() => setEditing(item.id)} />
            <Button size="small" variant="minimal" icon={<AppIcon name="steer" size={14} />} aria-label={t("Steer")} title={t("Steer")} disabled={!editable} onClick={() => steer(item)} />
          </>}
          {item.state === "uncertain" && <Button size="small" variant="minimal" icon={<AppIcon name="refresh" size={14} />} aria-label={t("Retry exact request")} title={t("Retry exact request")} disabled={disabled} onClick={() => retry(item)} />}
          <Button size="small" variant="minimal" icon={<AppIcon name="close" size={14} />} aria-label={t("Delete pending message")} title={t("Delete pending message")}
            disabled={disabled || item.state === "sending" || item.state === "uncertain" || item.state === "submitted" && item.kind === "Steer"}
            onClick={() => item.state === "submitted" ? cancel(item) : owner.remove(item)} />
        </span>
      </div>;
    })}
    {edited && !disabled && <ExpandedPromptEditor text={edited.request.text} onChange={text => owner.edit(edited, text)} onClose={() => setEditing(null)} />}
  </div>;
}
