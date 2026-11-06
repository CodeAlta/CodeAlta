// CodeAlta toolbar of a page: a bar of tools, of which Annotate is the first.
//
// CodeAlta adds this script to the documents of a session's tabs in an isolated world of its own: it sees the DOM of
// the page, and the page sees neither its functions nor the binding it reports through. It stays idle until the host
// enables it for a page of the session's project. It then shows the CodeAlta bar at the bottom of the page, in the
// dark or light look the user chose there: a bar of tools, of which Annotate is the first. In its Annotate mode, the
// element under the pointer is outlined, and the clicks and drags that choose are kept from the page: a click opens a
// popup where the user writes a note, Shift+click gathers several elements for one note, and a drag draws an area. A
// saved note goes to the host, which numbers it; the numbered marks stay until the host clears them, when the prompt
// that carries them is sent, from the window or from the Send of the bar.
(function () {
  'use strict';

  const KEY = '__codealtaToolbar';
  const BINDING = '__codealtaToolbarMessage';
  if (globalThis[KEY]) return;
  // The toolbar is for the document of the tab, not for its frames.
  if (window !== window.top) return;

  const MAX_HTML = 600;
  const MAX_TEXT = 300;
  const MARK_COLOR = '#7c3aed';

  function squash(text) {
    return String(text == null ? '' : text).replace(/\s+/g, ' ').trim();
  }

  function clip(text, limit) {
    text = String(text);
    return text.length > limit ? text.slice(0, limit) + '…' : text;
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Messages to the host
  // ---------------------------------------------------------------------------------------------------------------

  const queue = [];
  let flushing = 0;
  function send(message) {
    queue.push(JSON.stringify(message));
    flush();
  }

  function flush() {
    const binding = globalThis[BINDING];
    if (typeof binding !== 'function') {
      // The binding comes with the world; wait a moment for it rather than lose a message.
      if (!flushing && queue.length) flushing = setTimeout(() => { flushing = 0; flush(); }, 50);
      return;
    }

    while (queue.length) {
      try { binding(queue.shift()); } catch (_) { /* the host is gone */ }
    }
  }

  // ---------------------------------------------------------------------------------------------------------------
  // What an element is
  // ---------------------------------------------------------------------------------------------------------------

  const IMPLICIT_ROLES = {
    a: 'link', button: 'button', h1: 'heading', h2: 'heading', h3: 'heading', h4: 'heading', h5: 'heading', h6: 'heading',
    img: 'img', nav: 'navigation', main: 'main', header: 'banner', footer: 'contentinfo', ul: 'list', ol: 'list',
    li: 'listitem', select: 'combobox', textarea: 'textbox', table: 'table', form: 'form', dialog: 'dialog', section: 'region',
  };

  function roleOf(element) {
    const explicit = element.getAttribute('role');
    if (explicit) return explicit.split(' ')[0];
    if (element.localName === 'input') {
      const type = (element.getAttribute('type') || 'text').toLowerCase();
      return type === 'checkbox' ? 'checkbox' : type === 'radio' ? 'radio' : type === 'button' || type === 'submit' || type === 'reset' ? 'button' : 'textbox';
    }
    return IMPLICIT_ROLES[element.localName] || '';
  }

  function nameOf(element) {
    const label = element.getAttribute('aria-label');
    if (label) return squash(label);
    const labelledBy = element.getAttribute('aria-labelledby');
    if (labelledBy) {
      const text = labelledBy.split(/\s+/).map(id => document.getElementById(id)).filter(Boolean).map(node => squash(node.textContent)).join(' ');
      if (text) return text;
    }
    for (const attribute of ['alt', 'title', 'placeholder']) {
      const value = element.getAttribute(attribute);
      if (value) return squash(value);
    }
    return clip(squash(element.innerText || element.textContent), 80);
  }

  function short(element) {
    let text = element.localName;
    if (element.id) text += '#' + element.id;
    const classes = [...element.classList].slice(0, 2);
    if (classes.length) text += '.' + classes.join('.');
    return text;
  }

  function unique(selector) {
    try { return document.querySelectorAll(selector).length === 1; } catch (_) { return false; }
  }

  function escape(value) {
    return typeof CSS !== 'undefined' && CSS.escape ? CSS.escape(value) : String(value).replace(/[^a-zA-Z0-9_-]/g, '\\$&');
  }

  // A selector that finds the element again after a reload: its id where it has one, else a short path of tags,
  // classes and positions among siblings of the same tag.
  function selectorOf(element) {
    if (element.id && unique('#' + escape(element.id))) return '#' + escape(element.id);
    const parts = [];
    let node = element;
    while (node && node.nodeType === 1 && node !== document.documentElement && parts.length < 8) {
      let part = node.localName;
      if (node.id) {
        part += '#' + escape(node.id);
        parts.unshift(part);
        if (unique(parts.join(' > '))) return parts.join(' > ');
        node = node.parentElement;
        continue;
      }
      const classes = [...node.classList].filter(name => !/^(css-|sc-|jsx-|svelte-|_)/.test(name)).slice(0, 2);
      if (classes.length) part += classes.map(name => '.' + escape(name)).join('');
      const parent = node.parentElement;
      if (parent) {
        const same = [...parent.children].filter(child => child.localName === node.localName);
        if (same.length > 1) part += ':nth-of-type(' + (same.indexOf(node) + 1) + ')';
      }
      parts.unshift(part);
      if (unique(parts.join(' > '))) return parts.join(' > ');
      node = parent;
    }
    return parts.join(' > ');
  }

  function htmlOf(element) {
    const html = element.outerHTML || '';
    if (html.length <= MAX_HTML) return html;
    const open = html.slice(0, html.indexOf('>') + 1);
    return clip(open, MAX_HTML) + '…</' + element.localName + '>';
  }

  function describe(element) {
    const rect = element.getBoundingClientRect();
    const ancestors = [];
    for (let node = element.parentElement; node && node !== document.body && node !== document.documentElement && ancestors.length < 6; node = node.parentElement) {
      ancestors.unshift(short(node));
    }
    return {
      tag: element.localName,
      id: element.id || '',
      classes: [...element.classList],
      role: roleOf(element),
      name: nameOf(element),
      text: clip(squash(element.innerText || element.textContent), MAX_TEXT),
      html: htmlOf(element),
      selector: selectorOf(element),
      ancestors: ancestors,
      rect: { x: rect.left, y: rect.top, width: rect.width, height: rect.height },
      scroll: { x: window.scrollX, y: window.scrollY },
      viewport: { width: window.innerWidth, height: window.innerHeight },
    };
  }

  // ---------------------------------------------------------------------------------------------------------------
  // The toolbar, the outline, the popups, the list and the marks, in a closed shadow root
  //
  // The toolbar is the CodeAlta bar of the page: its tools come first, each a group of controls (Annotate is the first
  // one), and the settings of the bar itself (its look) close it. Everything takes its colors from the variables of the
  // theme the user chose, dark or light; the marks keep the color of CodeAlta in both.
  //
  // An annotation is one note on a part of the page: one element clicked, several elements chosen with Shift+click, or
  // an area drawn with the pointer (with the elements inside it). Until the user sends them, the list of the bar shows
  // the annotations of the session, which they can change or remove there or from the number on their mark.
  // ---------------------------------------------------------------------------------------------------------------

  const THEMES = {
    dark: {
      surface: '#1f2330', text: '#e9ebf2', muted: '#9aa1b4', control: '#343b4c', hover: '#454e65', field: '#2b3142',
      placeholder: '#8a91a5', border: '#30364a', shadow: 'rgba(0,0,0,.35)',
    },
    light: {
      surface: '#ffffff', text: '#1f2330', muted: '#5f6678', control: '#eceef4', hover: '#dfe2ea', field: '#f4f5f9',
      placeholder: '#8a91a5', border: '#d6d9e2', shadow: 'rgba(20,24,40,.18)',
    },
  };

  const MAX_ELEMENTS = 12;
  const DRAW_THRESHOLD = 6;

  const STYLE = `
    :host { all: initial; }
    * { box-sizing: border-box; font: 13px/1.35 system-ui, -apple-system, "Segoe UI", sans-serif; }
    .bar { position: fixed; right: 16px; bottom: 16px; z-index: 2147483647; display: flex; align-items: center; gap: 6px;
      padding: 5px 6px 5px 10px; border-radius: 999px; background: var(--surface); color: var(--text); border: 1px solid var(--border);
      box-shadow: 0 4px 18px var(--shadow); user-select: none; pointer-events: auto; }
    .bar.collapsed .body { display: none; }
    .body, .tool { display: contents; }
    .separator { width: 1px; height: 18px; margin: 0 2px; background: var(--border); }
    .brand { font-weight: 600; letter-spacing: .02em; cursor: grab; touch-action: none; }
    .bar.dragging, .bar.dragging .brand { cursor: grabbing; }
    .bar.dragging { opacity: .92; }
    .dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; background: ${MARK_COLOR}; margin-right: 6px; }
    button { all: unset; cursor: pointer; padding: 4px 10px; border-radius: 999px; background: var(--control); color: var(--text); }
    button:hover { background: var(--hover); }
    button:focus-visible { outline: 2px solid ${MARK_COLOR}; outline-offset: 1px; }
    button.on, button.primary { background: ${MARK_COLOR}; color: #fff; }
    button.primary:hover { filter: brightness(1.1); }
    button[disabled] { opacity: .45; cursor: default; filter: none; }
    button.icon { min-width: 30px; padding: 3px 8px; font-size: 15px; line-height: 1.2; text-align: center; }
    button.count { min-width: 26px; padding: 3px 8px; text-align: center; }
    button.small { padding: 2px 8px; font-size: 12px; }
    .hidden { display: none !important; }
    .outline { position: fixed; z-index: 2147483646; pointer-events: none; border: 2px solid ${MARK_COLOR}; background: rgba(124,58,237,.08);
      border-radius: 3px; display: none; }
    .label { position: fixed; z-index: 2147483647; pointer-events: none; padding: 1px 6px; border-radius: 4px; background: ${MARK_COLOR};
      color: #fff; font-size: 11px; display: none; white-space: nowrap; }
    .draw { position: fixed; z-index: 2147483646; pointer-events: none; border: 2px dashed ${MARK_COLOR}; background: rgba(124,58,237,.10); }
    .chosen { position: fixed; z-index: 2147483645; pointer-events: none; border: 2px solid ${MARK_COLOR}; background: rgba(124,58,237,.14); border-radius: 3px; }
    .popup, .panel, .notice { position: fixed; z-index: 2147483647; padding: 10px; border-radius: 10px; background: var(--surface); color: var(--text);
      border: 1px solid var(--border); box-shadow: 0 8px 28px var(--shadow); pointer-events: auto; }
    .popup { width: 320px; }
    .popup .what { color: var(--muted); margin-bottom: 6px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .popup textarea { all: unset; box-sizing: border-box; display: block; width: 100%; min-height: 64px; padding: 6px 8px; border-radius: 6px;
      background: var(--field); color: var(--text); border: 1px solid var(--border); white-space: pre-wrap; resize: vertical; }
    .popup textarea::placeholder { color: var(--placeholder); }
    .row { display: flex; align-items: center; gap: 6px; margin-top: 8px; }
    .row .grow { flex: 1; }
    .panel { width: 340px; max-height: 50vh; overflow: auto; padding: 8px; }
    .panel .title { font-weight: 600; padding: 2px 4px 6px; }
    .panel .empty { color: var(--muted); padding: 4px; }
    .item { display: flex; gap: 8px; align-items: flex-start; padding: 6px 4px; border-radius: 6px; }
    .item:hover { background: var(--field); }
    .item .number { flex: none; min-width: 20px; height: 20px; padding: 0 6px; border-radius: 10px; background: ${MARK_COLOR}; color: #fff;
      font-size: 12px; font-weight: 600; line-height: 20px; text-align: center; }
    .item .text { flex: 1; min-width: 0; }
    .item .what { color: var(--muted); font-size: 12px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .item .note { overflow: hidden; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; white-space: pre-wrap; }
    .item .where { color: var(--muted); font-size: 11px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .item .actions { flex: none; display: flex; gap: 4px; }
    .notice { max-width: 340px; padding: 8px 10px; }
    .box { position: fixed; z-index: 2147483645; pointer-events: none; border: 2px dashed ${MARK_COLOR}; border-radius: 3px; }
    .box.hot { border-style: solid; background: rgba(124,58,237,.16); }
    .badge { position: fixed; z-index: 2147483646; pointer-events: auto; cursor: pointer; min-width: 20px; height: 20px; padding: 0 6px;
      border-radius: 10px; background: ${MARK_COLOR}; color: #fff; font-size: 12px; font-weight: 600; line-height: 20px; text-align: center;
      box-shadow: 0 2px 6px rgba(0,0,0,.35); }
  `;

  let host = null;
  let root = null;
  let bar = null;
  let tools = null;
  let toggle = null;
  let chooser = null;
  let counter = null;
  let sender = null;
  let themeButton = null;
  let themeStyle = null;
  let theme = 'dark';
  // Where the user moved the bar: its distance from the right and the bottom of the viewport, or null for its place.
  let position = null;
  const MARGIN = 16;
  let outline = null;
  let label = null;
  let popup = null;
  let panel = null;
  let notice = null;
  let enabled = false;
  let picking = false;
  let nextId = 1;
  // The marks of this page, by the id of their annotation: their elements or their area, and what shows them.
  const marks = new Map();
  // The elements chosen with Shift+click for the next note, each with its box.
  const chosen = [];
  // The annotations of the session waiting to be sent, as the host lists them; null until it first does.
  let items = null;

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = text;
    return node;
  }

  function button(text, className, title, action) {
    const node = element('button', className || '', text);
    if (title) node.title = title;
    node.addEventListener('click', event => { event.preventDefault(); event.stopPropagation(); if (event.isTrusted && !node.disabled) action(event); });
    return node;
  }

  // A tool of the bar: a group of controls, after the tools already there and before the settings of the bar.
  function addTool(name, controls) {
    const group = element('span', 'tool');
    group.dataset.tool = name;
    for (const control of controls) group.appendChild(control);
    tools.appendChild(group);
    return group;
  }

  function build() {
    if (host && host.isConnected) return;
    // A plain element, never a custom one: the page could define a custom element of any name before the bar is built,
    // and the internals of such an element would give it the shadow root of the bar, closed or not.
    host = document.createElement('div');
    host.setAttribute('data-codealta-toolbar', '');
    host.style.setProperty('all', 'initial', 'important');
    root = host.attachShadow({ mode: 'closed' });
    const style = element('style');
    style.textContent = STYLE;
    themeStyle = element('style');
    root.appendChild(style);
    root.appendChild(themeStyle);

    bar = element('div', 'bar');
    const brand = element('span', 'brand');
    brand.appendChild(element('span', 'dot'));
    brand.appendChild(document.createTextNode('CodeAlta'));
    brand.title = 'Drag to move the bar · Click to collapse or expand · Double-click to put it back';
    makeHandle(brand);
    const body = element('span', 'body');
    tools = element('span', 'body');

    toggle = button('Annotate', '', 'Click an element, Shift+click several, or drag over an area; then write what should change (Esc ends)', () => setPicking(!picking));
    chooser = button('', 'primary hidden', 'Write one note for the elements chosen with Shift+click (Enter)', () => openNote({ targets: chosen.map(item => item.target), area: null }));
    counter = button('0', 'count', 'The annotations waiting: change or remove them', togglePanel);
    sender = button('Send', 'primary', 'Send the annotations to the session, with a message', openSend);
    addTool('annotate', [toggle, chooser, counter, sender]);

    // The look of the bar, kept by CodeAlta for every page: it changes the bar, never the page.
    themeButton = button('', 'icon', '', () => {
      const next = theme === 'dark' ? 'light' : 'dark';
      applyTheme(next);
      send({ type: 'theme', theme: next });
    });

    body.appendChild(tools);
    body.appendChild(element('span', 'separator'));
    body.appendChild(themeButton);
    bar.appendChild(brand);
    bar.appendChild(body);
    root.appendChild(bar);

    outline = element('div', 'outline');
    label = element('div', 'label');
    root.appendChild(outline);
    root.appendChild(label);
    applyTheme(theme);
    window.addEventListener('resize', placeBar);

    // Keys typed in the popups stay out of the page's own shortcuts as far as a bubbling listener can keep them.
    for (const type of ['keydown', 'keyup', 'keypress']) root.addEventListener(type, event => event.stopPropagation());
    (document.documentElement || document).appendChild(host);
    updateCount();
  }

  // The brand is the handle of the bar: dragged, it moves the bar out of the way of what is under it; clicked, it
  // collapses or expands the bar; double-clicked, it puts the bar back at the bottom right. CodeAlta keeps the place for
  // every page, as the distance from the right and the bottom of the viewport.
  function makeHandle(handle) {
    let drag = null;
    let dragged = false;
    handle.addEventListener('pointerdown', event => {
      if (!event.isTrusted || event.button !== 0) return;
      const rect = bar.getBoundingClientRect();
      drag = { id: event.pointerId, x: event.clientX, y: event.clientY, right: window.innerWidth - rect.right, bottom: window.innerHeight - rect.bottom, moved: false };
      dragged = false;
      handle.setPointerCapture(event.pointerId);
      event.preventDefault();
    });
    handle.addEventListener('pointermove', event => {
      if (!event.isTrusted || !drag || event.pointerId !== drag.id) return;
      const dx = event.clientX - drag.x;
      const dy = event.clientY - drag.y;
      if (!drag.moved && Math.abs(dx) + Math.abs(dy) < 4) return;
      drag.moved = true;
      bar.classList.add('dragging');
      position = { right: drag.right - dx, bottom: drag.bottom - dy };
      placeBar();
    });
    const end = event => {
      if (!event.isTrusted || !drag || event.pointerId !== drag.id) return;
      dragged = drag.moved;
      drag = null;
      bar.classList.remove('dragging');
      if (dragged) send({ type: 'position', position: position });
    };
    handle.addEventListener('pointerup', end);
    handle.addEventListener('pointercancel', end);
    handle.addEventListener('click', event => {
      if (!event.isTrusted) return;
      // The click that ends a drag does not collapse the bar.
      if (dragged) { dragged = false; return; }
      bar.classList.toggle('collapsed');
      placeBar();
    });
    handle.addEventListener('dblclick', event => {
      if (!event.isTrusted) return;
      moveBar(null);
      send({ type: 'position', position: null });
    });
  }

  function moveBar(value) {
    position = value && isFinite(value.right) && isFinite(value.bottom) ? { right: +value.right, bottom: +value.bottom } : null;
    placeBar();
  }

  // Puts the bar where the user moved it, kept inside the viewport whatever the size of the window and of the bar.
  function placeBar() {
    if (!bar) return;
    const right = position ? position.right : MARGIN;
    const bottom = position ? position.bottom : MARGIN;
    const maxRight = Math.max(0, window.innerWidth - bar.offsetWidth);
    const maxBottom = Math.max(0, window.innerHeight - bar.offsetHeight);
    bar.style.right = Math.round(Math.min(Math.max(0, right), maxRight)) + 'px';
    bar.style.bottom = Math.round(Math.min(Math.max(0, bottom), maxBottom)) + 'px';
    if (position) position = { right: parseFloat(bar.style.right), bottom: parseFloat(bar.style.bottom) };
    if (panel) placeNearBar(panel);
    if (notice) placeNearBar(notice);
  }

  // A panel of the bar opens above it, or below it when the bar is in the upper half of the window.
  function placeNearBar(node) {
    const rect = bar.getBoundingClientRect();
    const width = node.offsetWidth || 340;
    node.style.left = Math.max(8, Math.min(rect.right - width, window.innerWidth - width - 8)) + 'px';
    if (rect.top > window.innerHeight / 2) { node.style.top = ''; node.style.bottom = (window.innerHeight - rect.top + 8) + 'px'; }
    else { node.style.bottom = ''; node.style.top = (rect.bottom + 8) + 'px'; }
  }

  function applyTheme(name) {
    theme = THEMES[name] ? name : 'dark';
    if (themeStyle) {
      const colors = THEMES[theme];
      themeStyle.textContent = ':host { ' + Object.keys(colors).map(key => '--' + key + ': ' + colors[key] + ';').join(' ') + ' }';
    }
    if (themeButton) {
      // The button shows the look it switches to; U+FE0E keeps the sun and the moon as text, not as colored emoji.
      themeButton.textContent = theme === 'dark' ? '☀︎' : '☾︎';
      themeButton.title = theme === 'dark' ? 'Light toolbar' : 'Dark toolbar';
      themeButton.setAttribute('aria-label', themeButton.title);
    }
  }

  function waiting() {
    return items ? items.length : marks.size;
  }

  function updateCount() {
    if (!counter) return;
    const count = waiting();
    counter.textContent = String(count);
    sender.disabled = count === 0;
    chooser.textContent = 'Note for ' + chosen.length;
    chooser.classList.toggle('hidden', chosen.length === 0);
    placeBar();
  }

  function setPicking(on) {
    picking = on && enabled;
    if (toggle) toggle.classList.toggle('on', picking);
    if (!picking) { hideOutline(); clearChosen(); cancelDraw(); }
  }

  function hideOutline() {
    if (outline) outline.style.display = 'none';
    if (label) label.style.display = 'none';
  }

  function ours(event) {
    return host && event.composedPath().includes(host);
  }

  function targetOf(event) {
    for (const node of event.composedPath()) {
      if (node && node.nodeType === 1 && node !== host) return node;
    }
    return null;
  }

  function showOutline(target) {
    if (!target || target === document.documentElement) { hideOutline(); return; }
    const rect = target.getBoundingClientRect();
    Object.assign(outline.style, { display: 'block', left: rect.left + 'px', top: rect.top + 'px', width: rect.width + 'px', height: rect.height + 'px' });
    label.textContent = short(target) + (chosen.length ? '  ·  Shift+click: ' + (chosen.some(item => item.target === target) ? 'remove' : 'add') : '');
    Object.assign(label.style, { display: 'block', left: Math.max(0, rect.left) + 'px', top: Math.max(0, rect.top - 20) + 'px' });
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Choosing several elements, drawing an area
  // ---------------------------------------------------------------------------------------------------------------

  function toggleChosen(target) {
    const index = chosen.findIndex(item => item.target === target);
    if (index >= 0) { chosen[index].box.remove(); chosen.splice(index, 1); }
    else if (chosen.length < MAX_ELEMENTS) {
      const box = element('div', 'chosen');
      root.appendChild(box);
      chosen.push({ target: target, box: box });
    }
    updateCount();
    track();
  }

  function clearChosen() {
    for (const item of chosen) item.box.remove();
    chosen.length = 0;
    updateCount();
  }

  let pressed = null;
  let drawing = null;
  let swallowClick = false;

  function cancelDraw() {
    if (drawing) drawing.box.remove();
    drawing = null;
    pressed = null;
  }

  function viewRect(a, b) {
    return { left: Math.min(a.x, b.x), top: Math.min(a.y, b.y), right: Math.max(a.x, b.x), bottom: Math.max(a.y, b.y) };
  }

  // The elements an area holds: the outermost ones that are wholly inside it, in the order of the page; when none is,
  // the smallest element that holds the whole area.
  function elementsIn(rect) {
    const found = [];
    if (document.body) {
      for (const node of document.body.querySelectorAll('*')) {
        if (node === host || /^(script|style|template|noscript)$/.test(node.localName)) continue;
        if (found.some(outer => outer.contains(node))) continue;
        const box = node.getBoundingClientRect();
        if (box.width < 1 || box.height < 1) continue;
        if (box.left >= rect.left - 1 && box.top >= rect.top - 1 && box.right <= rect.right + 1 && box.bottom <= rect.bottom + 1) {
          found.push(node);
          if (found.length === MAX_ELEMENTS) break;
        }
      }
    }
    if (found.length) return found;
    let node = document.elementFromPoint((rect.left + rect.right) / 2, (rect.top + rect.bottom) / 2);
    while (node && node !== document.body && node !== document.documentElement) {
      const box = node.getBoundingClientRect();
      if (node !== host && box.left <= rect.left && box.top <= rect.top && box.right >= rect.right && box.bottom >= rect.bottom) return [node];
      node = node.parentElement;
    }
    return [];
  }

  // The box around elements, in pixels of the document.
  function areaOf(targets) {
    let left = Infinity, top = Infinity, right = -Infinity, bottom = -Infinity;
    for (const target of targets) {
      const box = target.getBoundingClientRect();
      left = Math.min(left, box.left); top = Math.min(top, box.top); right = Math.max(right, box.right); bottom = Math.max(bottom, box.bottom);
    }
    if (!isFinite(left)) return { x: 0, y: 0, width: 0, height: 0, drawn: false };
    return { x: left + window.scrollX, y: top + window.scrollY, width: right - left, height: bottom - top, drawn: false };
  }

  function viewOf(area) {
    const left = area.x - window.scrollX;
    const top = area.y - window.scrollY;
    return { left: left, top: top, right: left + area.width, bottom: top + area.height };
  }

  // ---------------------------------------------------------------------------------------------------------------
  // The popups: the note of a new annotation or of one waiting, the message sent with them, a notice
  // ---------------------------------------------------------------------------------------------------------------

  function closePopup() {
    if (popup) { popup.remove(); popup = null; }
  }

  function placePopup(anchor) {
    if (!anchor) { placeNearBar(popup); return; }
    const width = 320;
    const left = Math.min(Math.max(8, anchor.left), window.innerWidth - width - 8);
    const below = anchor.bottom + 8;
    const top = below + 170 < window.innerHeight ? below : Math.max(8, anchor.top - 178);
    Object.assign(popup.style, { left: left + 'px', top: top + 'px' });
  }

  function describeChoice(choice) {
    const targets = choice.targets;
    if (choice.area) {
      const size = Math.round(choice.area.width) + '×' + Math.round(choice.area.height);
      return 'Area ' + size + (targets.length ? ' · ' + targets.length + (targets.length === 1 ? ' element: ' : ' elements: ') + targets.slice(0, 3).map(short).join(', ') : '');
    }
    if (targets.length === 1) return short(targets[0]) + (nameOf(targets[0]) ? ' · ' + clip(nameOf(targets[0]), 60) : '');
    return targets.length + ' elements · ' + targets.slice(0, 3).map(short).join(', ') + (targets.length > 3 ? ', …' : '');
  }

  // A popup with a text field: its title, its text, its buttons, and what Ctrl+Enter does.
  function openPopup(title, text, placeholder, anchor, actions, commit) {
    closePopup();
    popup = element('div', 'popup');
    popup.appendChild(element('div', 'what', title));
    const field = element('textarea');
    field.placeholder = placeholder;
    field.value = text || '';
    const row = element('div', 'row');
    for (const action of actions) row.appendChild(action === 'grow' ? element('span', 'grow') : action);
    popup.appendChild(field);
    popup.appendChild(row);
    root.appendChild(popup);
    placePopup(anchor);
    field.addEventListener('keydown', event => {
      if (!event.isTrusted) return;
      if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) { event.preventDefault(); commit(field); }
      else if (event.key === 'Escape') { event.preventDefault(); closePopup(); }
    });
    setTimeout(() => { field.focus(); field.setSelectionRange(field.value.length, field.value.length); }, 0);
    return field;
  }

  // The note of a new annotation, on the elements chosen or the area drawn.
  function openNote(choice) {
    if (!choice.targets.length && !choice.area) return;
    hideOutline();
    closePanel();
    const area = choice.area || areaOf(choice.targets);
    let field = null;
    const commit = () => {
      const text = field.value.trim();
      if (!text) { field.focus(); return; }
      const id = 'a' + Date.now().toString(36) + '-' + (nextId++);
      addMark(id, choice.targets, choice.area, null, text);
      send({ type: 'annotation', id: id, note: text, url: location.href, elements: choice.targets.map(describe), area: area });
      clearChosen();
      closePopup();
    };
    const cancel = button('Cancel', '', '', closePopup);
    const save = button('Save', 'primary', 'Save the note (Ctrl+Enter)', commit);
    field = openPopup(describeChoice(choice), '', 'What should change here?', viewOf(area), ['grow', cancel, save], commit);
  }

  // The note of an annotation waiting: changed, or the annotation removed.
  function openEdit(id) {
    const item = (items || []).find(candidate => candidate.id === id);
    const mark = marks.get(id);
    const note = item ? item.note : mark ? mark.note : '';
    const number = item ? item.number : mark ? mark.number : null;
    const title = (number == null ? '' : '#' + number + ' · ') + (item && item.label ? item.label : 'Annotation');
    let field = null;
    const commit = () => {
      const text = field.value.trim();
      if (!text) { field.focus(); return; }
      if (mark) { mark.note = text; mark.badge.title = text; }
      if (item) item.note = text;
      send({ type: 'edit', id: id, note: text });
      closePopup();
      renderPanel();
    };
    const remove = button('Remove', '', 'Remove this annotation', () => { forget(id); closePopup(); });
    const cancel = button('Cancel', '', '', closePopup);
    const save = button('Save', 'primary', 'Save the note (Ctrl+Enter)', commit);
    closePanel();
    field = openPopup(title, note, 'What should change here?', mark ? markView(mark) : null, [remove, 'grow', cancel, save], commit);
  }

  // The message sent with the annotations, as the prompt of the session.
  function openSend() {
    const count = waiting();
    if (!count) return;
    closePanel();
    let field = null;
    const commit = () => {
      send({ type: 'send', text: field.value.trim() });
      closePopup();
      showNotice('Sending…');
    };
    const cancel = button('Cancel', '', '', closePopup);
    const go = button('Send', 'primary', 'Send to the session (Ctrl+Enter)', commit);
    field = openPopup('Send ' + count + (count === 1 ? ' annotation' : ' annotations') + ' to the session',
      '', 'A message to the agent (optional)', null, ['grow', cancel, go], commit);
  }

  let noticeTimer = 0;
  function showNotice(text) {
    if (!root) return;
    if (!notice) { notice = element('div', 'notice'); root.appendChild(notice); }
    notice.textContent = text;
    placeNearBar(notice);
    clearTimeout(noticeTimer);
    noticeTimer = setTimeout(() => { if (notice) { notice.remove(); notice = null; } }, 5000);
  }

  // ---------------------------------------------------------------------------------------------------------------
  // The list of the annotations waiting
  // ---------------------------------------------------------------------------------------------------------------

  function samePage(url) {
    try { const a = new URL(url), b = new URL(location.href); return a.origin === b.origin && a.pathname === b.pathname && a.search === b.search; }
    catch (_) { return false; }
  }

  function closePanel() {
    if (panel) { panel.remove(); panel = null; }
    hot(null);
  }

  function togglePanel() {
    if (panel) { closePanel(); return; }
    closePopup();
    panel = element('div', 'panel');
    root.appendChild(panel);
    renderPanel();
  }

  function renderPanel() {
    if (!panel) return;
    panel.textContent = '';
    const list = items || [...marks.entries()].map(([id, mark]) => ({ id: id, number: mark.number, label: '', note: mark.note, url: location.href }));
    panel.appendChild(element('div', 'title', list.length ? 'Annotations waiting (' + list.length + ')' : 'Annotations waiting'));
    if (!list.length) panel.appendChild(element('div', 'empty', 'None yet: turn on Annotate, then click an element, Shift+click several, or drag over an area.'));
    for (const item of list) {
      const row = element('div', 'item');
      row.appendChild(element('span', 'number', item.number == null ? '…' : String(item.number)));
      const text = element('div', 'text');
      if (item.label) text.appendChild(element('div', 'what', item.label));
      text.appendChild(element('div', 'note', item.note));
      if (!samePage(item.url)) {
        let where = item.url;
        try { where = new URL(item.url).pathname; } catch (_) { /* as it is */ }
        text.appendChild(element('div', 'where', 'On another page: ' + where));
      }
      row.appendChild(text);
      const actions = element('div', 'actions');
      actions.appendChild(button('Edit', 'small', 'Change the note', () => openEdit(item.id)));
      actions.appendChild(button('✕', 'small', 'Remove this annotation', () => forget(item.id)));
      row.appendChild(actions);
      row.addEventListener('mouseenter', () => hot(item.id, true));
      row.addEventListener('mouseleave', () => hot(null));
      panel.appendChild(row);
    }
    placeNearBar(panel);
  }

  // Removes an annotation here at once; the host removes it everywhere and lists the others.
  function forget(id) {
    removeMark(id);
    if (items) items = items.filter(item => item.id !== id);
    send({ type: 'remove', id: id });
    updateCount();
    renderPanel();
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Marks
  // ---------------------------------------------------------------------------------------------------------------

  let tracking = 0;

  // Shows an annotation of this page as the one pointed at in the list, scrolled into view when asked.
  function hot(id, reveal) {
    for (const [key, mark] of marks) for (const box of mark.boxes) box.classList.toggle('hot', key === id);
    const mark = id ? marks.get(id) : null;
    if (mark && reveal) {
      const view = markView(mark);
      if (view && (view.bottom < 0 || view.top > window.innerHeight)) window.scrollBy({ top: view.top - window.innerHeight / 3, behavior: 'smooth' });
    }
  }

  function addMark(id, targets, area, number, note) {
    removeMark(id);
    const boxes = (area ? [null] : targets).map(() => { const box = element('div', 'box'); root.appendChild(box); return box; });
    const badge = element('div', 'badge', number == null ? '…' : String(number));
    badge.title = (note || '') + '\nClick to change or remove this annotation.';
    badge.addEventListener('click', event => {
      event.preventDefault();
      event.stopPropagation();
      if (event.isTrusted) openEdit(id);
    });
    root.appendChild(badge);
    marks.set(id, { targets: targets.slice(), area: area, boxes: boxes, badge: badge, number: number, note: note });
    updateCount();
    track();
  }

  function removeMark(id) {
    const mark = marks.get(id);
    if (!mark) return;
    for (const box of mark.boxes) box.remove();
    mark.badge.remove();
    marks.delete(id);
    updateCount();
  }

  // Where a mark is in the viewport: its area, or the box around its elements still in the page.
  function markView(mark) {
    if (mark.area) return viewOf(mark.area);
    const present = mark.targets.filter(target => target && target.isConnected);
    return present.length ? viewOf(areaOf(present)) : null;
  }

  function boxView(target) {
    if (!target || !target.isConnected) return null;
    const box = target.getBoundingClientRect();
    return { left: box.left, top: box.top, right: box.right, bottom: box.bottom };
  }

  // Shows a box around a part of the viewport, or hides it when that part is out of view.
  function setBox(box, view, pad) {
    const shown = !!view && view.bottom > 0 && view.top < window.innerHeight && view.right > 0 && view.left < window.innerWidth;
    box.style.display = shown ? 'block' : 'none';
    if (shown) {
      Object.assign(box.style, {
        left: view.left - pad + 'px', top: view.top - pad + 'px', width: view.right - view.left + 2 * pad + 'px', height: view.bottom - view.top + 2 * pad + 'px',
      });
    }
    return shown;
  }

  function placeMarks() {
    tracking = 0;
    for (const mark of marks.values()) {
      let first = null;
      if (mark.area) {
        const view = viewOf(mark.area);
        if (setBox(mark.boxes[0], view, 0)) first = view;
      } else {
        mark.targets.forEach((target, index) => {
          // An element scrolled out of the viewport takes its box with it.
          const view = boxView(target);
          if (setBox(mark.boxes[index], view, 3) && !first) first = view;
        });
      }
      mark.badge.style.display = first ? 'block' : 'none';
      if (first) Object.assign(mark.badge.style, { left: Math.max(0, first.left - 10) + 'px', top: Math.max(0, first.top - 10) + 'px' });
    }
    for (const item of chosen) setBox(item.box, boxView(item.target), 2);
    if (marks.size || chosen.length) track();
  }

  function track() {
    if (!tracking) tracking = requestAnimationFrame(placeMarks);
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Input while annotating: the page never sees the clicks and the drags that choose
  // ---------------------------------------------------------------------------------------------------------------

  // Only what the user does chooses: an event the page makes up (isTrusted false) is left to the page, and does nothing here.
  const BLOCKED = ['mousedown', 'mouseup', 'dblclick', 'contextmenu', 'auxclick', 'touchstart', 'touchend'];
  for (const type of BLOCKED) {
    window.addEventListener(type, event => {
      if (!picking || !event.isTrusted || ours(event)) return;
      event.preventDefault();
      event.stopImmediatePropagation();
    }, true);
  }

  window.addEventListener('pointerdown', event => {
    if (!picking || !event.isTrusted || ours(event)) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    if (event.button === 0 && !popup) pressed = { x: event.clientX, y: event.clientY };
  }, true);

  window.addEventListener('pointermove', event => {
    if (!picking || !pressed || !event.isTrusted) return;
    const at = { x: event.clientX, y: event.clientY };
    if (!drawing && Math.abs(at.x - pressed.x) + Math.abs(at.y - pressed.y) < DRAW_THRESHOLD) return;
    if (!drawing) { drawing = { box: element('div', 'draw') }; root.appendChild(drawing.box); hideOutline(); }
    const rect = viewRect(pressed, at);
    Object.assign(drawing.box.style, { left: rect.left + 'px', top: rect.top + 'px', width: rect.right - rect.left + 'px', height: rect.bottom - rect.top + 'px' });
    const selection = window.getSelection && window.getSelection();
    if (selection && selection.rangeCount) selection.removeAllRanges();
  }, true);

  window.addEventListener('pointerup', event => {
    if (!event.isTrusted) return;
    if (!picking) { pressed = null; return; }
    // A drag that ends over a mark or the bar still draws its area.
    if (!drawing) {
      pressed = null;
      if (ours(event)) return;
      event.preventDefault();
      event.stopImmediatePropagation();
      return;
    }
    event.preventDefault();
    event.stopImmediatePropagation();
    const rect = viewRect(pressed, { x: event.clientX, y: event.clientY });
    cancelDraw();
    // The click that ends a drag does not choose an element.
    swallowClick = true;
    setTimeout(() => { swallowClick = false; }, 0);
    if (rect.right - rect.left < DRAW_THRESHOLD || rect.bottom - rect.top < DRAW_THRESHOLD) return;
    const area = { x: rect.left + window.scrollX, y: rect.top + window.scrollY, width: rect.right - rect.left, height: rect.bottom - rect.top, drawn: true };
    clearChosen();
    openNote({ targets: elementsIn(rect), area: area });
  }, true);

  window.addEventListener('click', event => {
    if (!picking || !event.isTrusted || ours(event)) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    if (swallowClick) { swallowClick = false; return; }
    const target = targetOf(event);
    if (!target || target === document.documentElement || popup) return;
    if (event.shiftKey || event.metaKey || event.ctrlKey) { toggleChosen(target); showOutline(target); return; }
    if (chosen.length && !chosen.some(item => item.target === target)) toggleChosen(target);
    openNote({ targets: chosen.length ? chosen.map(item => item.target) : [target], area: null });
  }, true);

  window.addEventListener('mousemove', event => {
    if (!picking || popup || drawing || !event.isTrusted || ours(event)) return;
    showOutline(targetOf(event));
  }, true);

  window.addEventListener('keydown', event => {
    if (!event.isTrusted || ours(event)) return;
    if (event.key === 'Escape') {
      if (popup) closePopup();
      else if (panel) closePanel();
      else if (drawing) cancelDraw();
      else if (chosen.length) clearChosen();
      else if (picking) setPicking(false);
      else return;
      event.preventDefault();
      event.stopImmediatePropagation();
    } else if (event.key === 'Enter' && picking && !popup && chosen.length) {
      event.preventDefault();
      event.stopImmediatePropagation();
      openNote({ targets: chosen.map(item => item.target), area: null });
    }
  }, true);

  // ---------------------------------------------------------------------------------------------------------------
  // What the host calls
  // ---------------------------------------------------------------------------------------------------------------

  function whenBody(action) {
    if (document.body) action();
    else document.addEventListener('DOMContentLoaded', action, { once: true });
  }

  // Marks of annotations saved before a reload, found again by their selectors while the page renders.
  function restore(pending) {
    let attempts = 0;
    const left = pending.slice();
    const tick = () => {
      attempts++;
      for (let index = left.length - 1; index >= 0; index--) {
        const item = left[index];
        const selectors = Array.isArray(item.selectors) ? item.selectors : [];
        const found = [];
        for (const selector of selectors) {
          try { const node = document.querySelector(selector); if (node) found.push(node); } catch (_) { /* not a selector of this document */ }
        }
        const area = item.area ? { x: +item.area.x, y: +item.area.y, width: +item.area.width, height: +item.area.height, drawn: true } : null;
        // An area shows at once; elements wait for the page to render them, then show those found.
        if (area || found.length === selectors.length || (attempts >= 20 && found.length)) {
          addMark(item.id, found, area, item.number, item.note);
          left.splice(index, 1);
        }
      }
      if (left.length && attempts < 20) setTimeout(tick, 500);
    };
    tick();
  }

  globalThis[KEY] = {
    enable(options) {
      enabled = true;
      if (options && typeof options.theme === 'string') applyTheme(options.theme);
      if (options && 'position' in options) moveBar(options.position);
      whenBody(() => {
        build();
        if (options && Array.isArray(options.pending)) restore(options.pending);
        updateCount();
      });
      return true;
    },
    // An element of an annotation, by its index among them.
    node(id, index) {
      const mark = marks.get(id);
      return mark ? mark.targets[index || 0] || null : null;
    },
    marked(id, number) {
      const mark = marks.get(id);
      if (mark) { mark.number = number; mark.badge.textContent = String(number); }
      return !!mark;
    },
    remove(id) {
      removeMark(id);
      return true;
    },
    // The annotations of the session waiting to be sent, in their order: the count, the list and the notes follow.
    list(value) {
      items = Array.isArray(value) ? value.filter(item => item && typeof item.id === 'string') : [];
      for (const item of items) {
        const mark = marks.get(item.id);
        if (mark) { mark.note = item.note; mark.badge.title = item.note + '\nClick to change or remove this annotation.'; }
      }
      updateCount();
      renderPanel();
      return true;
    },
    notice(text) {
      showNotice(String(text));
      return true;
    },
    // Shows the bar in the look the user chose, here or in another tab.
    theme(name) {
      applyTheme(name);
      return true;
    },
    // Puts the bar where the user moved it, here or in another tab; null puts it back at the bottom right.
    place(value) {
      moveBar(value);
      return true;
    },
    // Turns the Annotate mode on or off, as the button of the toolbar does.
    mode(on) {
      if (!enabled) return false;
      whenBody(() => { build(); setPicking(!!on); });
      return true;
    },
    clear() {
      for (const id of [...marks.keys()]) removeMark(id);
      closePopup();
      setPicking(false);
      return true;
    },
    // The annotations a prompt carried: their marks leave the page; those saved meanwhile keep theirs, and a note being
    // written stays open.
    taken(ids) {
      for (const id of Array.isArray(ids) ? ids : []) removeMark(id);
      if (!popup) setPicking(false);
      return true;
    },
    state() {
      return { enabled: enabled, picking: picking, noting: !!popup, marks: marks.size, chosen: chosen.length, waiting: waiting(), theme: theme, position: position };
    },
  };

  send({ type: 'ready', url: location.href, title: document.title });
})();
//# sourceURL=codealta-toolbar.js