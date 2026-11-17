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

  // macOS / Linux / Windows tabs of the install command: the script of the head chose the visitor's system.
  var systems = ["macos", "linux", "windows"];
  function applyOs(os) {
    root.setAttribute("data-alta-os", os);
    var tabs = document.querySelectorAll("[data-alta-os-set]");
    for (var i = 0; i < tabs.length; i++) {
      var active = tabs[i].getAttribute("data-alta-os-set") === os;
      tabs[i].classList.toggle("active", active);
      tabs[i].setAttribute("aria-pressed", active ? "true" : "false");
    }
  }

  document.addEventListener("click", function (event) {
    var tab = event.target.closest ? event.target.closest("[data-alta-os-set]") : null;
    if (tab && systems.indexOf(tab.getAttribute("data-alta-os-set")) >= 0) applyOs(tab.getAttribute("data-alta-os-set"));
  });

  // Copy button of an install command: the text of the command beside it, then a check mark that says it is done.
  function copyText(text) {
    if (navigator.clipboard && navigator.clipboard.writeText) return navigator.clipboard.writeText(text);
    // A page not served over HTTPS has no clipboard API: a selection copied by the browser does the same.
    return new Promise(function (resolve, reject) {
      var area = document.createElement("textarea");
      area.value = text;
      area.setAttribute("readonly", "");
      area.style.position = "fixed";
      area.style.opacity = "0";
      document.body.appendChild(area);
      area.select();
      var done = false;
      try { done = document.execCommand("copy"); } catch (_) { }
      document.body.removeChild(area);
      if (done) resolve(); else reject(new Error("copy"));
    });
  }

  document.addEventListener("click", function (event) {
    var button = event.target.closest ? event.target.closest("[data-alta-copy]") : null;
    if (!button) return;
    var command = button.closest(".alta-command");
    var code = command ? command.querySelector("code") : null;
    if (!code) return;
    var icon = button.querySelector("i");
    // The button shows an icon only: its name says what happened.
    function name(title, label) {
      button.title = title;
      button.setAttribute("aria-label", label);
    }
    copyText(code.textContent.trim()).then(function () {
      button.classList.add("copied");
      name("Copied", "Copied");
      if (icon) icon.className = "bi bi-check2";
      clearTimeout(button.altaCopied);
      button.altaCopied = setTimeout(function () {
        button.classList.remove("copied");
        name("Copy", "Copy the command");
        if (icon) icon.className = "bi bi-clipboard";
      }, 1600);
    }, function () {
      name("Select the command and copy it", "Select the command and copy it");
    });
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
      var picture = frame.querySelector("img");
      if (picture) picture.alt = "";
      // A video of the stage is a moving picture: no controls, and it plays only while its chapter is the current one.
      var video = frame.querySelector("video");
      if (video) {
        video.removeAttribute("controls");
        video.removeAttribute("aria-label");
        video.preload = "none";
        video.tabIndex = -1;
      }
      stage.appendChild(frame);
      return frame;
    });
    story.querySelector(".alta-story-layout").appendChild(stage);
    balanceIndex(story, story.querySelector(".alta-story-index"), links);
    var layoutBox = story.querySelector(".alta-story-chapters");
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
      playFrames();
    }
    function playFrames() {
      frames.forEach(function (frame, i) {
        var video = frame.querySelector("video");
        if (!video) return;
        if (i === selected && roomy.matches) {
          video.preload = "auto";
          var playing = video.play();
          if (playing && playing.catch) playing.catch(function () { });
        } else if (!video.paused) {
          video.pause();
        }
      });
    }
    function update() {
      scheduled = false;
      var index = 0;
      // Read geometry together, then write only when the chapter changes.
      steps.forEach(function (step, i) {
        if (step.getBoundingClientRect().top <= window.innerHeight * .5) index = i;
      });
      var bounds = layoutBox.getBoundingClientRect();
      // Full once the middle of the last chapter reaches the middle of the window.
      var span = bounds.height - steps[steps.length - 1].offsetHeight * .5;
      select(index);
      if (!roomy.matches) return;
      // A decorative progress line follows the reading position through the chapters.
      var progress = (window.innerHeight * .5 - bounds.top) / Math.max(1, span);
      story.style.setProperty("--alta-story-progress", Math.min(1, Math.max(0, progress)).toFixed(4));
    }
    function schedule() {
      if (scheduled) return;
      scheduled = true;
      window.requestAnimationFrame(update);
    }
    function layout() {
      story.classList.toggle("is-enhanced", roomy.matches);
      stage.hidden = !roomy.matches;
      // The chapter's own video is hidden while the stage shows its copy.
      if (roomy.matches) shots.forEach(function (shot) { var video = shot.querySelector("video"); if (video && !video.paused) video.pause(); });
      playFrames();
      schedule();
    }
    window.addEventListener("scroll", schedule, { passive: true });
    window.addEventListener("resize", schedule);
    window.addEventListener("pageshow", schedule);
    roomy.addEventListener("change", layout);
    layout();
  }

  // The chapter index takes as few rows as its widest chapter allows, with the same number of chapters
  // on each row, so a new chapter never leaves one alone on a row. The stage below follows its height.
  function balanceIndex(story, index, links) {
    var range = document.createRange();
    function layout() {
      var style = getComputedStyle(index);
      var width = index.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
      var gap = parseFloat(style.columnGap) || 0;
      var widest = 0;
      links.forEach(function (link) {
        var linkStyle = getComputedStyle(link);
        range.selectNodeContents(link);
        var outer = range.getBoundingClientRect().width + parseFloat(linkStyle.paddingLeft) + parseFloat(linkStyle.paddingRight) + 2;
        widest = Math.max(widest, Math.ceil(outer));
      });
      var columns = Math.min(links.length, Math.max(1, Math.floor((width + gap) / (widest + gap))));
      var rows = Math.ceil(links.length / columns);
      story.style.setProperty("--alta-story-columns", String(Math.ceil(links.length / rows)));
      story.style.setProperty("--alta-story-index-height", index.offsetHeight + "px");
    }
    if (window.ResizeObserver) new ResizeObserver(layout).observe(index);
    if (document.fonts) document.fonts.ready.then(layout);
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
    applyOs(systems.indexOf(root.getAttribute("data-alta-os")) >= 0 ? root.getAttribute("data-alta-os") : "linux");
    scrollBrand();
    document.querySelectorAll("[data-alta-story]").forEach(scrollStory);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", ready);
  } else {
    ready();
  }
})();
