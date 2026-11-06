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

  // What goes into the bar stays out of the page. A key, an input, a paste, a copy, a cut and the drags of a drop cross
  // the shadow root of the bar: a listener of the page on the window, as the event starts, gets it before the bar does,
  // with the keys, the words and the files it carries. This script runs as a document is created, before the scripts of
  // the page, so these listeners are the first of the window: they stop there every event bound for the bar, run the
  // keys of its fields and hand its images to the note open (see guard). In a page already open when CodeAlta added this
  // script, the page may have listened first.
  for (const type of ['keydown', 'keypress', 'keyup', 'beforeinput', 'input', 'textInput', 'compositionstart', 'compositionupdate', 'compositionend', 'paste', 'copy', 'cut', 'dragenter', 'dragover', 'dragleave', 'drop']) {
    window.addEventListener(type, event => guard(event), true);
  }

  const MAX_HTML = 1500;
  const MAX_TEXT = 300;
  const MARK_COLOR = '#7c3aed';
  // The bounds of what the bar reads of an element: the page may hold anything, of any size, so every text, every list
  // and every walk of the DOM stops at a bound. The host bounds and filters what it receives again.
  const MAX_NAME = 120;
  const MAX_ATTRIBUTE = 300;
  const MAX_CLASSES = 20;
  const MAX_CLASS = 120;
  const MAX_ID = 200;
  const MAX_LABELLED_BY = 16;
  const MAX_HTML_NODES = 400;
  const MAX_HTML_DEPTH = 12;
  const MAX_TEXT_NODES = 400;
  const MAX_AREA_SCAN = 10000;

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
    if (explicit) return explicit.trim().slice(0, 200).split(/\s+/)[0];
    if (element.localName === 'input') {
      const type = (element.getAttribute('type') || 'text').toLowerCase();
      return type === 'checkbox' ? 'checkbox' : type === 'radio' ? 'radio' : type === 'button' || type === 'submit' || type === 'reset' ? 'button' : 'textbox';
    }
    return IMPLICIT_ROLES[element.localName] || '';
  }

  function nameOf(element) {
    const label = element.getAttribute('aria-label');
    if (label) return safeText(clip(squash(label.slice(0, 4 * MAX_NAME)), MAX_NAME));
    const labelledBy = element.getAttribute('aria-labelledby');
    if (labelledBy) {
      const ids = labelledBy.slice(0, 1000).split(/\s+/).filter(Boolean).slice(0, MAX_LABELLED_BY);
      // A label may be hidden and still name the element.
      const text = ids.map(id => document.getElementById(id)).filter(Boolean).map(node => textOf(node, MAX_NAME, true)).join(' ');
      if (text) return safeText(clip(squash(text), MAX_NAME));
    }
    for (const attribute of ['alt', 'title', 'placeholder']) {
      const value = element.getAttribute(attribute);
      if (value) return safeText(clip(squash(value.slice(0, 4 * MAX_NAME)), MAX_NAME));
    }
    return textOf(element, 80);
  }

  // ---------------------------------------------------------------------------------------------------------------
  // What the bar tells of the page: bounded, and without what may be a secret
  //
  // The prompt carries what the page shows of an element, and is kept in the session and shown in its timeline: only
  // the attributes that say what an element is go into it, an address loses its query and fragment (where tokens
  // travel), and a value that looks like a credential is replaced. The host applies the same rules again.
  // ---------------------------------------------------------------------------------------------------------------

  const REDACTED = '[redacted]';
  // The attributes kept in the HTML of an element, with every aria-* one. Of the data-* attributes, which a page may fill
  // with anything, only the hooks of tests are kept: they name an element, and its source can be found by them.
  const SAFE_ATTRIBUTES = new Set(['id', 'class', 'name', 'type', 'role', 'href', 'src', 'alt', 'title', 'placeholder', 'for', 'action', 'method',
    'data-testid', 'data-test', 'data-test-id', 'data-cy', 'data-qa']);
  // Attributes that name an element rather than carry a value: name="password" or class="api-key" says what a field is,
  // and is kept.
  const NAMING_ATTRIBUTES = new Set(['id', 'class', 'name', 'type', 'role', 'for', 'method', 'data-testid', 'data-test', 'data-test-id', 'data-cy', 'data-qa']);
  const URL_ATTRIBUTES = new Set(['href', 'src', 'action']);
  const SECRET = /access[_-]?token|auth[_-]?token|refresh[_-]?token|id[_-]?token|api[_-]?key|apikey|client[_-]?secret|oauth[_-]?state|x-amz-|session[_-]?id|sessionid|csrf|xsrf|secret|password|passwd|bearer\s/i;
  // A value written after the name of a credential in a text, such as "token=…", "Password: hunter2" or "Authorization:
  // Bearer …", or after "Bearer": it goes whatever it looks like, since a weak password is a password all the same.
  const SECRET_VALUE = /((?:access[_-]?token|auth[_-]?token|refresh[_-]?token|id[_-]?token|session[_-]?token|api[_-]?key|apikey|client[_-]?secret|session[_-]?id|sessionid|csrf[_-]?token|xsrf[_-]?token|authorization|auth|secret|password|passwd|pwd|token|session|cookie|bearer)["']?\s*[:=]\s*["']?(?:(?:bearer|basic)\s+)?|\bbearer\s+)([^\s"'&<>,;]+)/gi;
  const URL_IN_TEXT = /\b((?:https?|file):\/\/[^\s?#"'<>]*)[?#][^\s"'<>]*/gi;
  const VOID_TAGS = new Set(['area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input', 'link', 'meta', 'source', 'track', 'wbr']);
  // Elements whose content is code or hidden markup, never what the element shows.
  const OPAQUE = /^(script|style|noscript|template)$/;

  // An address without its query and fragment; of another scheme than http, https or file only the scheme is kept
  // (javascript:…, data:…, mailto:…). A link to an anchor of the page keeps its anchor.
  function safeUrl(value) {
    const text = String(value).trim();
    if (/^#[\w-]{1,80}$/.test(text) && !SECRET.test(text)) return text;
    const scheme = /^([a-zA-Z][a-zA-Z0-9+.-]*):/.exec(text);
    if (scheme && !/^(https?|file)$/i.test(scheme[1])) return scheme[1].toLowerCase() + ':…';
    const cut = text.search(/[?#]/);
    return cut < 0 ? text : text.slice(0, cut);
  }

  function safeAttribute(name, value) {
    let text = String(value).slice(0, 4 * MAX_ATTRIBUTE);
    if (URL_ATTRIBUTES.has(name)) text = safeUrl(text);
    if (!NAMING_ATTRIBUTES.has(name) && SECRET.test(text)) return REDACTED;
    return clip(squash(text), MAX_ATTRIBUTE);
  }

  // A text of the page: its addresses lose their query and fragment, and the values of credentials written in it go.
  function safeText(text) {
    return String(text).replace(URL_IN_TEXT, '$1').replace(SECRET_VALUE, '$1' + REDACTED);
  }

  function escapeHtml(text, quote) {
    text = String(text).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    return quote ? text.replace(/"/g, '&quot;') : text;
  }

  // The text an element shows, its whitespace collapsed: a walk of its text nodes that stops at a bound of nodes and of
  // text, where innerText would lay out and copy all of it. The text of scripts, styles and, unless asked, hidden
  // elements is left out.
  function textOf(element, limit, hidden) {
    let text = '';
    let seen = 0;
    const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
    for (let node = walker.nextNode(); node && seen < MAX_TEXT_NODES && text.length <= 2 * limit; node = walker.nextNode()) {
      seen++;
      const parent = node.parentElement;
      if (!parent || OPAQUE.test(parent.localName)) continue;
      if (!hidden && typeof parent.checkVisibility === 'function' && !parent.checkVisibility()) continue;
      text += String(node.nodeValue).slice(0, 2 * limit);
    }
    return safeText(clip(squash(text), limit));
  }

  // The opening tag of an element with the attributes the prompt may show.
  function openTag(node) {
    let tag = '<' + node.localName;
    let count = 0;
    for (const attribute of node.attributes) {
      if (count === 30) break;
      const name = attribute.name.toLowerCase();
      if (!SAFE_ATTRIBUTES.has(name) && !/^aria-[a-z-]+$/.test(name)) continue;
      count++;
      tag += ' ' + name + '="' + escapeHtml(safeAttribute(name, attribute.value), true) + '"';
    }
    return tag + '>';
  }

  // The HTML of an element as the prompt shows it: the attributes of the list above only, no content of scripts and
  // styles, no comments, and whitespace collapsed, within a bound of length, of depth and of the nodes it visits.
  function htmlOf(element) {
    let out = '';
    let visited = 0;
    let full = false;
    const add = text => {
      if (out.length + text.length <= MAX_HTML) { out += text; return; }
      out += text.slice(0, MAX_HTML - out.length);
      full = true;
    };
    const walk = (node, depth) => {
      if (full) return;
      if (visited++ >= MAX_HTML_NODES) { full = true; return; }
      if (node.nodeType === 3) {
        const text = String(node.nodeValue).slice(0, MAX_HTML).replace(/\s*\n\s*/g, '\n').replace(/[ \t\f\r]+/g, ' ');
        if (text) add(escapeHtml(safeText(text), false));
        return;
      }
      if (node.nodeType !== 1 || node === host) return;
      add(openTag(node));
      if (VOID_TAGS.has(node.localName)) return;
      if (!OPAQUE.test(node.localName)) {
        if (depth >= MAX_HTML_DEPTH && node.firstChild) add('…');
        else for (let child = node.firstChild; child && !full; child = child.nextSibling) walk(child, depth + 1);
      }
      if (!full) add('</' + node.localName + '>');
    };
    walk(element, 0);
    return full ? out + '…</' + element.localName + '>' : out;
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

  // A name a tool made up rather than a person: a long run of letters and digits in both cases, as hashes are.
  function looksHashy(value) {
    return /^[A-Za-z0-9_-]{12,}$/.test(value) && /\d/.test(value) && /[A-Z]/.test(value) && /[a-z]/.test(value);
  }

  // An id that changes from one render to the next: one of React's useId (:r1:, «r1»), or a hash.
  function stableId(id) {
    return !!id && id.length <= 80 && !/^[:«].*[:»]$/.test(id) && !/^(ember|ext-gen|yui_|jQuery)\d/.test(id) && !looksHashy(id);
  }

  // The classes of an element that stay from one build and one render to the next, at most two. The libraries of CSS
  // in JavaScript and the CSS modules add classes whose names hold a hash of the build (css-1x2y3z, sc-AxjAm with its
  // kGtEvt, Button_primary__3xYz1, _primary_1x2y3_12, jsx-123, svelte-abc), and a page adds and removes the classes of
  // a state (active, is-open) as the user moves: none of them finds the element again after a reload.
  function stableClasses(node) {
    const names = [...node.classList];
    const styled = names.some(name => name.startsWith('sc-'));
    const result = [];
    for (const name of names) {
      if (result.length === 2) break;
      if (!name || name.length > 60) continue;
      if (/^(css|sc|jsx|svelte|emotion|styled|ng-tns|ng-star)-/.test(name) || name.startsWith('_') || looksHashy(name)) continue;
      // A CSS module: the name of the class, then the hash of the build after two underscores or more.
      if (/_{2,}[A-Za-z0-9-]{5,}$/.test(name) && /_{2,}[A-Za-z0-9-]*\d[A-Za-z0-9-]*$|_{2,}(?=[A-Za-z0-9-]*[A-Z])(?=[A-Za-z0-9-]*[a-z])[A-Za-z0-9-]{5}$/.test(name)) continue;
      // The hash class styled-components puts beside its sc- class: a few letters in both cases.
      if (styled && /^[A-Za-z]{5,8}$/.test(name) && /[A-Z]/.test(name) && /[a-z]/.test(name)) continue;
      if (/^(active|selected|current|open|opened|closed|hover|hovered|focus|focused|focus-visible|disabled|checked|expanded|collapsed|visible|hidden|show|shown|in)$/.test(name)) continue;
      if (/^(is|has)-/.test(name)) continue;
      result.push(name);
    }
    return result;
  }

  // One step of a selector: the tag with its id, or with its stable classes.
  function selectorPart(node) {
    if (stableId(node.id)) return node.localName + '#' + escape(node.id);
    return node.localName + stableClasses(node).map(name => '.' + escape(name)).join('');
  }

  // A selector that finds the element again after a reload: its id where it has a stable one, else the shortest path
  // of tags with their ids or stable classes, from the element up, that finds it alone; the position of a step among
  // its siblings is added only where siblings match the same step.
  function selectorOf(element) {
    if (stableId(element.id) && unique('#' + escape(element.id))) return '#' + escape(element.id);
    const parts = [];
    let node = element;
    while (node && node.nodeType === 1 && node !== document.documentElement && parts.length < 10) {
      let part = selectorPart(node);
      const parent = node.parentElement;
      if (parent && !unique([part].concat(parts).join(' > '))) {
        let same = 0, index = 0;
        for (const child of parent.children) {
          if (child.localName !== node.localName) continue;
          same++;
          if (child === node) index = same;
        }
        let siblings = 0;
        try { siblings = parent.querySelectorAll(':scope > ' + part).length; } catch (_) { siblings = same; }
        if (siblings > 1) part += ':nth-of-type(' + index + ')';
      }
      parts.unshift(part);
      if (unique(parts.join(' > '))) return parts.join(' > ');
      node = parent;
    }
    return parts.join(' > ');
  }

  function describe(element) {
    const rect = element.getBoundingClientRect();
    const ancestors = [];
    for (let node = element.parentElement; node && node !== document.body && node !== document.documentElement && ancestors.length < 6; node = node.parentElement) {
      ancestors.unshift(clip(short(node), 200));
    }
    return {
      tag: element.localName,
      id: clip(element.id || '', MAX_ID),
      classes: [...element.classList].slice(0, MAX_CLASSES).map(name => clip(name, MAX_CLASS)),
      role: clip(roleOf(element), 64),
      name: nameOf(element),
      text: textOf(element, MAX_TEXT),
      html: htmlOf(element),
      selector: selectorOf(element),
      ancestors: ancestors,
      rect: { x: rect.left, y: rect.top, width: rect.width, height: rect.height },
      scroll: { x: window.scrollX, y: window.scrollY },
      viewport: { width: window.innerWidth, height: window.innerHeight },
      styles: stylesOf(element),
      nearby: nearbyOf(element),
    };
  }

  // The computed styles the prompt may show, by their names in CSS. A value that is the default of its property says
  // nothing and is left out.
  const STYLE_PROPERTIES = ['display', 'position', 'width', 'height', 'margin', 'padding', 'color', 'background-color', 'border', 'border-radius',
    'font-family', 'font-size', 'font-weight', 'line-height', 'text-align', 'z-index'];

  function isDefaultStyle(name, value) {
    if (!value || value === 'auto' || value === 'normal' || value === 'none') return true;
    switch (name) {
      case 'display': return value === 'inline';
      case 'position': return value === 'static';
      case 'background-color': return value === 'transparent' || value === 'rgba(0, 0, 0, 0)';
      case 'margin': case 'padding': case 'border-radius': return value === '0px';
      case 'border': return /^(0px |medium )?none\b/.test(value) || /^0px\b/.test(value);
      case 'text-align': return value === 'start';
      default: return false;
    }
  }

  function stylesOf(element) {
    const styles = {};
    const computed = getComputedStyle(element);
    for (const name of STYLE_PROPERTIES) {
      const value = clip(squash(computed.getPropertyValue(name).slice(0, 400)), 200);
      if (!isDefaultStyle(name, value)) styles[name] = value;
    }
    return styles;
  }

  // The elements beside an element, the nearest first, in a few words each: what it is among. Hidden ones are skipped,
  // and the walk stops at a bound.
  const MAX_NEARBY = 3;
  const MAX_NEARBY_SCAN = 40;

  function nearbyOf(element) {
    const nearby = [];
    for (const [side, step] of [['before', 'previousElementSibling'], ['after', 'nextElementSibling']]) {
      let found = 0;
      let seen = 0;
      for (let node = element[step]; node && found < MAX_NEARBY && seen < MAX_NEARBY_SCAN; node = node[step]) {
        seen++;
        if (node === host || OPAQUE.test(node.localName)) continue;
        const rect = node.getBoundingClientRect();
        if (rect.width < 1 && rect.height < 1) continue;
        const text = textOf(node, 60);
        nearby.push(side + ': ' + clip(short(node), 80) + (text ? ' "' + text + '"' : ''));
        found++;
      }
    }
    return nearby;
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
    .layer { position: fixed; inset: 0; z-index: 2147483647; display: block; width: 100vw; height: 100vh; max-width: none; max-height: none;
      margin: 0; padding: 0; border: 0; background: transparent; color: inherit; overflow: visible; pointer-events: none; }
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
      color: #fff; font-size: 11px; display: none; white-space: nowrap; max-width: calc(100vw - 8px); overflow: hidden; text-overflow: ellipsis;
      box-shadow: 0 1px 4px rgba(0,0,0,.3); }
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
    .switch { display: flex; align-items: center; gap: 6px; margin-top: 8px; color: var(--muted); cursor: pointer; }
    .switch input { margin: 0; accent-color: ${MARK_COLOR}; cursor: pointer; }
    .question::after { content: '?'; margin-left: 1px; }
    .choice { display: flex; align-items: center; gap: 8px; margin-top: 8px; }
    .intent { flex: none; display: inline-flex; gap: 2px; padding: 2px; border-radius: 999px; background: var(--field); border: 1px solid var(--border); }
    .intent button { padding: 2px 10px; font-size: 12px; background: transparent; }
    .intent button:hover { background: var(--hover); }
    .intent button.on { background: ${MARK_COLOR}; color: #fff; }
    .choice .hint { flex: 1; min-width: 0; color: var(--muted); font-size: 11px; }
    .images { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; }
    .image { display: inline-flex; align-items: center; gap: 2px; padding: 2px; border-radius: 6px; background: var(--field); border: 1px solid var(--border); }
    .image img { display: block; width: 40px; height: 40px; object-fit: cover; border-radius: 4px; }
    .image .name { padding: 0 4px; font-size: 12px; color: var(--muted); }
    .popup.dropping { outline: 2px dashed ${MARK_COLOR}; outline-offset: -4px; }
  `;

  let host = null;
  // The shadow root of the bar, and the layer that holds everything it shows: the parts of the bar go in the layer.
  let shadow = null;
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
  // Whether the prompt carries the details of the page with the annotations: CodeAlta keeps the choice for every page.
  let details = false;
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
    node.addEventListener('click', event => {
      event.preventDefault();
      event.stopPropagation();
      if (event.isTrusted && !node.disabled) action(event);
      // Clicked with the pointer, a button gives the focus back to the page: the keys then belong to the mode it turned
      // on, and Enter after a drawing opens its note instead of pressing Draw again. A button reached with the keyboard
      // keeps the focus.
      if (event.detail > 0 && shadow && shadow.activeElement === node) node.blur();
    });
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
    shadow = host.attachShadow({ mode: 'closed' });
    root = element('div', 'layer');
    root.setAttribute('popover', 'manual');
    shadow.appendChild(root);
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
    buildDrawTool();

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
    findModals();
    raise();
    updateCount();
  }

  // The layer of the bar is a popover of the top layer, above the modal dialogs, the popovers and the element in full
  // screen of the page, whatever their z-index; what the page shows in the top layer later goes above it, so it is
  // shown again then, last. A browser without popovers shows it as an element of the highest z-index.
  function raise() {
    if (!root || !host || typeof root.showPopover !== 'function') return;
    // Showing it again, or moving it, takes the focus from a note being written: it goes back where it was.
    const focused = shadow.activeElement;
    const selection = focused && typeof focused.selectionStart === 'number' ? [focused.selectionStart, focused.selectionEnd] : null;
    const home = homeOf();
    if (home && host.parentNode !== home) home.appendChild(host);
    watchHome(!!home && home !== document.documentElement);
    if (!host.isConnected) return;
    try {
      if (root.matches(':popover-open')) root.hidePopover();
      root.showPopover();
    } catch (_) { /* the host is being moved: the next change of the top layer shows it */ }
    if (focused && focused.isConnected && shadow.activeElement !== focused) {
      focused.focus({ preventScroll: true });
      if (selection) try { focused.setSelectionRange(selection[0], selection[1]); } catch (_) { /* not a field */ }
    }
  }

  // The modal dialogs of the page in the order they opened: the last one open makes the rest of the document inert,
  // the top layer included, so the bar could not be clicked nor its note written. While one is open, the host of the
  // bar goes in it (the layer stays in the top layer, where the dialog does not clip it), and back to the root of the
  // document once none is.
  const modals = [];
  let homeWatcher = null;

  // The modal dialogs already open when the bar is built: the page may have opened them before this script came to a
  // tab already open, so no toggle event told of them. Their order of opening is unknown; that of the document is used.
  function findModals() {
    for (const dialog of document.querySelectorAll('dialog')) {
      let modal = false;
      try { modal = dialog.matches(':modal'); } catch (_) { /* no :modal */ }
      if (modal && !modals.includes(dialog)) modals.push(dialog);
    }
  }

  function homeOf() {
    for (let index = modals.length - 1; index >= 0; index--) {
      let open = false;
      try { open = modals[index].isConnected && modals[index].matches(':modal'); } catch (_) { /* no :modal */ }
      if (open) return modals[index];
      modals.splice(index, 1);
    }
    return document.documentElement;
  }

  // While the host is in a dialog, the page may take the dialog out of the document without closing it: the host then
  // goes back home.
  function watchHome(on) {
    if (!on) { if (homeWatcher) { homeWatcher.disconnect(); homeWatcher = null; } return; }
    if (homeWatcher || typeof MutationObserver !== 'function') return;
    homeWatcher = new MutationObserver(() => { if (host && (!host.isConnected || host.parentNode !== homeOf())) raise(); });
    homeWatcher.observe(document, { childList: true, subtree: true });
  }

  // Popovers and dialogs of the page say when they open and close, and the document when an element goes in or out of
  // full screen.
  window.addEventListener('toggle', event => {
    if (ours(event)) return;
    const target = event.target;
    const dialog = target && target.localName === 'dialog';
    if (dialog && event.newState === 'open' && !modals.includes(target)) {
      let modal = false;
      try { modal = target.matches(':modal'); } catch (_) { /* no :modal */ }
      if (modal) modals.push(target);
    }
    if (event.newState === 'open' || dialog) raise();
  }, true);
  window.addEventListener('close', event => { if (event.target && event.target.localName === 'dialog') raise(); }, true);
  document.addEventListener('fullscreenchange', () => raise(), true);

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
    // Annotate and Draw both take the pointer: one at a time.
    if (picking) setDrawing(false);
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
    label.textContent = hoverLabel(target, rect) + (chosen.length ? '  ·  Shift+click: ' + (chosen.some(item => item.target === target) ? 'remove' : 'add') : '');
    placeLabel(rect);
  }

  // What the outline says of the element under the pointer: its tag with its stable id or classes, its role where the
  // tag does not say it, the start of its name or text, and its size.
  function hoverLabel(target, rect) {
    const parts = [selectorPart(target)];
    const role = roleOf(target);
    if (role && role !== target.localName) parts.push(role);
    const text = hoverText(target);
    if (text) parts.push('"' + text + '"');
    parts.push(Math.round(rect.width) + '×' + Math.round(rect.height));
    return parts.join(' · ');
  }

  // The start of the name or the text of an element, read only as far as the label shows: the element under the pointer
  // can be the whole page, whose text is long to gather at each move.
  function hoverText(target) {
    const LIMIT = 40;
    for (const attribute of ['aria-label', 'alt', 'title', 'placeholder']) {
      const value = squash(target.getAttribute(attribute));
      if (value) return clip(value, LIMIT);
    }
    let text = '';
    const walker = document.createTreeWalker(target, NodeFilter.SHOW_TEXT, {
      acceptNode: node => node.parentElement && /^(script|style|template|noscript)$/.test(node.parentElement.localName) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT,
    });
    for (let node = walker.nextNode(), count = 0; node && text.length <= LIMIT && count < 200; node = walker.nextNode(), count++) {
      text = squash(text + ' ' + node.data.slice(0, 2 * LIMIT));
    }
    return clip(text, LIMIT);
  }

  // The label goes above the outline, below it when the element starts at the top of the window, inside it when the
  // element fills the window; always inside the window.
  function placeLabel(rect) {
    Object.assign(label.style, { display: 'block', left: '0px', top: '0px' });
    const width = label.offsetWidth;
    const height = label.offsetHeight || 18;
    let top = rect.top - height - 3;
    if (top < 2) top = rect.bottom + 3 + height <= window.innerHeight - 2 ? rect.bottom + 3 : Math.max(2, rect.top + 2);
    const left = Math.max(4, Math.min(rect.left, window.innerWidth - width - 4));
    Object.assign(label.style, { left: left + 'px', top: Math.min(top, window.innerHeight - height - 2) + 'px' });
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
      let scanned = 0;
      for (const node of document.body.querySelectorAll('*')) {
        if (++scanned > MAX_AREA_SCAN) break;
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
    let node = pageElementAt((rect.left + rect.right) / 2, (rect.top + rect.bottom) / 2);
    while (node && node !== document.body && node !== document.documentElement) {
      const box = node.getBoundingClientRect();
      if (node !== host && box.left <= rect.left && box.top <= rect.top && box.right >= rect.right && box.bottom >= rect.bottom) return [node];
      node = node.parentElement;
    }
    return [];
  }

  // The element of the page at a point of the viewport, under what the bar shows there, such as the sheet of Draw; null
  // on the page itself.
  function pageElementAt(x, y) {
    for (const node of document.elementsFromPoint(x, y)) {
      if (node === host) continue;
      return node === document.body || node === document.documentElement ? null : node;
    }
    return null;
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
    imageSink = null;
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
    onKeys(field, event => {
      if (!event.isTrusted) return;
      if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) { event.preventDefault(); commit(field); }
      else if (event.key === 'Escape') { event.preventDefault(); closePopup(); }
    });
    setTimeout(() => { field.focus(); field.setSelectionRange(field.value.length, field.value.length); }, 0);
    return field;
  }

  // What the user wants of an annotation: a change of the code, or the answer to a question, which changes no code.
  const INTENTS = {
    change: { label: 'Change', title: 'Ask the agent to change it in the code', placeholder: 'What should change here?', hint: 'The agent changes the code.' },
    question: { label: 'Question', title: 'Ask the agent about it: it answers, and changes no code', placeholder: 'What do you want to know about it?', hint: 'The agent answers; it changes no code.' },
  };

  function intentOf(value) {
    return value === 'question' ? 'question' : 'change';
  }

  // The choice of a note between a change and a question, under its field, Change first: it changes the hint of the
  // field; the arrows switch it, and Ctrl+Enter saves from it as from the field. Gives what the user chose.
  function addIntentChoice(field, initial, commit) {
    let value = intentOf(initial);
    const row = element('div', 'choice');
    const group = element('span', 'intent');
    group.setAttribute('role', 'radiogroup');
    group.setAttribute('aria-label', 'What you want');
    const hint = element('span', 'hint');
    const buttons = {};
    const pick = next => {
      value = intentOf(next);
      for (const key of Object.keys(buttons)) {
        buttons[key].classList.toggle('on', key === value);
        buttons[key].setAttribute('aria-checked', String(key === value));
      }
      field.placeholder = INTENTS[value].placeholder;
      hint.textContent = INTENTS[value].hint;
    };
    for (const key of Object.keys(INTENTS)) {
      buttons[key] = button(INTENTS[key].label, '', INTENTS[key].title, () => pick(key));
      buttons[key].setAttribute('role', 'radio');
      group.appendChild(buttons[key]);
    }
    row.appendChild(group);
    row.appendChild(hint);
    onKeys(row, event => {
      if (!event.isTrusted) return;
      if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) { event.preventDefault(); commit(); }
      else if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') { event.preventDefault(); pick(value === 'change' ? 'question' : 'change'); buttons[value].focus(); }
      else if (event.key === 'Escape') { event.preventDefault(); closePopup(); }
    });
    field.after(row);
    pick(value);
    fitPopup();
    return () => value;
  }

  // A popup placed by its top, and grown since, stays inside the window.
  function fitPopup() {
    if (!popup || !popup.style.top) return;
    const rect = popup.getBoundingClientRect();
    if (rect.bottom > window.innerHeight - 8) popup.style.top = Math.max(8, window.innerHeight - 8 - rect.height) + 'px';
  }

  // The images of a note: a mockup of how it should look, a picture of another site. They come from the clipboard, where
  // text wins over an image as a copy from a page or a document often carries a picture of its text, or from a drop.
  const MAX_IMAGES = 4;
  const MAX_IMAGE_BYTES = 3 * 1024 * 1024;
  const MAX_IMAGE_SIDE = 2400;
  const IMAGE_TYPES = /^image\/(png|jpeg|gif|webp)$/;

  function readData(blob) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result));
      reader.onerror = () => reject(reader.error);
      reader.readAsDataURL(blob);
    });
  }

  // An image as the data URL the host takes: as it is when it is small enough, else smaller, as a JPEG; null when it
  // cannot be made small enough.
  async function imageData(file) {
    if (file.size <= MAX_IMAGE_BYTES) return await readData(file);
    if (typeof createImageBitmap !== 'function') return null;
    const bitmap = await createImageBitmap(file);
    const scale = Math.min(1, MAX_IMAGE_SIDE / Math.max(bitmap.width, bitmap.height));
    const canvas = document.createElement('canvas');
    canvas.width = Math.max(1, Math.round(bitmap.width * scale));
    canvas.height = Math.max(1, Math.round(bitmap.height * scale));
    const context = canvas.getContext('2d');
    context.fillStyle = '#fff';
    context.fillRect(0, 0, canvas.width, canvas.height);
    context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    if (bitmap.close) bitmap.close();
    for (const quality of [0.85, 0.7, 0.5]) {
      const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', quality));
      if (blob && blob.size <= MAX_IMAGE_BYTES) return await readData(blob);
    }
    return null;
  }

  function hasFiles(event) {
    return !!event.dataTransfer && [...event.dataTransfer.types].includes('Files');
  }

  const TYPING = new Set(['keydown', 'keypress', 'keyup', 'beforeinput', 'input', 'textInput', 'compositionstart', 'compositionupdate', 'compositionend']);

  // What the keys do in a field of the bar, or in a part of it, run by the guard of the window, which stops the keys
  // before they reach the bar: the nearest part of the bar holding the focus that has keys.
  const keyHandlers = new WeakMap();

  function onKeys(element, handler) {
    keyHandlers.set(element, handler);
  }

  function runKeys(event) {
    for (let node = shadow ? shadow.activeElement : null; node && node !== shadow; node = node.parentNode) {
      const handler = keyHandlers.get(node);
      if (handler) { handler(event); return true; }
    }
    return false;
  }

  // The keys of Annotate and Draw, pressed while a button of the bar has the focus, as after a click on Draw: they do
  // what they do over the page, but in a field, and Enter and Space still press the button.
  const modeKeys = [];

  function runModeKeys(event) {
    const active = shadow ? shadow.activeElement : null;
    if (active && /^(input|textarea|select)$/.test(active.localName)) return;
    if (active && active.localName === 'button' && (event.key === 'Enter' || event.key === ' ')) return;
    for (const handler of modeKeys) {
      if (event.defaultPrevented) return;
      handler(event, true);
    }
  }

  // The popup open whose note takes images, with its field and what takes them; null when none is.
  let imageSink = null;

  // A paste, a copy, a cut or a drag bound for the bar, stopped on the window before any listener of the page (see the
  // top of this script). The browser still pastes, copies and cuts words in the fields of the bar as it does. A picture
  // pasted into the field of a note, or files dropped on its popup, go to the note; files dragged anywhere else over the
  // bar are dropped nowhere. The closed shadow root hides from the window where in the bar the event is: the focus says
  // it of a paste, the pointer of a drag.
  function guard(event) {
    if (!ours(event)) return;
    event.stopImmediatePropagation();
    // A key or what it types: the browser still types it in the field, and the keys of the bar run from here.
    if (TYPING.has(event.type)) { if (event.type === 'keydown' && !runKeys(event)) runModeKeys(event); return; }
    const sink = imageSink && imageSink.box === popup ? imageSink : null;
    if (event.type === 'paste') {
      if (!sink || !event.isTrusted || shadow.activeElement !== sink.field) return;
      // Text wins over an image: a copy from a page or a document often carries a picture of its text.
      if (!event.clipboardData || event.clipboardData.getData('text/plain')) return;
      const files = [...event.clipboardData.items].filter(item => item.kind === 'file' && IMAGE_TYPES.test(item.type)).map(item => item.getAsFile());
      if (!files.length) return;
      event.preventDefault();
      sink.take(files);
      return;
    }
    // Words dragged into a field are dropped there by the browser, as they are typed.
    if (event.type === 'copy' || event.type === 'cut' || !hasFiles(event)) return;
    const rect = sink ? sink.box.getBoundingClientRect() : null;
    const over = !!rect && event.clientX >= rect.left && event.clientX < rect.right && event.clientY >= rect.top && event.clientY < rect.bottom;
    if (event.type === 'dragleave') {
      // Out of the bar: from the window, a node of the bar is its host.
      if (sink && event.relatedTarget !== host) sink.box.classList.remove('dropping');
      return;
    }
    event.preventDefault();
    if (sink) sink.box.classList.toggle('dropping', over && event.type !== 'drop');
    if (event.type === 'drop') { if (over && event.isTrusted) sink.take([...event.dataTransfer.files]); return; }
    event.dataTransfer.dropEffect = over ? 'copy' : 'none';
  }

  // The images of the note of a popup, under its field: those the annotation has, by the names the host gave them, and
  // those pasted or dropped since, kept here until the note is saved. Each can be removed.
  function addImages(field, existing) {
    const box = popup;
    const kept = (Array.isArray(existing) ? existing : []).filter(name => typeof name === 'string');
    const added = [];
    const waiting = [];
    let reading = 0;
    const strip = element('div', 'images');
    const render = () => {
      strip.textContent = '';
      kept.forEach((name, index) => {
        const chip = element('span', 'image');
        chip.appendChild(element('span', 'name', 'Image ' + (index + 1)));
        chip.appendChild(button('✕', 'small', 'Remove this image', () => { kept.splice(index, 1); render(); }));
        strip.appendChild(chip);
      });
      added.forEach((data, index) => {
        const chip = element('span', 'image');
        const picture = element('img');
        picture.alt = 'Image ' + (kept.length + index + 1);
        // A page whose policy forbids data: images shows its name instead.
        picture.addEventListener('error', () => picture.replaceWith(element('span', 'name', picture.alt)));
        picture.src = data;
        chip.appendChild(picture);
        chip.appendChild(button('✕', 'small', 'Remove this image', () => { added.splice(index, 1); render(); }));
        strip.appendChild(chip);
      });
      if (reading) strip.appendChild(element('span', 'name', 'Reading the image…'));
      strip.classList.toggle('hidden', !strip.childNodes.length);
      fitPopup();
    };
    const take = files => {
      for (const file of files) {
        if (!file || !IMAGE_TYPES.test(file.type)) continue;
        if (kept.length + added.length + reading >= MAX_IMAGES) { showNotice('A note takes ' + MAX_IMAGES + ' images at most.'); break; }
        reading++;
        imageData(file)
          .then(data => { if (data && box === popup) added.push(data); else if (!data) showNotice('This image is too large for a note, even made smaller.'); },
            () => showNotice('CodeAlta could not read this image.'))
          .finally(() => {
            reading--;
            render();
            if (!reading) for (const action of waiting.splice(0)) action();
          });
      }
      render();
    };
    // The guard of the window hands over the pastes and the drops of the note, which the page never sees.
    imageSink = { box: box, field: field, take: take };
    field.title = 'Paste or drop an image to show what you mean';
    field.after(strip);
    render();
    return {
      // The names of the images the annotation keeps, and the images to send.
      kept: () => kept.slice(),
      added: () => added.slice(),
      // Runs an action once the images being read are read, and only while its popup is open: the popup is the token of
      // its own life, so a note saved and then cancelled, or replaced by another one, while an image was read is not
      // saved later, nor does it close the newer popup.
      ready: action => {
        const run = () => { if (box === popup) action(); };
        if (reading) waiting.push(run); else run();
      },
    };
  }

  // Sends the images pasted into the note of an annotation, after the annotation or its change.
  function sendImages(id, images) {
    for (const data of images.added()) send({ type: 'image', id: id, data: data });
  }

  // Shows on the mark of an annotation whether it is a question.
  function setMarkIntent(id, value) {
    const mark = marks.get(id);
    if (!mark) return;
    mark.intent = intentOf(value);
    mark.badge.classList.toggle('question', mark.intent === 'question');
  }

  // The note of a new annotation, on the elements chosen or the area drawn.
  function openNote(choice) {
    if (!choice.targets.length && !choice.area) return;
    hideOutline();
    closePanel();
    const area = choice.area || areaOf(choice.targets);
    let field = null;
    let intent = null;
    let images = null;
    let saved = false;
    const commit = () => {
      const text = field.value.trim();
      if (!text) { field.focus(); return; }
      if (saved) return;
      saved = true;
      // An image still being read goes with the note.
      images.ready(() => store(text));
    };
    const store = text => {
      const id = 'a' + Date.now().toString(36) + '-' + (nextId++);
      addMark(id, choice.targets, choice.area, null, text);
      setMarkIntent(id, intent());
      send({ type: 'annotation', id: id, note: text, intent: intent(), url: location.href, elements: choice.targets.map(describe), area: area });
      sendImages(id, images);
      clearChosen();
      closePopup();
    };
    const cancel = button('Cancel', '', '', closePopup);
    const save = button('Save', 'primary', 'Save the note (Ctrl+Enter)', commit);
    field = openPopup(describeChoice(choice), '', 'What should change here?', viewOf(area), ['grow', cancel, save], commit);
    intent = addIntentChoice(field, 'change', commit);
    images = addImages(field, []);
  }

  // The note of an annotation waiting: changed, or the annotation removed.
  function openEdit(id) {
    const item = (items || []).find(candidate => candidate.id === id);
    const mark = marks.get(id);
    const note = item ? item.note : mark ? mark.note : '';
    const number = item ? item.number : mark ? mark.number : null;
    const title = (number == null ? '' : '#' + number + ' · ') + (item && item.label ? item.label : 'Annotation');
    let field = null;
    let intent = null;
    let images = null;
    let saved = false;
    const commit = () => {
      const text = field.value.trim();
      if (!text) { field.focus(); return; }
      if (saved) return;
      saved = true;
      images.ready(() => store(text));
    };
    const store = text => {
      if (mark) { mark.note = text; mark.badge.title = text; }
      if (item) { item.note = text; item.intent = intent(); }
      setMarkIntent(id, intent());
      // The images the annotation keeps, when the host listed them: without the list, it keeps them all.
      send({ type: 'edit', id: id, note: text, intent: intent(), images: item ? images.kept() : undefined });
      sendImages(id, images);
      closePopup();
      renderPanel();
    };
    const remove = button('Remove', '', 'Remove this annotation', () => { forget(id); closePopup(); });
    const cancel = button('Cancel', '', '', closePopup);
    const save = button('Save', 'primary', 'Save the note (Ctrl+Enter)', commit);
    closePanel();
    field = openPopup(title, note, 'What should change here?', mark ? markView(mark) : null, [remove, 'grow', cancel, save], commit);
    intent = addIntentChoice(field, item ? item.intent : mark ? mark.intent : 'change', commit);
    images = addImages(field, item ? item.images : []);
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
    popup.insertBefore(detailsSwitch(), popup.lastChild);
    placeNearBar(popup);
  }

  // The switch of the Send popup that adds the details of the page to the prompt: the HTML, computed styles, ancestors,
  // nearby elements, bounds and viewport of each element. CodeAlta keeps the choice for every page, and for the prompts
  // written in CodeAlta.
  function detailsSwitch() {
    const row = element('label', 'switch');
    row.title = 'Adds the HTML, computed styles, ancestors, nearby elements, bounds and viewport of each element: for questions of layout. Kept for every page.';
    const box = element('input');
    box.type = 'checkbox';
    box.checked = details;
    box.addEventListener('change', event => {
      if (!event.isTrusted) { box.checked = details; return; }
      details = box.checked;
      send({ type: 'details', on: details });
    });
    row.appendChild(box);
    row.appendChild(element('span', '', 'Include page details'));
    return row;
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
    const list = items || [...marks.entries()].map(([id, mark]) => ({ id: id, number: mark.number, label: '', note: mark.note, intent: mark.intent, url: location.href }));
    panel.appendChild(element('div', 'title', list.length ? 'Annotations waiting (' + list.length + ')' : 'Annotations waiting'));
    if (!list.length) panel.appendChild(element('div', 'empty', 'None yet: turn on Annotate, then click an element, Shift+click several, or drag over an area.'));
    for (const item of list) {
      const row = element('div', 'item');
      row.appendChild(element('span', 'number' + (item.intent === 'question' ? ' question' : ''), item.number == null ? '…' : String(item.number)));
      const text = element('div', 'text');
      if (item.label) text.appendChild(element('div', 'what', item.label));
      text.appendChild(element('div', 'note', item.note));
      const pictures = Array.isArray(item.images) ? item.images.length : 0;
      if (pictures) text.appendChild(element('div', 'where', pictures === 1 ? '1 image' : pictures + ' images'));
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

  const annotateKeys = (event, fromBar) => {
    if (!event.isTrusted || (ours(event) && !fromBar)) return;
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
  };
  window.addEventListener('keydown', annotateKeys, true);
  modeKeys.push(annotateKeys);

  // ---------------------------------------------------------------------------------------------------------------
  // Draw: the user draws over the page to show what they mean, then saves the drawing with a note
  //
  // While Draw is on, a sheet over the viewport takes the pointer, and the page gets none of what draws. The shapes
  // are kept in pixels of the document, so that they stay on what they were drawn over when the page scrolls. Saved,
  // the drawing is an annotation of its own kind: the bar hides everything of its own but the drawing, the host takes
  // the picture of that part of the page with the drawing on it, then the drawing leaves the page and its mark stays.
  // What is under the drawing is not looked for: the picture shows it.
  // ---------------------------------------------------------------------------------------------------------------

  const SVG = 'http://www.w3.org/2000/svg';
  // U+FE0E keeps the symbols as text, not as colored emoji.
  const DRAW_TOOLS = [
    { name: 'pen', key: 'p', icon: '✎︎', title: 'Pen (P)' },
    { name: 'highlighter', key: 'h', icon: '▮︎', title: 'Highlighter (H)' },
    { name: 'arrow', key: 'a', icon: '↗︎', title: 'Arrow (A)' },
    { name: 'rect', key: 'r', icon: '▭︎', title: 'Rectangle (R)' },
    { name: 'ellipse', key: 'o', icon: '◯︎', title: 'Ellipse (O)' },
    { name: 'text', key: 't', icon: 'T', title: 'Text (T): click where it goes, type it, then Enter' },
    { name: 'eraser', key: 'e', icon: '⌫︎', title: 'Eraser (E): click or drag over shapes to remove them' },
  ];
  const DRAW_COLORS = [
    { value: '#ef4444', name: 'Red' }, { value: '#f59e0b', name: 'Amber' }, { value: '#22c55e', name: 'Green' },
    { value: '#3b82f6', name: 'Blue' }, { value: '#111827', name: 'Black' },
  ];
  const STROKE = { pen: 3, highlighter: 16, arrow: 3, rect: 3, ellipse: 3 };
  const TEXT_SIZE = 18;
  const MAX_SHAPES = 200;
  const MAX_POINTS = 2000;
  // The room around the drawing in its picture, and how long the bar waits for the host to take it.
  const DRAW_MARGIN = 24;
  const PICTURE_WAIT = 5000;

  const DRAW_STYLE = `
    .sheet { position: fixed; left: 0; top: 0; right: 0; bottom: 0; z-index: 2147483644; pointer-events: auto; cursor: crosshair; touch-action: none; }
    .sheet[data-tool="text"] { cursor: text; }
    .sheet[data-tool="eraser"] { cursor: cell; }
    .sheet svg { position: absolute; left: 0; top: 0; width: 100%; height: 100%; overflow: visible; }
    .draw-controls { display: inline-flex; align-items: center; gap: 3px; }
    /* While Draw is on, its controls take a row of their own under the bar, which stays narrow enough for small windows. */
    .bar.drawing { flex-wrap: wrap; justify-content: flex-end; border-radius: 16px; max-width: calc(100vw - 16px); }
    .bar.drawing.collapsed { width: auto !important; }
    .bar.drawing .draw-controls { order: 1; flex-basis: 100%; justify-content: flex-end; flex-wrap: wrap; padding-top: 3px; }
    .draw-controls button.icon { min-width: 26px; padding: 3px 5px; }
    button.swatch { width: 16px; height: 16px; padding: 0; margin: 0 1px; border-radius: 50%; box-shadow: inset 0 0 0 1px rgba(255,255,255,.4); }
    button.swatch.picked { outline: 2px solid var(--text); outline-offset: 2px; }
    .sketch-text { all: unset; position: fixed; z-index: 2147483647; min-width: 140px; padding: 1px 4px; border-radius: 4px;
      font: 600 ${TEXT_SIZE}px/1.2 system-ui, -apple-system, "Segoe UI", sans-serif; background: rgba(255,255,255,.92); border: 1px dashed currentColor; }
  `;

  // The drawing being made: its shapes, the lists of shapes before (for Undo) and after (for Redo), the shape or the
  // erasing the pointer is doing, the text being typed, and the drawing whose picture the host is taking.
  const sketch = { on: false, tool: 'pen', color: DRAW_COLORS[0].value, shapes: [], past: [], future: [], stroke: null, text: null, saving: null };
  let sheet = null;
  let sheetSvg = null;
  let drawToggle = null;
  let drawControls = null;
  let undoButton = null;
  let clearButton = null;
  let drawSave = null;

  // The Draw tool of the bar, after Annotate: its button, then the controls it shows while it is on.
  function buildDrawTool() {
    const style = element('style');
    style.textContent = DRAW_STYLE;
    root.appendChild(style);
    drawToggle = button('Draw', '', 'Draw on the page to show what you mean, then save the drawing with a note (Esc ends)', () => setDrawing(!sketch.on));
    drawControls = element('span', 'draw-controls hidden');
    for (const tool of DRAW_TOOLS) {
      const control = button(tool.icon, 'icon', tool.title, () => { sketch.tool = tool.name; commitText(); updateDrawControls(); });
      control.dataset.drawTool = tool.name;
      control.setAttribute('aria-label', tool.title);
      drawControls.appendChild(control);
    }
    for (const color of DRAW_COLORS) {
      const swatch = button('', 'swatch', color.name + ' (' + (DRAW_COLORS.indexOf(color) + 1) + ')', () => { sketch.color = color.value; updateDrawControls(); });
      swatch.style.background = color.value;
      swatch.dataset.color = color.value;
      swatch.setAttribute('aria-label', color.name);
      drawControls.appendChild(swatch);
    }
    undoButton = button('↶︎', 'icon', 'Undo (Ctrl+Z; Ctrl+Shift+Z redoes)', () => undoSketch(false));
    clearButton = button('⌧︎', 'icon', 'Clear the drawing', clearSketch);
    drawSave = button('Save', 'primary', 'Save the drawing with a note (Enter)', openDrawingNote);
    drawControls.appendChild(undoButton);
    drawControls.appendChild(clearButton);
    drawControls.appendChild(drawSave);
    addTool('draw', [drawToggle, drawControls]);

    sheet = element('div', 'sheet hidden');
    sheetSvg = document.createElementNS(SVG, 'svg');
    sheet.appendChild(sheetSvg);
    root.appendChild(sheet);
    // A bar built again (the page removed it) starts with Draw off; a drawing not saved shows again when it is on.
    sketch.on = false;
    renderSketch();
    updateDrawControls();
  }

  function setDrawing(on) {
    on = !!on && enabled && !!sheet;
    if (on === sketch.on) return;
    if (on) setPicking(false);
    if (!on) { commitText(); sketch.stroke = null; }
    sketch.on = on;
    // A drawing not saved stays, hidden, until Draw is on again.
    if (!sketch.saving) sheet.classList.toggle('hidden', !on);
    drawToggle.classList.toggle('on', on);
    bar.style.width = '';
    const row = bar.offsetWidth;
    drawControls.classList.toggle('hidden', !on);
    bar.classList.toggle('drawing', on);
    if (on) {
      // A bar that wraps would take the whole width of the window: it takes the width of its row, or of the controls
      // when they are wider.
      let controls = 0;
      for (const control of drawControls.children) controls += control.offsetWidth + 4;
      bar.style.width = Math.ceil(Math.max(row, controls + 24)) + 'px';
    }
    renderSketch();
    updateDrawControls();
    placeBar();
  }

  function updateDrawControls() {
    if (!drawControls) return;
    for (const control of drawControls.children) {
      if (control.dataset.drawTool) control.classList.toggle('on', control.dataset.drawTool === sketch.tool);
      if (control.dataset.color) control.classList.toggle('picked', control.dataset.color === sketch.color);
    }
    undoButton.disabled = !sketch.past.length || !!sketch.saving;
    clearButton.disabled = !sketch.shapes.length || !!sketch.saving;
    drawSave.disabled = !sketch.shapes.length || !!sketch.saving;
    sheet.dataset.tool = sketch.tool;
  }

  // Each change keeps the shapes before it, as Undo gives them back.
  function changeShapes(shapes) {
    sketch.past.push(sketch.shapes);
    if (sketch.past.length > MAX_SHAPES) sketch.past.shift();
    sketch.future = [];
    sketch.shapes = shapes;
    renderSketch();
    updateDrawControls();
  }

  function undoSketch(redo) {
    if (sketch.saving) return;
    commitText();
    const from = redo ? sketch.future : sketch.past;
    const to = redo ? sketch.past : sketch.future;
    if (!from.length) return;
    if (redo) to.push(sketch.shapes); else to.unshift(sketch.shapes);
    sketch.shapes = redo ? from.shift() : from.pop();
    renderSketch();
    updateDrawControls();
  }

  function clearSketch() {
    commitText();
    if (sketch.shapes.length && !sketch.saving) changeShapes([]);
  }

  // ---- Shapes, in pixels of the document ----

  function pagePoint(event) {
    return { x: event.clientX + window.scrollX, y: event.clientY + window.scrollY };
  }

  function distance(a, b) {
    return Math.hypot(a.x - b.x, a.y - b.y);
  }

  function segmentDistance(point, a, b) {
    const length = (b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y);
    if (!length) return distance(point, a);
    const t = Math.max(0, Math.min(1, ((point.x - a.x) * (b.x - a.x) + (point.y - a.y) * (b.y - a.y)) / length));
    return distance(point, { x: a.x + t * (b.x - a.x), y: a.y + t * (b.y - a.y) });
  }

  // The box of a shape, its stroke included; the width of a text is measured once it is drawn.
  function shapeBox(shape) {
    if (shape.kind === 'text') {
      const at = shape.points[0];
      const width = shape.width || shape.text.length * TEXT_SIZE * 0.6;
      return { left: at.x, top: at.y, right: at.x + width, bottom: at.y + TEXT_SIZE * 1.3 };
    }
    let left = Infinity, top = Infinity, right = -Infinity, bottom = -Infinity;
    for (const point of shape.points) {
      left = Math.min(left, point.x); top = Math.min(top, point.y); right = Math.max(right, point.x); bottom = Math.max(bottom, point.y);
    }
    // An arrow's head reaches past its line.
    const pad = shape.kind === 'arrow' ? 14 : STROKE[shape.kind] / 2 + 1;
    return { left: left - pad, top: top - pad, right: right + pad, bottom: bottom + pad };
  }

  // Whether a point is on a shape: on its line, or on the outline of a rectangle or an ellipse (what is inside them is
  // the page, not the shape), or in the box of a text.
  function shapeHit(shape, point, tolerance) {
    const reach = tolerance + (STROKE[shape.kind] || 0) / 2;
    const points = shape.points;
    if (shape.kind === 'text') {
      const box = shapeBox(shape);
      return point.x >= box.left - tolerance && point.x <= box.right + tolerance && point.y >= box.top - tolerance && point.y <= box.bottom + tolerance;
    }
    if (shape.kind === 'rect') {
      const left = Math.min(points[0].x, points[1].x), right = Math.max(points[0].x, points[1].x);
      const top = Math.min(points[0].y, points[1].y), bottom = Math.max(points[0].y, points[1].y);
      const corners = [{ x: left, y: top }, { x: right, y: top }, { x: right, y: bottom }, { x: left, y: bottom }];
      return corners.some((corner, index) => segmentDistance(point, corner, corners[(index + 1) % 4]) <= reach);
    }
    if (shape.kind === 'ellipse') {
      const cx = (points[0].x + points[1].x) / 2, cy = (points[0].y + points[1].y) / 2;
      const rx = Math.max(1, Math.abs(points[1].x - points[0].x) / 2), ry = Math.max(1, Math.abs(points[1].y - points[0].y) / 2);
      const radial = Math.hypot((point.x - cx) / rx, (point.y - cy) / ry);
      return Math.abs(radial - 1) * Math.min(rx, ry) <= reach;
    }
    if (points.length === 1) return distance(point, points[0]) <= reach;
    for (let index = 1; index < points.length; index++) if (segmentDistance(point, points[index - 1], points[index]) <= reach) return true;
    return false;
  }

  // A shape too small to mean anything is not kept: a click with the rectangle tool draws no rectangle.
  function worthKeeping(shape) {
    if (shape.kind === 'pen' || shape.kind === 'highlighter') return shape.points.length > 0;
    return shape.points.length === 2 && distance(shape.points[0], shape.points[1]) >= 4;
  }

  function svgNode(tag, attributes) {
    const node = document.createElementNS(SVG, tag);
    for (const name of Object.keys(attributes)) node.setAttribute(name, String(attributes[name]));
    return node;
  }

  function shapeNode(shape) {
    const points = shape.points;
    const stroke = { stroke: shape.color, 'stroke-width': STROKE[shape.kind], fill: 'none', 'stroke-linecap': 'round', 'stroke-linejoin': 'round' };
    switch (shape.kind) {
      case 'pen':
      case 'highlighter': {
        // One point is a dot: a line of no length with round ends.
        const path = points.map((point, index) => (index ? 'L' : 'M') + point.x + ' ' + point.y).join(' ') + (points.length === 1 ? ' l 0.01 0' : '');
        const node = svgNode('path', Object.assign(stroke, { d: path }));
        if (shape.kind === 'highlighter') node.setAttribute('stroke-opacity', '0.38');
        return node;
      }
      case 'arrow': {
        const [from, to] = points;
        const angle = Math.atan2(to.y - from.y, to.x - from.x);
        const head = (turn) => (to.x - 14 * Math.cos(angle + turn)) + ' ' + (to.y - 14 * Math.sin(angle + turn));
        const group = svgNode('g', {});
        group.appendChild(svgNode('line', Object.assign({}, stroke, { x1: from.x, y1: from.y, x2: to.x, y2: to.y })));
        group.appendChild(svgNode('path', { d: 'M' + to.x + ' ' + to.y + ' L' + head(0.45) + ' L' + head(-0.45) + ' Z', fill: shape.color, stroke: shape.color, 'stroke-width': 2, 'stroke-linejoin': 'round' }));
        return group;
      }
      case 'rect':
        return svgNode('rect', Object.assign(stroke, {
          x: Math.min(points[0].x, points[1].x), y: Math.min(points[0].y, points[1].y),
          width: Math.abs(points[1].x - points[0].x), height: Math.abs(points[1].y - points[0].y), rx: 3,
        }));
      case 'ellipse':
        return svgNode('ellipse', Object.assign(stroke, {
          cx: (points[0].x + points[1].x) / 2, cy: (points[0].y + points[1].y) / 2,
          rx: Math.abs(points[1].x - points[0].x) / 2, ry: Math.abs(points[1].y - points[0].y) / 2,
        }));
      default: {
        // A light halo keeps the text readable on any page.
        const node = svgNode('text', {
          x: points[0].x, y: points[0].y + TEXT_SIZE, fill: shape.color, stroke: '#ffffff', 'stroke-width': 3, 'paint-order': 'stroke',
          'font-family': 'system-ui, -apple-system, "Segoe UI", sans-serif', 'font-size': TEXT_SIZE, 'font-weight': 600,
        });
        node.textContent = shape.text;
        return node;
      }
    }
  }

  // Draws the shapes where the page is scrolled to now.
  function renderSketch() {
    if (!sheetSvg) return;
    sheetSvg.textContent = '';
    const group = svgNode('g', { transform: 'translate(' + -window.scrollX + ' ' + -window.scrollY + ')' });
    const shapes = sketch.stroke && sketch.stroke.shape ? sketch.shapes.concat([sketch.stroke.shape]) : sketch.shapes;
    for (const shape of shapes) {
      const node = shapeNode(shape);
      group.appendChild(node);
    }
    sheetSvg.appendChild(group);
    // The width of a text, as the browser draws it, for the eraser and the bounds of the picture.
    for (const node of group.querySelectorAll('text')) {
      const shape = shapes[[...group.children].indexOf(node)];
      if (shape && !shape.width) { try { shape.width = node.getComputedTextLength(); } catch (_) { /* not laid out */ } }
    }
  }

  window.addEventListener('scroll', () => { if (sketch.shapes.length || sketch.stroke) renderSketch(); }, { passive: true });

  // ---- Text ----

  function openText(at) {
    commitText();
    const field = element('input', 'sketch-text');
    field.type = 'text';
    field.maxLength = 200;
    field.placeholder = 'Text, then Enter';
    field.style.color = sketch.color;
    field.style.left = (at.x - window.scrollX) + 'px';
    field.style.top = (at.y - window.scrollY) + 'px';
    onKeys(field, event => {
      if (!event.isTrusted) return;
      if (event.key === 'Enter') { event.preventDefault(); commitText(); }
      else if (event.key === 'Escape') { event.preventDefault(); cancelText(); }
    });
    field.addEventListener('blur', event => { if (event.isTrusted) commitText(); });
    root.appendChild(field);
    sketch.text = { at: at, field: field, color: sketch.color };
    setTimeout(() => field.focus(), 0);
  }

  function commitText() {
    const text = sketch.text;
    if (!text) return;
    sketch.text = null;
    const value = squash(text.field.value);
    text.field.remove();
    if (value && sketch.shapes.length < MAX_SHAPES) changeShapes(sketch.shapes.concat([{ kind: 'text', color: text.color, points: [text.at], text: value }]));
  }

  function cancelText() {
    const text = sketch.text;
    if (!text) return;
    sketch.text = null;
    text.field.remove();
  }

  // ---- Pointer and keys while drawing: only what the user does draws ----

  // An event of the user on the sheet: the page cannot reach the sheet, nor make up an event the browser trusts. Seen
  // from the window, an event inside the closed shadow root is at its host; the root itself says what is under the
  // pointer, and the pointer that draws stays on the sheet wherever it goes.
  function onSheet(event) {
    if (!sketch.on || !event.isTrusted || !sheet || !ours(event)) return false;
    if (sketch.stroke && event.pointerId === sketch.stroke.id) return true;
    // The shadow root says what is under the pointer; the layer that holds the parts of the bar is an element.
    const under = (shadow || root).elementFromPoint(event.clientX, event.clientY);
    return !!under && sheet.contains(under);
  }

  // The page gets nothing of what draws.
  function keep(event) {
    event.preventDefault();
    event.stopImmediatePropagation();
  }

  function eraseAt(point) {
    const left = sketch.shapes.filter(shape => !shapeHit(shape, point, 6));
    if (left.length !== sketch.shapes.length) { sketch.shapes = left; renderSketch(); }
  }

  window.addEventListener('pointerdown', event => {
    if (!onSheet(event)) return;
    keep(event);
    if (event.button !== 0 || popup || sketch.saving || sketch.stroke) return;
    const at = pagePoint(event);
    if (sketch.tool === 'text') { openText(at); return; }
    commitText();
    try { sheet.setPointerCapture(event.pointerId); } catch (_) { /* the pointer is gone */ }
    if (sketch.tool === 'eraser') {
      sketch.stroke = { id: event.pointerId, erase: true, before: sketch.shapes };
      eraseAt(at);
      return;
    }
    if (sketch.shapes.length >= MAX_SHAPES) return;
    const points = sketch.tool === 'pen' || sketch.tool === 'highlighter' ? [at] : [at, at];
    sketch.stroke = { id: event.pointerId, shape: { kind: sketch.tool, color: sketch.color, points: points } };
    renderSketch();
  }, true);

  window.addEventListener('pointermove', event => {
    if (!onSheet(event)) return;
    keep(event);
    const stroke = sketch.stroke;
    if (!stroke || event.pointerId !== stroke.id) return;
    const at = pagePoint(event);
    if (stroke.erase) { eraseAt(at); return; }
    const points = stroke.shape.points;
    if (points.length === 2 && stroke.shape.kind !== 'pen' && stroke.shape.kind !== 'highlighter') points[1] = at;
    else if (points.length < MAX_POINTS && distance(points[points.length - 1], at) >= 1.5) points.push(at);
    else return;
    renderSketch();
  }, true);

  function endStroke(event) {
    if (!onSheet(event)) return;
    keep(event);
    const stroke = sketch.stroke;
    if (!stroke || event.pointerId !== stroke.id) return;
    sketch.stroke = null;
    if (stroke.erase) {
      // The whole erasing is one step to undo.
      if (sketch.shapes !== stroke.before) {
        const shapes = sketch.shapes;
        sketch.shapes = stroke.before;
        changeShapes(shapes);
      }
      return;
    }
    if (event.type === 'pointerup' && worthKeeping(stroke.shape)) changeShapes(sketch.shapes.concat([stroke.shape]));
    else renderSketch();
  }

  window.addEventListener('pointerup', endStroke, true);
  window.addEventListener('pointercancel', endStroke, true);
  for (const type of ['mousedown', 'mouseup', 'click', 'dblclick', 'contextmenu', 'auxclick', 'touchstart', 'touchmove', 'touchend']) {
    window.addEventListener(type, event => { if (onSheet(event)) keep(event); }, true);
  }

  // After the keys of Annotate: Esc closes the note first, then the text being typed (in its field), then leaves Draw.
  // Enter opens the note of the drawing, Ctrl+Z undoes, Ctrl+Shift+Z or Ctrl+Y redoes, a letter or a digit picks a tool or a color.
  const drawKeys = (event, fromBar) => {
    if (!sketch.on || !event.isTrusted || (ours(event) && !fromBar) || popup || sketch.saving) return;
    const key = event.key.toLowerCase();
    if (event.key === 'Escape') setDrawing(false);
    else if ((event.metaKey || event.ctrlKey) && key === 'z') undoSketch(event.shiftKey);
    else if ((event.metaKey || event.ctrlKey) && key === 'y') undoSketch(true);
    else if (event.key === 'Enter' && sketch.shapes.length) openDrawingNote();
    else if (!event.metaKey && !event.ctrlKey && !event.altKey && pickByKey(key)) { /* a tool or a color */ }
    else return;
    keep(event);
  };
  window.addEventListener('keydown', drawKeys, true);
  modeKeys.push(drawKeys);

  // A letter picks a tool, a digit a color, as their buttons say.
  function pickByKey(key) {
    const tool = DRAW_TOOLS.find(candidate => candidate.key === key);
    const color = /^[1-9]$/.test(key) ? DRAW_COLORS[+key - 1] : null;
    if (!tool && !color) return false;
    if (tool) { commitText(); sketch.tool = tool.name; }
    if (color) sketch.color = color.value;
    updateDrawControls();
    return true;
  }

  // ---- Saving ----

  // The part of the page the picture shows: the drawing with some room around it, inside the viewport, since only the
  // viewport shows the drawing. A drawing that fits in the viewport is first scrolled into it.
  function pictureArea() {
    let left = Infinity, top = Infinity, right = -Infinity, bottom = -Infinity;
    for (const shape of sketch.shapes) {
      const box = shapeBox(shape);
      left = Math.min(left, box.left); top = Math.min(top, box.top); right = Math.max(right, box.right); bottom = Math.max(bottom, box.bottom);
    }
    if (!isFinite(left)) return null;
    left -= DRAW_MARGIN; top -= DRAW_MARGIN; right += DRAW_MARGIN; bottom += DRAW_MARGIN;
    const width = Math.min(window.innerWidth, document.documentElement.clientWidth || window.innerWidth);
    const height = Math.min(window.innerHeight, document.documentElement.clientHeight || window.innerHeight);
    let x = window.scrollX, y = window.scrollY;
    if (left < x || right > x + width || top < y || bottom > y + height) {
      if (right - left <= width) x = Math.max(0, Math.min(left, right - width));
      if (bottom - top <= height) y = Math.max(0, Math.min(top, bottom - height));
      if (x !== window.scrollX || y !== window.scrollY) {
        window.scrollTo({ left: x, top: y, behavior: 'instant' });
        x = window.scrollX; y = window.scrollY;
        renderSketch();
      }
    }
    const area = { x: Math.max(left, x), y: Math.max(top, y), right: Math.min(right, x + width), bottom: Math.min(bottom, y + height) };
    if (area.right - area.x < 1 || area.bottom - area.y < 1) return null;
    return { x: area.x, y: area.y, width: area.right - area.x, height: area.bottom - area.y, drawn: true };
  }

  function openDrawingNote() {
    commitText();
    if (!sketch.shapes.length || sketch.saving) return;
    closePanel();
    const area = pictureArea();
    if (!area) return;
    let field = null;
    let intent = null;
    let images = null;
    let saved = false;
    const commit = () => {
      const text = field.value.trim();
      if (!text) { field.focus(); return; }
      if (saved) return;
      saved = true;
      // An image still being read goes with the drawing.
      images.ready(() => {
        const chosen = intent();
        const added = images.added();
        closePopup();
        saveDrawing(text, chosen, added);
      });
    };
    const cancel = button('Cancel', '', '', closePopup);
    const save = button('Save', 'primary', 'Save the drawing with the note (Ctrl+Enter)', commit);
    const count = sketch.shapes.length;
    field = openPopup('Drawing · ' + count + (count === 1 ? ' shape' : ' shapes'), '', 'What should change here?', viewOf(area), ['grow', cancel, save], commit);
    intent = addIntentChoice(field, 'change', commit);
    images = addImages(field, []);
  }

  // Shows only the drawing over the page and asks the host for its picture; the host answers with drawn(id).
  function saveDrawing(note, intent, images) {
    const area = pictureArea();
    if (!area) return;
    const id = 'd' + Date.now().toString(36) + '-' + (nextId++);
    const targets = drawingTargets();
    addMark(id, targets, area, null, note);
    setMarkIntent(id, intent);
    // The marks of the other annotations stay in the picture: the agent sees which of them the drawing is about.
    const others = new Set();
    for (const [other, mark] of marks) {
      if (other === id) continue;
      for (const box of mark.boxes) others.add(box);
      others.add(mark.badge);
    }
    const hidden = [];
    for (const node of root.children) {
      if (node === sheet || others.has(node) || node.localName === 'style' || node.style.visibility === 'hidden') continue;
      node.style.visibility = 'hidden';
      hidden.push(node);
    }
    sheet.classList.remove('hidden');
    sketch.saving = { id: id, hidden: hidden, timer: setTimeout(() => finishDrawing(id), PICTURE_WAIT) };
    updateDrawControls();
    // The picture is taken once the browser painted the page without the bar.
    requestAnimationFrame(() => requestAnimationFrame(() => {
      send({ type: 'drawing', id: id, note: note, intent: intentOf(intent), url: location.href, area: area, elements: targets.map(describe) });
      for (const data of images || []) send({ type: 'image', id: id, data: data });
    }));
  }

  // The elements a drawing is about, as the Annotate tool would choose them: those a rectangle, an ellipse or a stroke
  // surrounds, the element an arrow points at, the element a text is written on. With them, the prompt says where in
  // the code is what the drawing shows. In the order the shapes were drawn, each once, at most as many as a note
  // covers: an arrow at a button of a card a rectangle surrounds names both, the card and the button it means.
  function drawingTargets() {
    const found = [];
    const add = node => {
      if (!node || node === host || found.length >= MAX_ELEMENTS || found.includes(node)) return;
      found.push(node);
    };
    for (const shape of sketch.shapes) {
      const box = shapeBox(shape);
      if (shape.kind === 'arrow') {
        const head = shape.points[shape.points.length - 1];
        add(pageElementAt(head.x - window.scrollX, head.y - window.scrollY));
      } else if (shape.kind === 'text') {
        add(pageElementAt((box.left + box.right) / 2 - window.scrollX, (box.top + box.bottom) / 2 - window.scrollY));
      } else {
        const view = { left: box.left - window.scrollX, top: box.top - window.scrollY, right: box.right - window.scrollX, bottom: box.bottom - window.scrollY };
        for (const node of elementsIn(view)) add(node);
      }
    }
    return found;
  }

  // The picture is taken, or the host did not answer in time: the bar comes back and the drawing leaves the page.
  function finishDrawing(id) {
    const saving = sketch.saving;
    if (!saving || saving.id !== id) return false;
    clearTimeout(saving.timer);
    sketch.saving = null;
    for (const node of saving.hidden) node.style.visibility = '';
    sketch.shapes = [];
    sketch.past = [];
    sketch.future = [];
    sheet.classList.toggle('hidden', !sketch.on);
    renderSketch();
    updateDrawControls();
    track();
    return true;
  }

  // What the host calls of the Draw tool, beside the calls of the bar.
  const drawingCalls = {
    // Turns Draw on or off, as its button does.
    draw(on) {
      if (!enabled) return false;
      whenBody(() => { build(); setDrawing(!!on); });
      return true;
    },
    // The host has the picture of a drawing.
    drawn(id) {
      return finishDrawing(String(id));
    },
    drawState() {
      return { drawing: sketch.on, tool: sketch.tool, color: sketch.color, shapes: sketch.shapes.length, saving: !!sketch.saving, undo: sketch.past.length, redo: sketch.future.length, typing: !!sketch.text };
    },
  };

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
          setMarkIntent(item.id, item.intent);
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
      if (options && typeof options.details === 'boolean') details = options.details;
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
        setMarkIntent(item.id, item.intent);
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
    // Whether the prompt carries the details of the page, as the user chose here or in another tab.
    details(on) {
      details = !!on;
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
      return { enabled: enabled, picking: picking, noting: !!popup, marks: marks.size, chosen: chosen.length, waiting: waiting(), theme: theme, position: position, details: details,
        hover: label && label.style.display === 'block' ? label.textContent : '' };
    },
  };
  Object.assign(globalThis[KEY], drawingCalls);

  send({ type: 'ready', url: location.href, title: document.title });
})();
//# sourceURL=codealta-toolbar.js