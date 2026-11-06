// CodeAlta agent overlay of a page: what shows the user that an agent acts in it.
//
// CodeAlta adds this script to the document of a tab in an isolated world of its own, only when an agent acts in a browser
// with a window. It draws over the page, in a closed shadow root and in the top layer, a cursor that moves to where the
// agent is about to act, a ring where it presses, a glow around the page and a small label that says an agent is using
// it. Nothing of it takes the pointer: every click goes to the page under it. The page sees neither its functions nor
// its content, and cannot make it show anything; it is hidden from assistive technology, and so from the snapshots of the
// agent, and CodeAlta hides it while it takes a picture of the page.
(function () {
  'use strict';

  const KEY = '__codealtaAgentOverlay';
  if (globalThis[KEY]) return;
  // The overlay is for the document of the tab, whose viewport the coordinates of the input are in.
  if (window !== window.top) return;

  const COLOR = '#7c3aed';
  // The longest wait for the cursor to arrive: an agent is never held longer by its own cursor.
  const ARRIVAL_LIMIT = 600;
  const APPEAR_MS = 140;
  const IDLE_DIM_MS = 1500;
  const RING_MS = 450;
  // A hidden overlay shows again by itself, should the host that hid it for a picture never say it is done.
  const HIDE_LIMIT = 5000;

  // The arrow of the cursor, its tip at the origin.
  const ARROW = 'M1 1 L1 18 L5.6 13.8 L8.7 20.6 L11.8 19.3 L8.8 12.6 L14.6 12.4 Z';

  const STYLE = `
    :host { all: initial; }
    .layer { position: fixed; inset: 0; width: 100vw; height: 100vh; margin: 0; padding: 0; border: 0; background: transparent;
      overflow: visible; pointer-events: none; user-select: none; color-scheme: normal; }
    .layer::backdrop { display: none; }
    .layer * { pointer-events: none !important; }
    .layer.off { visibility: hidden !important; }
    .glow { position: fixed; inset: 0; border-radius: 2px; opacity: 0; transition: opacity 220ms ease-out;
      box-shadow: inset 0 0 0 2px rgba(124,58,237,.85), inset 0 0 26px 6px rgba(124,58,237,.35); }
    .active .glow { opacity: 1; animation: breathe 2.4s ease-in-out infinite alternate; }
    @keyframes breathe { from { box-shadow: inset 0 0 0 2px rgba(124,58,237,.85), inset 0 0 18px 4px rgba(124,58,237,.28); }
      to { box-shadow: inset 0 0 0 2px rgba(124,58,237,.95), inset 0 0 32px 8px rgba(124,58,237,.42); } }
    .label { position: fixed; top: 8px; left: 50%; transform: translateX(-50%); padding: 3px 10px 3px 8px; border-radius: 999px;
      background: rgba(31,35,48,.88); color: #fff; font: 600 12px/1.4 system-ui, -apple-system, "Segoe UI", sans-serif;
      white-space: nowrap; box-shadow: 0 2px 10px rgba(0,0,0,.25); opacity: 0; transition: opacity 220ms ease-out; }
    .label .dot { display: inline-block; width: 7px; height: 7px; border-radius: 50%; background: ${COLOR}; margin-right: 6px;
      vertical-align: 1px; box-shadow: 0 0 0 2px rgba(124,58,237,.35); }
    .active .label { opacity: 1; }
    .active .label.aside { opacity: .15; }
    .cursor { position: fixed; left: 0; top: 0; width: 0; height: 0; opacity: 0; transition: opacity 300ms ease-out; will-change: transform; }
    .cursor svg { position: absolute; left: -1px; top: -1px; overflow: visible; filter: drop-shadow(0 1px 2px rgba(0,0,0,.35)); }
    .cursor.shown { opacity: 1; }
    .cursor.shown.idle { opacity: .5; }
    .ring { position: fixed; left: -14px; top: -14px; width: 28px; height: 28px; border-radius: 50%; border: 2px solid ${COLOR};
      background: rgba(124,58,237,.12); opacity: 0; }
    @media (prefers-reduced-motion: reduce) {
      .active .glow { animation: none; }
      .glow, .label, .cursor { transition: none; }
    }
  `;

  let host = null;
  let layer = null;
  let glow = null;
  let label = null;
  let cursor = null;
  let point = null;
  let motion = null;
  let activeUntil = 0;
  let endTimer = 0;
  let idleTimer = 0;
  let hidden = 0;
  let hideTimer = 0;

  function element(tag, className) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    return node;
  }

  function reducedMotion() {
    try { return matchMedia('(prefers-reduced-motion: reduce)').matches; } catch (_) { return false; }
  }

  // Only a cursor the user can see is waited for: a tab in the background, or a window minimized, draws nothing.
  function watched() {
    return document.visibilityState === 'visible' && !reducedMotion() && hidden === 0;
  }

  function build() {
    if (host && host.isConnected) { raise(); return; }
    if (!host) {
      // A plain element, never a custom one, whose shadow root is closed: the page can see that it is there and remove
      // it, but can neither read nor change what it shows.
      host = document.createElement('div');
      host.setAttribute('data-codealta-agent-overlay', '');
      // Hidden from assistive technology, so from the snapshots the agent reads; the content is in the shadow root anyway.
      host.setAttribute('aria-hidden', 'true');
      host.setAttribute('role', 'none');
      host.style.cssText = 'all: initial !important; display: block !important; position: fixed !important; left: 0 !important; top: 0 !important;'
        + ' width: 0 !important; height: 0 !important; pointer-events: none !important; visibility: visible !important;';
      const root = host.attachShadow({ mode: 'closed' });
      const style = element('style');
      style.textContent = STYLE;
      root.appendChild(style);
      // The layer is a popover inside the shadow root: in the top layer, above the dialogs of the page, and out of reach of
      // the styles of the page, its ::backdrop included.
      layer = element('div', 'layer');
      layer.setAttribute('popover', 'manual');
      glow = element('div', 'glow');
      label = element('div', 'label');
      label.appendChild(element('span', 'dot'));
      label.appendChild(document.createTextNode('CodeAlta agent is using this page'));
      cursor = element('div', 'cursor');
      cursor.innerHTML = '<svg width="16" height="22" viewBox="0 0 16 22" aria-hidden="true" focusable="false"><path d="' + ARROW
        + '" fill="' + COLOR + '" stroke="#fff" stroke-width="1.5" stroke-linejoin="round"/></svg>';
      layer.appendChild(glow);
      layer.appendChild(label);
      layer.appendChild(cursor);
      root.appendChild(layer);
      // The label steps aside while the user's own pointer is near it, so that it never hides what they look at.
      document.addEventListener('pointermove', event => {
        if (!event.isTrusted || !label) return;
        const box = label.getBoundingClientRect();
        const near = event.clientX > box.left - 24 && event.clientX < box.right + 24 && event.clientY > box.top - 24 && event.clientY < box.bottom + 24;
        label.classList.toggle('aside', near);
      }, { capture: true, passive: true });
    }
    (document.documentElement || document).appendChild(host);
    raise();
  }

  // Shows the layer last in the top layer, above what the page put there since, as a dialog it opened. A layer already
  // open stays as it is while the page has nothing there: showing it again would restart its glow at each step.
  function raise() {
    try {
      if (layer.matches(':popover-open')) {
        if (!document.querySelector(':modal, :popover-open, :fullscreen')) return;
        layer.hidePopover();
      }
      layer.showPopover();
    } catch (_) {
      // A document without the popover API: the layer stays in the document, above the page as far as it can be.
      layer.style.zIndex = '2147483647';
    }
  }

  function place(x, y) {
    point = { x: x, y: y };
    cursor.style.transform = 'translate(' + x + 'px, ' + y + 'px)';
  }

  function stopMotion() {
    if (motion) {
      const current = motion;
      motion = null;
      current.animation.cancel();
      current.done(false);
    }
  }

  function markActive() {
    clearTimeout(idleTimer);
    cursor.classList.remove('idle');
  }

  function rest() {
    clearTimeout(idleTimer);
    idleTimer = setTimeout(() => cursor && cursor.classList.add('idle'), IDLE_DIM_MS);
  }

  // The agent acts: the glow and the label show until it has not acted for a while.
  function activity(duration) {
    build();
    const span = Math.max(1000, Number(duration) || 15000);
    activeUntil = Date.now() + span;
    layer.classList.add('active');
    clearTimeout(endTimer);
    endTimer = setTimeout(end, span);
    return true;
  }

  function end() {
    clearTimeout(endTimer);
    activeUntil = 0;
    if (!layer) return true;
    stopMotion();
    layer.classList.remove('active');
    cursor.classList.remove('shown', 'idle');
    point = null;
    return true;
  }

  // Moves the cursor to a point of the viewport. With wait, the promise resolves once the user saw it arrive, or at the
  // limit; drag steps and a cursor already there resolve at once.
  function move(x, y, options) {
    x = Number(x); y = Number(y);
    if (!isFinite(x) || !isFinite(y)) return Promise.resolve(false);
    const wait = !!(options && options.wait);
    activity(options && options.duration);
    stopMotion();
    markActive();
    const appear = !point || !cursor.classList.contains('shown');
    const from = point;
    const distance = from ? Math.hypot(x - from.x, y - from.y) : 0;
    place(x, y);
    cursor.classList.add('shown');
    if (!wait || !watched() || (!appear && distance < 1)) { rest(); return Promise.resolve(true); }

    let animation;
    let duration;
    if (appear) {
      // The cursor shows where the agent acts, rather than flying in from a place the user no longer remembers.
      duration = APPEAR_MS;
      animation = cursor.animate([{ opacity: 0, transform: 'translate(' + x + 'px, ' + y + 'px) scale(.6)' }, { opacity: 1, transform: 'translate(' + x + 'px, ' + y + 'px) scale(1)' }],
        { duration: duration, easing: 'ease-out' });
    } else {
      duration = Math.min(480, Math.max(160, 140 + distance * 0.45));
      animation = cursor.animate([{ transform: 'translate(' + from.x + 'px, ' + from.y + 'px)' }, { transform: 'translate(' + x + 'px, ' + y + 'px)' }],
        { duration: duration, easing: 'cubic-bezier(.3, .7, .25, 1)' });
    }

    return new Promise(resolve => {
      let settled = false;
      const done = arrived => {
        if (settled) return;
        settled = true;
        clearTimeout(limit);
        if (motion && motion.animation === animation) motion = null;
        rest();
        resolve(arrived);
      };
      const limit = setTimeout(() => done(false), Math.min(ARRIVAL_LIMIT, duration + 120));
      motion = { animation: animation, done: done };
      animation.finished.then(() => done(true), () => done(false));
    });
  }

  // A ring out of the tip of the cursor, once per press; it never holds the press.
  function press() {
    if (!layer || !point) return false;
    markActive();
    const ring = element('div', 'ring');
    const at = 'translate(' + point.x + 'px, ' + point.y + 'px)';
    ring.style.transform = at;
    layer.appendChild(ring);
    const frames = reducedMotion()
      ? [{ opacity: 0.7 }, { opacity: 0 }]
      : [{ opacity: 0.7, transform: at + ' scale(.35)' }, { opacity: 0, transform: at + ' scale(1.6)' }];
    const animation = ring.animate(frames, { duration: RING_MS, easing: 'cubic-bezier(.2, .7, .3, 1)' });
    const remove = () => ring.remove();
    animation.onfinish = remove;
    animation.oncancel = remove;
    // A tab in the background runs no animation: the ring goes anyway.
    setTimeout(remove, RING_MS + 200);
    rest();
    return true;
  }

  // Hides the overlay while CodeAlta takes a picture of the page, and shows it again after.
  function hide() {
    hidden++;
    if (layer) layer.classList.add('off');
    clearTimeout(hideTimer);
    hideTimer = setTimeout(() => { hidden = 0; if (layer) layer.classList.remove('off'); }, HIDE_LIMIT);
    return true;
  }

  function show() {
    hidden = Math.max(0, hidden - 1);
    if (hidden === 0) {
      clearTimeout(hideTimer);
      if (layer) layer.classList.remove('off');
    }
    return true;
  }

  globalThis[KEY] = {
    activity: activity,
    end: end,
    move: move,
    press: press,
    hide: hide,
    show: show,
    // What the overlay shows, for the tests of CodeAlta.
    state() {
      const style = cursor ? getComputedStyle(cursor) : null;
      return {
        present: !!(host && host.isConnected),
        topLayer: !!(layer && layer.matches(':popover-open')),
        active: !!(layer && layer.classList.contains('active')) && Date.now() < activeUntil,
        hidden: hidden > 0,
        cursor: point && cursor.classList.contains('shown') ? { x: point.x, y: point.y, idle: cursor.classList.contains('idle'), opacity: style ? Number(style.opacity) : 0 } : null,
        label: label ? label.textContent : '',
        reducedMotion: reducedMotion(),
      };
    },
  };
})();
//# sourceURL=codealta-agent-overlay.js
