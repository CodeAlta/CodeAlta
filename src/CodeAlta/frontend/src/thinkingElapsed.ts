import { useEffect, useState } from "react";

export function formatThinkingElapsed(seconds: number): string {
  const total = Math.max(0, Math.floor(seconds));
  return total < 60 ? `${total}s` : `${Math.floor(total / 60)}m ${total % 60}s`;
}

// Presentation only. This clock never authorizes a run operation.
export function useThinkingElapsed(busy: boolean): number {
  const [seconds, setSeconds] = useState(0);
  useEffect(() => {
    setSeconds(0);
    if (!busy) return;
    const started = performance.now();
    const timer = setInterval(() => setSeconds(Math.floor((performance.now() - started) / 1000)), 250);
    return () => clearInterval(timer);
  }, [busy]);
  return seconds;
}
