import { useEffect, useRef, useState } from "react";

export function formatThinkingElapsed(seconds: number): string {
  const total = Math.max(0, Math.floor(seconds));
  return total < 60 ? `${total}s` : `${Math.floor(total / 60)}m ${total % 60}s`;
}

// Presentation only. This clock never authorizes a run operation.
// It counts from the moment `busy` starts, and is read only while `shown`: the pane of a tab nobody sees has no
// timer, and shows the time that passed when it is seen again.
export function useThinkingElapsed(busy: boolean, shown = true): number {
  const [seconds, setSeconds] = useState(0);
  const started = useRef<number | null>(null);
  useEffect(() => {
    started.current = busy ? performance.now() : null;
    setSeconds(0);
  }, [busy]);
  useEffect(() => {
    if (!busy || !shown) return;
    const read = () => setSeconds(Math.floor((performance.now() - (started.current ?? performance.now())) / 1000));
    read();
    const timer = setInterval(read, 250);
    return () => clearInterval(timer);
  }, [busy, shown]);
  return seconds;
}
