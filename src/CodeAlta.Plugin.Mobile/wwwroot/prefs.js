// Applied before the style sheet paints, so the page never flashes the other theme or size.
(function () {
  var d = document.documentElement, t = null, f = 2;
  try { t = localStorage.getItem("alta.theme"); f = +localStorage.getItem("alta.fontStep") || 2; } catch (e) { }
  if (t !== "light" && t !== "dark") t = matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
  d.dataset.theme = t;
  d.dataset.fontStep = String(f >= 1 && f <= 5 ? f : 2);
})();

// The token of the QR link comes in the fragment once (#k=…): it is kept for an installed app, and leaves the address.
(function () {
  var m = /^#k=([^&]+)/.exec(location.hash);
  if (!m) return;
  try { localStorage.setItem("alta.token", decodeURIComponent(m[1])); } catch (e) { }
  history.replaceState(null, "", location.pathname + location.search);
})();
