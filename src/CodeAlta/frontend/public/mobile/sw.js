// The service worker an installable app needs, and nothing more: it caches nothing (the API is authenticated and
// live), and has no fetch handler, so every request goes to the network as without it.
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", event => event.waitUntil(self.clients.claim()));
