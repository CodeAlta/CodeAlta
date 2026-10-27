import { createContext, useContext, useEffect, useId, useRef, useState } from "react";
import { Button, InputGroup, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";

/**
 * What the card of a run can ask of the session it is in: to turn the run into a script of the library, or to run it again
 * (resumed, over its recorded results, or from a step). Each gives why the request was not sent, or null; the context is
 * null where the session cannot be asked, such as a session of another host.
 */
export type ScriptRunActions = Readonly<{
  saveAsScript(runId: string, name: string): Promise<string | null>;
  resume(runId: string, how: Readonly<{ from?: string; all?: boolean }>): Promise<string | null>;
}>;
export const ScriptRunActionsContext = createContext<ScriptRunActions | null>(null);

/** The name a script is saved under: lowercase letters, digits and hyphens, starting with a letter, as the library names them. */
export const isScriptName = (name: string) => /^[a-z][a-z0-9-]{0,63}$/u.test(name);

/**
 * Save as script… on the card of a run that ended: a small popover beside the button asks for the name, then the session of
 * the run is asked to turn it into a script of the library, with parameters in place of the values it was written for.
 */
export function SaveAsScript({ runId }: { runId: string }) {
  const { t } = useShellLanguage();
  const actions = useContext(ScriptRunActionsContext);
  const id = useId();
  const [open, setOpen] = useState(false);
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if (!open) return;
    const timer = setTimeout(() => input.current?.select(), 0);
    return () => clearTimeout(timer);
  }, [open]);
  if (!actions) return null;
  function show() { setName(""); setProblem(null); setOpen(true); }
  async function save() {
    const chosen = name.trim();
    if (!isScriptName(chosen)) { setProblem(t("Lowercase letters, digits and hyphens, starting with a letter.")); return; }
    setBusy(true);
    const failure = await actions!.saveAsScript(runId, chosen);
    setBusy(false);
    if (failure === null) setOpen(false);
    else setProblem(failure || t("The session could not be asked."));
  }
  const form = <form className="confirm-form script-save-form" aria-labelledby={`${id}-title`}
    // What is done in the popover stays there: the tile of the call it is under does not react to it.
    onClick={event => event.stopPropagation()} onPointerDown={event => event.stopPropagation()}
    onSubmit={event => { event.preventDefault(); void save(); }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.key === "Escape" && !busy) { event.preventDefault(); setOpen(false); }
    }}>
    <strong id={`${id}-title`}>{t("Save as script")}</strong>
    <InputGroup inputRef={input} size="small" value={name} disabled={busy} spellCheck={false} aria-label={t("Name")} placeholder="review-pr"
      onChange={event => { setName(event.target.value); setProblem(null); }} />
    {problem && <p role="alert" className="confirm-error">{problem}</p>}
    <div className="rename-actions">
      <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={() => setOpen(false)} />
      <Button type="submit" intent="primary" size="small" text={t("Save")} loading={busy} />
    </div>
  </form>;
  return <PopoverNext isOpen={open} content={form} placement="bottom-start" popoverClassName="rename-popover" autoFocus={false} enforceFocus={false}
    canEscapeKeyClose={!busy} onInteraction={next => { if (!next && !busy) setOpen(false); }}>
    <Button size="small" variant="minimal" className="script-save" icon={<AppIcon name="save" size={13} />} text={t("Save as script…")}
      onClick={event => { event.stopPropagation(); if (open) setOpen(false); else show(); }} />
  </PopoverNext>;
}
