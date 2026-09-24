// Test-owned browser fixture for the real timeline scroll hook. No catalog or native host is opened.
import { createElement, useEffect, useLayoutEffect, useState, type UIEvent } from "react";
import { createRoot } from "react-dom/client";
import { bottomScrollTop, createTimelineScrollMemory, useTimelinePosition } from "./timelineScroll";

const memory = createTimelineScrollMemory();
function MountedTimeline() {
  const position = useTimelinePosition("fixture-session", memory);
  const [loaded, setLoaded] = useState(false);
  useEffect(() => { const timer = setTimeout(() => setLoaded(true), 40); return () => clearTimeout(timer); }, []);
  useLayoutEffect(() => { if (loaded) position.settled(); }, [loaded]);
  useEffect(() => {
    if (!loaded) return;
    const container = position.elementRef.current!;
    const initial = container.scrollTop;
    const resize = setTimeout(() => {
      // Layout grows without child-list/character-data mutation, as with late visual sizing.
      document.getElementById("history-rows")!.style.height = "2200px";
    }, 80);
    const check = setTimeout(() => {
      const target = bottomScrollTop(container);
      const atLatest = { initial, top: container.scrollTop, target, following: memory.open("fixture-session").following() };
      container.scrollTop = 420;
      container.dispatchEvent(new Event("scroll", { bubbles: true }));
      setTimeout(() => { document.getElementById("history-rows")!.style.height = "2700px"; }, 60);
      setTimeout(() => {
        document.getElementById("result")!.textContent = JSON.stringify({ atLatest,
          readingTop: container.scrollTop, readingFollow: memory.open("fixture-session").following(),
          finalTarget: bottomScrollTop(container) });
      }, 300);
    }, 420);
    return () => { clearTimeout(resize); clearTimeout(check); };
  }, [loaded]);
  return createElement("div", null,
    createElement("div", { ref: position.elementRef, onScroll: (event: UIEvent<HTMLDivElement>) => position.scroll(event.currentTarget),
      style: { height: "200px", overflowY: "scroll" } },
    createElement("div", { id: "history-rows", style: { height: "1000px" } }, "First cards and latest user prompt")),
    createElement("output", { id: "result" }, "waiting"));
}
createRoot(document.getElementById("app")!).render(createElement(MountedTimeline));
