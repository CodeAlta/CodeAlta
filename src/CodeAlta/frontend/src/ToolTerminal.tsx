import { useEffect, useRef } from "react";
import { shellColor, subscribeAppearance } from "./shellColors";
import { OutputTerminal } from "./terminal/OutputTerminal";
import { loadTerminalFont } from "./terminal/TerminalSurface";
import { restoreTerminalLook, terminalLookKey } from "./terminal/terminalLook";
import { terminalTheme } from "./terminal/terminals";

// The colors of the window as it is now, like the terminals of the tabs.
function readTheme() {
  const root = document.documentElement;
  const probe = root.appendChild(document.createElement("span"));
  try { return terminalTheme(property => shellColor(probe, property), root.dataset.theme !== "light"); }
  finally { probe.remove(); }
}

function fontSize(): number {
  try { return restoreTerminalLook(() => localStorage.getItem(terminalLookKey)).fontSize; }
  catch { return 13; }
}

/**
 * What a command wrote, in a terminal that is only read. `text` is the newest part of the output `stream` and
 * `offset` its position in the whole output: text that continues what is shown is added, anything else (another
 * stream, a part that does not follow) replaces it.
 */
export function ToolTerminal({ stream, text, offset = 0 }: { stream: string; text: string; offset?: number }) {
  const host = useRef<HTMLDivElement>(null);
  const terminal = useRef<OutputTerminal | null>(null);
  const shown = useRef<{ stream: string; end: number } | null>(null);
  const latest = useRef({ stream, text, offset }); latest.current = { stream, text, offset };
  function show() {
    const target = terminal.current;
    if (!target) return;
    const value = latest.current, state = shown.current;
    if (!state || state.stream !== value.stream || state.end < value.offset || state.end > value.offset + value.text.length) {
      target.reset();
      target.write(value.text);
    } else target.write(value.text.slice(state.end - value.offset));
    shown.current = { stream: value.stream, end: value.offset + value.text.length };
  }
  useEffect(() => {
    const element = host.current!;
    let active = true;
    // A terminal measures its cells once, with the font it finds: it is created when the font is there.
    void loadTerminalFont().then(() => {
      if (!active) return;
      const created = new OutputTerminal({ fontSize: fontSize(), theme: readTheme(), mac: navigator.platform.startsWith("Mac"),
        copy: value => void navigator.clipboard?.writeText(value).catch(() => { /* The clipboard is not available: nothing is copied. */ }) });
      terminal.current = created;
      created.mount(element);
      show();
    });
    const observer = new ResizeObserver(() => terminal.current?.fit());
    observer.observe(element);
    const unsubscribe = subscribeAppearance(() => terminal.current?.setTheme(readTheme()));
    return () => {
      active = false;
      observer.disconnect();
      unsubscribe();
      terminal.current?.dispose();
      terminal.current = null;
      shown.current = null;
    };
  }, []);
  useEffect(show, [stream, text, offset]);
  return <div className="tool-terminal" ref={host} data-tool-terminal />;
}
