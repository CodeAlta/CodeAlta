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

  // The document is the story: every chapter, link and image works without JavaScript.
  // A decorative sticky stage mirrors its pictures on roomy screens. Never intercept scrolling,
  // move focus, hide chapter text, or announce every scroll position to a screen reader.
  function scrollStory(story) {
    var steps = Array.from(story.querySelectorAll(".alta-story-step"));
    var links = Array.from(story.querySelectorAll(".alta-story-index a"));
    var shots = steps.map(function (step) { return step.querySelector(".alta-story-shot"); });
    if (!steps.length || links.length !== steps.length || shots.some(function (shot) { return !shot; })) return;
    var roomy = window.matchMedia("(min-width: 1000px) and (min-height: 700px) and (prefers-reduced-motion: no-preference)");
    var stage = document.createElement("div");
    stage.className = "alta-story-stage";
    stage.setAttribute("aria-hidden", "true");
    stage.hidden = true;
    var frames = shots.map(function (shot) {
      var frame = shot.cloneNode(true);
      frame.className = "alta-window alta-story-frame";
      frame.querySelector("img").alt = "";
      stage.appendChild(frame);
      return frame;
    });
    story.querySelector(".alta-story-layout").appendChild(stage);
    var selected = -1;
    var scheduled = false;
    function select(index) {
      if (index === selected) return;
      selected = index;
      steps.forEach(function (step, i) {
        var active = i === selected;
        step.classList.toggle("is-current", active);
        frames[i].classList.toggle("is-current", active);
        if (active) links[i].setAttribute("aria-current", "step");
        else links[i].removeAttribute("aria-current");
      });
    }
    function update() {
      scheduled = false;
      var index = 0;
      // Read geometry together, then write only when the chapter changes.
      steps.forEach(function (step, i) {
        if (step.getBoundingClientRect().top <= window.innerHeight * .5) index = i;
      });
      select(index);
    }
    function schedule() {
      if (scheduled) return;
      scheduled = true;
      window.requestAnimationFrame(update);
    }
    function layout() {
      story.classList.toggle("is-enhanced", roomy.matches);
      stage.hidden = !roomy.matches;
      schedule();
    }
    window.addEventListener("scroll", schedule, { passive: true });
    window.addEventListener("resize", schedule);
    window.addEventListener("pageshow", schedule);
    roomy.addEventListener("change", layout);
    layout();
  }

  function scrollBrand() {
    var logo = document.querySelector(".codealta-ascii-logo");
    var brand = document.querySelector("[data-alta-scroll-brand]");
    if (!logo || !brand || !window.IntersectionObserver) return;
    brand.hidden = logo.getBoundingClientRect().bottom > 0;
    new IntersectionObserver(function (entries) {
      brand.hidden = entries[0].boundingClientRect.bottom > 0;
    }).observe(logo);
  }

  function ready() {
    applyUi(root.getAttribute("data-alta-ui") === "tui" ? "tui" : "desktop");
    scrollBrand();
    document.querySelectorAll("[data-alta-story]").forEach(scrollStory);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", ready);
  } else {
    ready();
  }
})();
