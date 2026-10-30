import { useEffect, useRef, useState } from "react";
import { Button, SegmentedControl, Spinner } from "@blueprintjs/core";
import type { MathfieldElement } from "mathlive";
import { AppWindow } from "./AppWindow";
import { loadMath } from "./math";
import { useShellLanguage } from "./shellLanguage";

/** What the equation editor opens with: the LaTeX of a formula, and whether it is displayed on lines of its own. */
export type EquationDraft = Readonly<{ tex: string; display: boolean }>;

/**
 * Writes an equation visually with MathLive (`/` makes a fraction, `^` a superscript, `sqrt` a root…), with its LaTeX
 * kept in sync below, and gives the LaTeX back. This module and MathLive are loaded the first time the window opens.
 * MathLive draws with KaTeX's fonts, which the stylesheet of the window already declares: it loads none, and no sound.
 */
export default function EquationEditor({ draft, editing, onInsert, onClose }: {
  draft: EquationDraft; editing: boolean; onInsert: (equation: EquationDraft) => void; onClose: () => void;
}) {
  const { t } = useShellLanguage();
  const host = useRef<HTMLDivElement>(null);
  const field = useRef<MathfieldElement | null>(null);
  const [tex, setTex] = useState(draft.tex);
  const [display, setDisplay] = useState(draft.display);
  const [state, setState] = useState<"loading" | "ready" | "failed">("loading");
  useEffect(() => {
    let current = true;
    void Promise.all([loadMath(), import("mathlive")]).then(([, mathlive]) => {
      const container = host.current;
      if (!current || !container) return;
      // Before the first field exists: later changes are ignored.
      mathlive.MathfieldElement.fontsDirectory = null;
      mathlive.MathfieldElement.soundsDirectory = null;
      const element = new mathlive.MathfieldElement();
      element.smartFence = true;
      element.value = draft.tex;
      element.setAttribute("aria-label", t("Equation"));
      element.addEventListener("input", () => setTex(element.value));
      container.append(element);
      field.current = element;
      // The rest of the page is inert under the modal window: its virtual keyboard opens in it.
      const dialog = container.closest("dialog");
      if (dialog) window.mathVirtualKeyboard.container = dialog;
      setState("ready");
      element.focus();
    }, () => { if (current) setState("failed"); });
    return () => {
      current = false;
      window.mathVirtualKeyboard?.hide?.();
      if (window.mathVirtualKeyboard) window.mathVirtualKeyboard.container = null;
      field.current?.remove();
      field.current = null;
    };
  }, []);
  const insert = () => { const value = (field.current?.value ?? tex).trim(); if (value) onInsert({ tex: value, display }); };
  return <AppWindow storageKey="codealta.desktop.window.equation-editor.v1" className="equation-editor-dialog" titleId="equation-editor-title"
    title={t(editing ? "Edit equation" : "Insert equation")} closeLabel={t("Close")} keepOnOutsidePress
    preferredSize={viewport => ({ width: Math.min(760, viewport.width - 40), height: Math.min(460, viewport.height - 40) })}
    minimumSize={{ width: 420, height: 300 }} onClose={onClose} onCancel={event => { event.preventDefault(); onClose(); }}
    onKeyDown={event => {
      // The window never lets its keys reach the composer or the shortcuts of the window.
      event.stopPropagation();
      if (event.key === "Enter" && (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey) { event.preventDefault(); insert(); }
    }}>
    <div className="equation-editor">
      <div className="equation-editor-field" ref={host}>
        {state === "loading" && <Spinner size={24} aria-label={t("Loading the equation editor…")} />}
        {state === "failed" && <p className="equation-editor-failed">{t("The equation editor could not be loaded.")}</p>}
      </div>
      <label className="equation-editor-source">
        <span>{t("LaTeX")}</span>
        <textarea value={tex} spellCheck={false} rows={3} onChange={event => {
          setTex(event.target.value);
          if (field.current) field.current.value = event.target.value;
        }} />
      </label>
      <div className="equation-editor-actions">
        <SegmentedControl small value={display ? "display" : "inline"} onValueChange={value => setDisplay(value === "display")}
          options={[{ label: t("Inline"), value: "inline" }, { label: t("Display"), value: "display" }]} />
        <span className="equation-editor-hint">{t("Ctrl+Enter inserts")}</span>
        <Button onClick={onClose}>{t("Cancel")}</Button>
        <Button intent="primary" disabled={!tex.trim()} onClick={insert}>{t(editing ? "Replace" : "Insert")}</Button>
      </div>
    </div>
  </AppWindow>;
}
