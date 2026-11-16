// No fetch handler or cache: session data stays authenticated and live. Web Push wakes this worker even when the
// installed app is backgrounded; its encrypted payload contains only the event kind, the session id and, for a session of
// another machine, the id of that machine.
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", event => event.waitUntil(self.clients.claim()));

const kinds = {
  completed: ["Turn complete", "A session has finished its turn."],
  failed: ["Turn failed", "A session's turn ended with an error."],
  answer: ["Your answer is needed", "A session is waiting for your answer or review."],
  approval: ["Your approval is needed", "A session is waiting for your permission."],
};

function sessionId(value) { return typeof value === "string" && /^[a-zA-Z0-9._-]{1,160}$/.test(value) ? value : null; }
// The machine of a session: null for this computer (none named, or `local`), undefined for a value that is no machine id.
function machineId(value) {
  if (value === undefined || value === null || value === "local") return null;
  return typeof value === "string" && /^[a-zA-Z0-9._:~-]{1,200}$/.test(value) ? value : undefined;
}
// The address of a session in the page, as the page writes it (`sessionHash`).
function sessionAddress(machine, id) {
  return machine ? `/#/machine/${encodeURIComponent(machine)}/session/${encodeURIComponent(id)}` : `/#/session/${encodeURIComponent(id)}`;
}

self.addEventListener("push", event => {
  let data;
  try { data = event.data?.json(); } catch { return; }
  const id = sessionId(data?.sessionId), machine = machineId(data?.machineId), text = Object.hasOwn(kinds, data?.kind) ? kinds[data.kind] : null;
  // A machine that cannot be read is not guessed: the notification would open another session.
  if (!id || !text || machine === undefined) return;
  event.waitUntil(self.registration.showNotification(text[0], {
    body: text[1], icon: "/mobile/icon-192.png", badge: "/mobile/icon-192.png",
    // Two machines may hold the same id: each has its own notification.
    tag: machine ? `alta-session-${machine}-${id}` : `alta-session-${id}`, data: machine ? { sessionId: id, machineId: machine } : { sessionId: id },
  }));
});

self.addEventListener("notificationclick", event => {
  event.notification.close();
  const id = sessionId(event.notification.data?.sessionId), machine = machineId(event.notification.data?.machineId);
  if (!id || machine === undefined) return;
  const url = new URL(sessionAddress(machine, id), self.location.origin);
  event.waitUntil((async () => {
    const windows = await self.clients.matchAll({ type: "window", includeUncontrolled: true });
    for (const client of windows) {
      const current = new URL(client.url);
      if (current.origin !== url.origin || current.pathname !== "/") continue;
      try {
        const navigated = await client.navigate(url.href);
        if (navigated) { await navigated.focus(); return; }
      } catch { /* a closing window: open a new app window below */ }
    }
    await self.clients.openWindow(url.href);
  })());
});
