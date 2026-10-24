// The theme and the text size of the phone page, applied before its style sheet paints, so the page never flashes the
// other theme or size. A classic script of the head: the page's policy allows no inline script. src/mobile/appearance.ts
// applies the same once the page runs.
(function () {
  var root = document.documentElement, theme = null, step = 2;
  try { theme = localStorage.getItem("alta.theme"); step = Number(localStorage.getItem("alta.fontStep")) || 2; } catch (e) { /* refused */ }
  if (theme !== "light" && theme !== "dark") theme = matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark";
  root.dataset.theme = theme;
  root.dataset.fontStep = String(step >= 1 && step <= 5 ? Math.floor(step) : 2);
  var meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute("content", theme === "light" ? "#ffffff" : "#252a31");
})();
