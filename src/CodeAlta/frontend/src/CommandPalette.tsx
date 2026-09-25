import { useLayoutEffect, useRef, useState } from "react";
import { paletteAvailable, paletteCommands, type PaletteAction, type PaletteContext } from "./paletteActions";

export function CommandPalette({ context, captured, onChoose, onClose }: {
  context: PaletteContext;
  captured: PaletteContext;
  onChoose: (action: PaletteAction) => void;
  onClose: () => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const results = useRef<HTMLDivElement>(null);
  const composingEscape = useRef(false);
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const words = query.trim().toLowerCase().split(/\s+/);
  const matches = paletteCommands(captured).filter(command => paletteAvailable(command.id, captured, context) &&
    words.every(word => command.label.toLowerCase().includes(word)));
  const index = Math.min(active, matches.length - 1);
  useLayoutEffect(() => {
    const element = dialog.current;
    element?.showModal();
    return () => { if (element?.open) element.close(); };
  }, []);
  useLayoutEffect(() => {
    const list = results.current;
    const option = list?.querySelector<HTMLElement>('[role="option"][aria-selected="true"]');
    if (!list || !option) return;
    // Keep keyboard selection visible inside the results, without scrolling the dialog or workspace.
    const viewport = list.getBoundingClientRect();
    const selected = option.getBoundingClientRect();
    if (selected.top < viewport.top) list.scrollTop += selected.top - viewport.top;
    else if (selected.bottom > viewport.bottom) list.scrollTop += selected.bottom - viewport.bottom;
  });
  return <dialog ref={dialog} className="app-dialog command-palette" aria-modal="true" aria-labelledby="palette-title"
    onCancel={event => { event.preventDefault(); if (!composingEscape.current) onClose(); }}
    onKeyUp={() => { composingEscape.current = false; }} onCompositionEnd={() => { composingEscape.current = false; }}
    onKeyDown={event => {
      event.stopPropagation();
      if (event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229) {
        if (event.key === "Escape") composingEscape.current = true;
        return;
      }
      if (event.key === "Escape") { event.preventDefault(); onClose(); }
      else if ((event.target as HTMLElement).matches("#palette-search, #palette-results [role='option']") &&
        (event.key === "ArrowDown" || event.key === "ArrowUp")) {
        event.preventDefault();
        if (matches.length) setActive((index + (event.key === "ArrowDown" ? 1 : matches.length - 1)) % matches.length);
      } else if ((event.target as HTMLElement).matches("#palette-search") && event.key === "Enter" &&
        !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey) {
        event.preventDefault();
        if (index >= 0) onChoose(matches[index].id);
      }
    }}>
    <header><div><span className="eyebrow">Implemented actions</span><h2 id="palette-title">Command palette</h2></div>
      <button type="button" className="icon-button" aria-label="Close command palette" onClick={onClose}>✕</button></header>
    <label htmlFor="palette-search">Search commands</label>
    <input autoFocus id="palette-search" type="search" role="combobox" aria-autocomplete="list" aria-expanded={matches.length > 0}
      aria-controls="palette-results" aria-activedescendant={index >= 0 ? `palette-option-${matches[index].id}` : undefined}
      value={query} onChange={event => { setQuery(event.target.value); setActive(0); }} />
    <div ref={results} id="palette-results" className="dialog-list" role="listbox" aria-label="Available commands">
      {matches.map((command, i) => <button type="button" role="option" id={`palette-option-${command.id}`} key={command.id}
        aria-selected={i === index} tabIndex={-1} onMouseEnter={() => setActive(i)} onClick={() => onChoose(command.id)}>{command.label}</button>)}
      {!matches.length && <p role="status">No available commands match.</p>}
    </div>
  </dialog>;
}
