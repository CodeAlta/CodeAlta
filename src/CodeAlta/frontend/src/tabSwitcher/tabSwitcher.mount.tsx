// Disposable page: the tab switcher with the keys of the window, over three tabs, as its test presses them.
import { StrictMode, useEffect, useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { recentOrder, rememberRecent } from "./recentTabs";
import { isTabSwitcherKey, TabSwitcher, type TabSwitcherHandle } from "./TabSwitcher";

const tabs = Object.freeze(["one", "two", "three"]);

function Window() {
  const [front, setFront] = useState("one");
  const [selections, setSelections] = useState(0);
  const renders = useRef(0);
  renders.current++;
  const recent = useRef<readonly string[]>([]);
  const switcher = useRef<TabSwitcherHandle>(null);
  useLayoutEffect(() => { recent.current = rememberRecent(recent.current, front, tabs); }, [front]);
  useEffect(() => {
    const keyDown = (event: KeyboardEvent) => {
      if (!isTabSwitcherKey(event)) return;
      event.preventDefault();
      switcher.current?.step(event.shiftKey ? -1 : 1);
    };
    window.addEventListener("keydown", keyDown, true);
    return () => window.removeEventListener("keydown", keyDown, true);
  }, []);
  return <>
    <output id="front" data-renders={renders.current} data-selections={selections}>{front}</output>
    <button id="other">Other control</button>
    <TabSwitcher ref={switcher} current={front}
      items={() => recentOrder(recent.current, tabs, front).map(id => ({ id, label: id, icon: null, preview: id === "three" }))}
      show={id => { setFront(id); setSelections(count => count + 1); }} />
  </>;
}

createRoot(document.getElementById("root")!).render(<StrictMode><div className="ide-shell"><Window /></div></StrictMode>);
Object.assign(window, { tabSwitcherFixture: {} });
