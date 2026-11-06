// No fetch handler or cache: session data stays authenticated and live. Web Push wakes this worker even when the
// installed app is backgrounded; its encrypted payload contains only the event kind and the session id.
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", event => event.waitUntil(self.clients.claim()));

const kinds = {
  completed: ["Turn complete", "A session has finished its turn."],
  failed: ["Turn failed", "A session's turn ended with an error."],
  answer: ["Your answer is needed", "A session is waiting for your answer or review."],
  approval: ["Your approval is needed", "A session is waiting for your permission."],
};

function sessionId(value) { return typeof value === "string" && /^[a-zA-Z0-9._-]{1,160}$/.test(value) ? value : null; }

self.addEventListener("push", event => {
  let data;
  try { data = event.data?.json(); } catch { return; }
  const id = sessionId(data?.sessionId), text = Object.hasOwn(kinds, data?.kind) ? kinds[data.kind] : null;
  if (!id || !text) return;
  event.waitUntil(self.registration.showNotification(text[0], {
    body: text[1], icon: "/mobile/icon-192.png", badge: "/mobile/icon-192.png",
    tag: `alta-session-${id}`, data: { sessionId: id },
  }));
});

self.addEventListener("notificationclick", event => {
  event.notification.close();
  const id = sessionId(event.notification.data?.sessionId);
  if (!id) return;
  const url = new URL(`/#/session/${encodeURIComponent(id)}`, self.location.origin);
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
