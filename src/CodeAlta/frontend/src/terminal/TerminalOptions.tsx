import { useEffect, useId, useState } from "react";
import { HTMLSelect, NumericInput, SegmentedControl, Switch } from "@blueprintjs/core";
import { useShellLanguage } from "../shellLanguage";
import { maximumTerminalFontSize, minimumTerminalFontSize, terminalCursors, terminalScrollbacks, type TerminalCursor, type TerminalLook } from "./terminalLook";

const cursorLabels: Readonly<Record<TerminalCursor, "Bar" | "Block" | "Underline">> = { bar: "Bar", block: "Block", underline: "Underline" };

/**
 * How the terminals of the window look and behave: each preference on a row, with the control that fits it.
 * A change applies at once to every terminal of the window, and is kept for the next ones.
 */
export function TerminalOptions({ look, onLook, shells = [] }: {
  look: TerminalLook; onLook: (look: TerminalLook) => void;
  /** The shells the host found on this system, the default one first. */
  shells?: readonly Readonly<{ id: string; name: string }>[];
}) {
  const { t, locale } = useShellLanguage();
  const id = useId();
  // The shell that was chosen while it is still one of the system; the default one otherwise.
  const shell = shells.find(candidate => candidate.id === look.shell)?.id ?? shells[0]?.id ?? "";
  // What is typed in the size, which is a size only once it is a whole number the terminals take.
  const [size, setSize] = useState(String(look.fontSize));
  useEffect(() => { setSize(String(look.fontSize)); }, [look.fontSize]);
  return <div className="terminal-options" role="group" aria-label={t("Terminal options")}>
    <div className="terminal-option">
      <span id={`${id}-cursor`}>{t("Cursor")}</span>
      <SegmentedControl size="small" aria-labelledby={`${id}-cursor`} value={look.cursorStyle}
        options={terminalCursors.map(value => ({ label: t(cursorLabels[value]), value }))}
        onValueChange={value => onLook({ ...look, cursorStyle: value as TerminalCursor })} />
    </div>
    <div className="terminal-option">
      <label htmlFor={`${id}-blink`}>{t("Blink cursor")}</label>
      <Switch alignIndicator="end" id={`${id}-blink`} checked={look.cursorBlink} onChange={event => onLook({ ...look, cursorBlink: event.currentTarget.checked })} />
    </div>
    <div className="terminal-option">
      <label htmlFor={`${id}-size`}>{t("Text size")}</label>
      <NumericInput id={`${id}-size`} className="terminal-option-size" size="small" value={size} min={minimumTerminalFontSize} max={maximumTerminalFontSize}
        stepSize={1} majorStepSize={2} minorStepSize={null} allowNumericCharactersOnly selectAllOnFocus
        onValueChange={(value, text) => {
          setSize(text);
          if (Number.isInteger(value) && value >= minimumTerminalFontSize && value <= maximumTerminalFontSize && value !== look.fontSize) onLook({ ...look, fontSize: value });
        }}
        onBlur={() => setSize(String(look.fontSize))} />
    </div>
    <div className="terminal-option">
      <label htmlFor={`${id}-select`}>{t("Copy on select")}</label>
      <Switch alignIndicator="end" id={`${id}-select`} checked={look.copyOnSelect} onChange={event => onLook({ ...look, copyOnSelect: event.currentTarget.checked })} />
    </div>
    <div className="terminal-option">
      <label htmlFor={`${id}-scrollback`}>{t("Scrollback")}</label>
      <HTMLSelect id={`${id}-scrollback`} value={look.scrollback} onChange={event => onLook({ ...look, scrollback: Number(event.currentTarget.value) })}
        options={terminalScrollbacks.map(lines => ({ value: lines, label: t("{count} lines", { count: lines.toLocaleString(locale) }) }))} />
    </div>
    {shells.length > 1 && <div className="terminal-option" title={t("Applies to new terminals")}>
      <label htmlFor={`${id}-shell`}>{t("Shell")}</label>
      <HTMLSelect id={`${id}-shell`} value={shell} options={shells.map(candidate => ({ value: candidate.id, label: candidate.name }))}
        onChange={event => onLook({ ...look, shell: event.currentTarget.value === shells[0].id ? null : event.currentTarget.value })} />
    </div>}
    <div className="terminal-option" title={t("Applies to new terminals")}>
      <label htmlFor={`${id}-integration`}>{t("Shell integration")}</label>
      <Switch alignIndicator="end" id={`${id}-integration`} checked={look.shellIntegration} onChange={event => onLook({ ...look, shellIntegration: event.currentTarget.checked })} />
    </div>
  </div>;
}
