(function () {
  "use strict";

  var storageKey = "codealta-ui";
  var root = document.documentElement;

  // Desktop / TUI switch: one choice for every screenshot of the site.
  function applyUi(ui) {
    root.setAttribute("data-alta-ui", ui);
    var tabs = document.querySelectorAll("[data-alta-ui-set]");
    for (var i = 0; i < tabs.length; i++) {
      var active = tabs[i].getAttribute("data-alta-ui-set") === ui;
      tabs[i].classList.toggle("active", active);
      tabs[i].setAttribute("aria-pressed", active ? "true" : "false");
    }
  }

  document.addEventListener("click", function (event) {
    var tab = event.target.closest ? event.target.closest("[data-alta-ui-set]") : null;
    if (!tab) return;
    var ui = tab.getAttribute("data-alta-ui-set") === "tui" ? "tui" : "desktop";
    // The two versions do not have the same height: keep the clicked switch where it is.
    var before = tab.getBoundingClientRect().top;
    applyUi(ui);
    try { localStorage.setItem(storageKey, ui); } catch (_) { }
    window.scrollBy(0, tab.getBoundingClientRect().top - before);
  });

  function ready() {
    applyUi(root.getAttribute("data-alta-ui") === "tui" ? "tui" : "desktop");

    // Home page: slow color shift of the "Alta" half of the logo.
    var alta = document.querySelector(".logo-alta");
    if (!alta || window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;
    var start;
    function tick(timestamp) {
      if (start === undefined) start = timestamp;
      var phase = ((timestamp - start) / 5200) % 1;
      root.style.setProperty("--alta-logo-shift", (phase * 100).toFixed(2) + "%");
      window.requestAnimationFrame(tick);
    }
    window.requestAnimationFrame(tick);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", ready);
  } else {
    ready();
  }
})();
