// Disposable page: the tab switcher with the keys of the window, over three tabs, as its test presses them.
import { StrictMode, useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { recentOrder, rememberRecent } from "./recentTabs";
import { TabSwitcher, useTabSwitcher } from "./TabSwitcher";

const tabs = ["one", "two", "three"];

function Window() {
  const [front, setFront] = useState("one");
  const recent = useRef<readonly string[]>([]);
  const switcher = useTabSwitcher(id => setFront(id));
  const step = useRef(switcher.step);
  step.current = switcher.step;
  const current = useRef(front);
  current.current = front;
  useEffect(() => { recent.current = rememberRecent(recent.current, front); }, [front]);
  useEffect(() => {
    const keyDown = (event: KeyboardEvent) => {
      if (event.key !== "Tab" || !event.ctrlKey) return;
      event.preventDefault();
      step.current(recentOrder(recent.current, tabs, current.current), event.shiftKey ? -1 : 1);
    };
    window.addEventListener("keydown", keyDown, true);
    return () => window.removeEventListener("keydown", keyDown, true);
  }, []);
  return <>
    <output id="front">{front}</output>
    {switcher.state && <TabSwitcher state={switcher.state} items={new Map(tabs.map(id => [id, { id, label: id, icon: null, preview: id === "three" }]))} />}
  </>;
}

createRoot(document.getElementById("root")!).render(<StrictMode><div className="ide-shell"><Window /></div></StrictMode>);
Object.assign(window, { tabSwitcherFixture: {} });
