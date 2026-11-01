// CodeAlta browser agent.
//
// CodeAlta runs this script in an isolated world of each document a session browses (a sandbox realm with WebDriver
// BiDi), where the page can neither see it nor change it. It builds the accessibility snapshot of the page and its
// element ids, and gives the host what the real input of the browser needs: where an element is, what filling it
// takes. The snapshot and the accessibility part come from NeoAstra's automation agent, which computes Chrome's role
// names from the DOM, so that a snapshot reads the same on every browser engine.
(function () {
  'use strict';

  const KEY = '__codealtaBrowserAgent';
  if (globalThis[KEY]) return;

  const MAX_SNAPSHOT_NODES = 50000;
  function clip(text, limit) {
    text = String(text);
    return text.length > limit ? text.slice(0, limit) + '…' : text;
  }

  function squash(text) {
    return String(text == null ? '' : text).replace(/\s+/g, ' ').trim();
  }

  function describe(element) {
    if (!element || element.nodeType !== 1) return String(element);
    let text = '<' + element.localName;
    if (element.id) text += ' id="' + element.id + '"';
    const classes = typeof element.className === 'string' ? element.className.trim() : '';
    if (classes) text += ' class="' + clip(classes, 60) + '"';
    return text + '>';
  }


  // ---------------------------------------------------------------------------------------------------------------
  // DOM helpers
  // ---------------------------------------------------------------------------------------------------------------

  function viewOf(node) {
    return (node.ownerDocument || node).defaultView || window;
  }

  function parentOf(node) {
    return node.assignedSlot || node.parentNode || node.host || null;
  }

  function composedContains(ancestor, node) {
    for (let current = node; current; current = parentOf(current)) if (current === ancestor) return true;
    return false;
  }

  function styleOf(element) {
    try { return viewOf(element).getComputedStyle(element); } catch (error) { return null; }
  }

  const neverRendered = { script: 1, style: 1, template: 1, noscript: 1, head: 1, meta: 1, link: 1, title: 1, base: 1, param: 1, source: 1, track: 1 };

  // Hidden from assistive technology: not rendered, or removed with aria-hidden.
  function isHidden(element) {
    if (neverRendered[element.localName]) return true;
    if (element.getAttribute('aria-hidden') === 'true') return true;
    if (element.hidden && element.localName !== 'body') return true;
    const style = styleOf(element);
    if (!style) return false;
    if (style.display === 'none') return true;
    // display:contents has no box of its own but its children are rendered.
    return style.visibility === 'hidden' || style.visibility === 'collapse';
  }

  function isDisabled(element) {
    if (element.getAttribute('aria-disabled') === 'true') return true;
    if ('disabled' in element && element.disabled) return true;
    const fieldset = element.closest ? element.closest('fieldset[disabled]') : null;
    return !!fieldset && /^(button|input|select|textarea)$/.test(element.localName) && !composedContains(fieldset.querySelector('legend'), element);
  }

  // Element.matches throws for a selector the engine does not know, such as :modal on an older one.
  function matchesSafely(element, selector) {
    try { return element.matches(selector); } catch (error) { return false; }
  }

  function isEditableContent(element) {
    return !!element.isContentEditable;
  }

  function isTextControl(element) {
    if (element.localName === 'textarea') return true;
    if (element.localName !== 'input') return false;
    return !/^(button|checkbox|color|file|hidden|image|radio|range|reset|submit)$/.test(element.type);
  }

  // An element with an EditContext is an editing host of another kind: the engine gives the text that is entered to
  // the EditContext and tells the page with a textupdate event, and the page draws the text. Such an element is no
  // input and need not be editable content. This is how the Monaco editor takes its text.
  function editContextOf(element) {
    const context = element.editContext;
    return context && typeof context.updateText === 'function' && typeof viewOf(element).TextUpdateEvent === 'function' ? context : null;
  }

  // The element whose EditContext takes the text entered with the focus on an element: the outermost of the editable
  // elements around it, when that one has an EditContext. A form control and content marked as not editable keep the
  // keyboard to themselves, and editable content around an element with an EditContext takes the text itself.
  function editContextHost(element) {
    let host = null;
    for (let current = element; current && current.nodeType === 1; current = parentOf(current)) {
      const tag = current.localName;
      if (tag === 'input' || tag === 'textarea' || tag === 'select') return null;
      if (current.getAttribute('contenteditable') === 'false') break;
      if (current.isContentEditable || editContextOf(current)) host = current;
    }
    return host && editContextOf(host) ? host : null;
  }

  function isFocusable(element) {
    if (isDisabled(element) && element.getAttribute('aria-disabled') !== 'true') return false;
    if (element.hasAttribute('tabindex')) return !isNaN(parseInt(element.getAttribute('tabindex'), 10));
    switch (element.localName) {
      case 'a':
      case 'area': return element.hasAttribute('href');
      case 'input': return element.type !== 'hidden';
      case 'button':
      case 'select':
      case 'textarea':
      case 'iframe': return true;
      case 'summary': return !!element.parentElement && element.parentElement.localName === 'details';
      case 'audio':
      case 'video': return element.hasAttribute('controls');
      default:
        if (isEditableContent(element)) return !element.parentElement || !element.parentElement.isContentEditable;
        return !!editContextOf(element) && editContextHost(element) === element;
    }
  }

  function deepActiveElement(root) {
    let active = (root || document).activeElement;
    for (;;) {
      if (active && active.shadowRoot && active.shadowRoot.activeElement) active = active.shadowRoot.activeElement;
      else if (active && active.localName === 'iframe') {
        let inner = null;
        try { inner = active.contentDocument && active.contentDocument.activeElement; } catch (error) { inner = null; }
        if (!inner) break;
        active = inner;
      } else break;
    }
    return active;
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Accessibility: roles, names, and properties computed from the DOM
  // ---------------------------------------------------------------------------------------------------------------

  // The role names follow what Chrome reports in its accessibility tree, so that a snapshot reads like the one of
  // Chrome DevTools: ARIA role names where they exist, and Chrome's own names for the rest.
  const inputRoles = {
    button: 'button', submit: 'button', reset: 'button', image: 'button', file: 'button', checkbox: 'checkbox', radio: 'radio',
    range: 'slider', number: 'spinbutton', search: 'searchbox', color: 'ColorWell', date: 'Date', 'datetime-local': 'DateTime',
    month: 'DateTime', week: 'DateTime', time: 'InputTime', hidden: 'none',
  };

  const tagRoles = {
    article: 'article', aside: 'complementary', blockquote: 'blockquote', button: 'button', caption: 'caption', code: 'code',
    datalist: 'listbox', dd: 'definition', del: 'deletion', details: 'Details', dfn: 'term', dialog: 'dialog', dt: 'term',
    em: 'emphasis', fieldset: 'group', figcaption: 'Figcaption', figure: 'figure', h1: 'heading', h2: 'heading', h3: 'heading',
    h4: 'heading', h5: 'heading', h6: 'heading', hr: 'separator', iframe: 'Iframe', ins: 'insertion', label: 'LabelText',
    legend: 'Legend', li: 'listitem', main: 'main', mark: 'mark', math: 'math', menu: 'list', meter: 'meter', nav: 'navigation',
    ol: 'list', optgroup: 'group', option: 'option', output: 'status', p: 'paragraph', progress: 'progressbar', search: 'search',
    strong: 'strong', sub: 'subscript', summary: 'DisclosureTriangle', sup: 'superscript', table: 'table', tbody: 'rowgroup',
    textarea: 'textbox', tfoot: 'rowgroup', thead: 'rowgroup', time: 'time', tr: 'row', ul: 'list', canvas: 'Canvas',
    video: 'Video', audio: 'Audio', address: 'group', hgroup: 'group', dl: 'DescriptionList',
  };

  const controlRoles = {
    button: 1, checkbox: 1, ColorWell: 1, combobox: 1, DisclosureTriangle: 1, listbox: 1, menu: 1, menubar: 1, menuitem: 1,
    menuitemcheckbox: 1, menuitemradio: 1, radio: 1, scrollbar: 1, searchbox: 1, slider: 1, spinbutton: 1, switch: 1, tab: 1,
    textbox: 1, tree: 1, treeitem: 1,
  };

  const leafRoles = { image: 1, img: 1, meter: 1, scrollbar: 1, slider: 1, separator: 1, progressbar: 1, 'graphics-symbol': 1, 'doc-cover': 1 };

  const nameFromContent = {
    button: 1, cell: 1, checkbox: 1, columnheader: 1, gridcell: 1, heading: 1, link: 1, menuitem: 1, menuitemcheckbox: 1,
    menuitemradio: 1, option: 1, radio: 1, row: 1, rowheader: 1, switch: 1, tab: 1, tooltip: 1, treeitem: 1,
    DisclosureTriangle: 1, LabelText: 1, Legend: 1, caption: 1, Figcaption: 1, term: 1,
  };

  const sectioning = 'article,aside,main,nav,section';

  function explicitRole(element) {
    const attribute = element.getAttribute('role');
    if (!attribute) return '';
    const role = attribute.trim().split(/\s+/)[0].toLowerCase();
    if (role === 'img') return 'image';
    return role;
  }

  function implicitRole(element) {
    const tag = element.localName;
    switch (tag) {
      case 'a':
      case 'area': return element.hasAttribute('href') ? 'link' : 'generic';
      case 'img': return element.getAttribute('alt') === '' && !element.hasAttribute('aria-label') && !element.hasAttribute('aria-labelledby') ? 'none' : 'image';
      case 'svg': return 'image';
      case 'input': {
        const type = (element.getAttribute('type') || 'text').toLowerCase();
        if (inputRoles[type]) return inputRoles[type];
        return element.hasAttribute('list') ? 'combobox' : 'textbox';
      }
      case 'select': return element.multiple || element.size > 1 ? 'listbox' : 'combobox';
      case 'header': return element.closest(sectioning) ? 'generic' : 'banner';
      case 'footer': return element.closest(sectioning) ? 'generic' : 'contentinfo';
      case 'section': return element.hasAttribute('aria-label') || element.hasAttribute('aria-labelledby') || element.hasAttribute('title') ? 'region' : 'generic';
      case 'form': return element.hasAttribute('aria-label') || element.hasAttribute('aria-labelledby') || element.hasAttribute('title') ? 'form' : 'generic';
      case 'td': {
        const table = element.closest('table');
        const role = table ? explicitRole(table) : '';
        return role === 'grid' || role === 'treegrid' ? 'gridcell' : 'cell';
      }
      case 'th': return element.getAttribute('scope') === 'row' ? 'rowheader' : 'columnheader';
      default: return tagRoles[tag] || 'generic';
    }
  }

  function roleOf(element) {
    const explicit = explicitRole(element);
    if (explicit === 'presentation' || explicit === 'none') return isFocusable(element) ? implicitRole(element) : 'none';
    return explicit || implicitRole(element);
  }

  function referenced(element, attribute) {
    const ids = (element.getAttribute(attribute) || '').trim();
    if (!ids) return [];
    const root = element.getRootNode ? element.getRootNode() : element.ownerDocument;
    const result = [];
    ids.split(/\s+/).forEach(function (id) {
      const target = root.getElementById ? root.getElementById(id) : element.ownerDocument.getElementById(id);
      if (target) result.push(target);
    });
    return result;
  }

  function pseudoText(element, pseudo) {
    try {
      const content = viewOf(element).getComputedStyle(element, pseudo).content;
      const match = /^"((?:[^"\\]|\\.)*)"$/.exec(content || '');
      return match ? match[1].replace(/\\(.)/g, '$1') : '';
    } catch (error) {
      return '';
    }
  }

  const blockDisplays = /^(block|flex|grid|list-item|table|table-row|table-cell|flow-root)$/;

  // Text an element contributes to a name computed from content: its own alternative when it has one, else its text.
  function contentText(node, state) {
    if (node.nodeType === 3) return node.nodeValue;
    if (node.nodeType !== 1) return '';
    const element = node;
    if (state.visited.has(element)) return '';
    state.visited.add(element);
    if (!state.hidden && isHidden(element)) return '';
    const label = (element.getAttribute('aria-label') || '').trim();
    if (label) return label;
    const tag = element.localName;
    if (tag === 'img' || tag === 'area') return element.getAttribute('alt') || element.getAttribute('title') || '';
    if (tag === 'br') return '\n';
    if (tag === 'input') {
      const type = element.type;
      if (type === 'hidden') return '';
      if (/^(button|submit|reset)$/.test(type)) return element.value || (type === 'submit' ? 'Submit' : type === 'reset' ? 'Reset' : '');
      if (type === 'image') return element.getAttribute('alt') || '';
      if (/^(checkbox|radio|file|color)$/.test(type)) return '';
      return type === 'password' ? '' : element.value;
    }
    if (tag === 'textarea') return element.value;
    if (tag === 'select') {
      return Array.prototype.map.call(element.selectedOptions || [], function (option) { return option.textContent; }).join(' ');
    }
    if (tag === 'svg') {
      const title = element.querySelector(':scope > title');
      return title ? title.textContent : '';
    }
    let text = pseudoText(element, '::before');
    const children = element.shadowRoot ? element.shadowRoot.childNodes : tag === 'slot' && element.assignedNodes && element.assignedNodes().length ? element.assignedNodes() : element.childNodes;
    for (let index = 0; index < children.length; index++) text += contentText(children[index], state);
    text += pseudoText(element, '::after');
    const style = styleOf(element);
    return style && blockDisplays.test(style.display) ? ' ' + text + ' ' : text;
  }

  function labelsOf(element) {
    try { return element.labels ? Array.prototype.slice.call(element.labels) : []; } catch (error) { return []; }
  }

  // The accessible name: aria-labelledby, aria-label, the native label of the element, its content, then its title.
  function nameOf(element, role) {
    const labelledBy = referenced(element, 'aria-labelledby');
    if (labelledBy.length) {
      const text = squash(labelledBy.map(function (target) { return contentText(target, { visited: new Set(), hidden: true }); }).join(' '));
      if (text) return text;
    }
    const ariaLabel = squash(element.getAttribute('aria-label'));
    if (ariaLabel) return ariaLabel;

    const tag = element.localName;
    if (tag === 'input' || tag === 'select' || tag === 'textarea' || tag === 'meter' || tag === 'progress' || tag === 'output' || tag === 'button') {
      const labels = labelsOf(element);
      if (labels.length) {
        const text = squash(labels.map(function (label) {
          const state = { visited: new Set(), hidden: false };
          state.visited.add(element);
          return contentText(label, state);
        }).join(' '));
        if (text) return text;
      }
    }
    if (tag === 'input') {
      const type = element.type;
      if (/^(button|submit|reset)$/.test(type)) {
        const value = squash(element.value) || (type === 'submit' ? 'Submit' : type === 'reset' ? 'Reset' : '');
        if (value) return value;
      }
      if (type === 'image') { const alt = squash(element.getAttribute('alt')); if (alt) return alt; }
      // Chrome names the button of a file input this way, whatever the language of the page.
      if (type === 'file') return 'Choose File';
    }
    if (tag === 'img' || tag === 'area') { const alt = squash(element.getAttribute('alt')); if (alt) return alt; }
    if (tag === 'fieldset') { const legend = element.querySelector(':scope > legend'); if (legend) return squash(contentText(legend, { visited: new Set(), hidden: false })); }
    if (tag === 'figure') { const caption = element.querySelector(':scope > figcaption'); if (caption) return squash(contentText(caption, { visited: new Set(), hidden: false })); }
    if (tag === 'table') { const caption = element.querySelector(':scope > caption'); if (caption) return squash(contentText(caption, { visited: new Set(), hidden: false })); }
    if (tag === 'svg') { const title = element.querySelector(':scope > title'); if (title) return squash(title.textContent); }

    if (nameFromContent[role]) {
      const state = { visited: new Set(), hidden: false };
      let text = pseudoText(element, '::before');
      const children = element.shadowRoot ? element.shadowRoot.childNodes : element.childNodes;
      for (let index = 0; index < children.length; index++) text += contentText(children[index], state);
      text = squash(text + pseudoText(element, '::after'));
      if (text) return text;
    }
    const title = squash(element.getAttribute('title'));
    if (title) return title;
    if ((role === 'textbox' || role === 'searchbox' || role === 'combobox' || role === 'spinbutton') && element.getAttribute('placeholder')) return squash(element.getAttribute('placeholder'));
    if (tag === 'iframe') return squash(element.getAttribute('name'));
    return '';
  }

  function descriptionOf(element, name) {
    const describedBy = referenced(element, 'aria-describedby');
    if (describedBy.length) {
      const text = squash(describedBy.map(function (target) { return contentText(target, { visited: new Set(), hidden: true }); }).join(' '));
      if (text) return text;
    }
    const description = squash(element.getAttribute('aria-description'));
    if (description) return description;
    const title = squash(element.getAttribute('title'));
    return title && title !== name ? title : '';
  }

  function tristate(value) {
    return value === 'true' ? true : value === 'mixed' ? 'mixed' : value === 'false' ? false : undefined;
  }

  // The properties of a node, with the names Puppeteer's serialized accessibility nodes use.
  function propertiesOf(element, role, focused) {
    const result = {};
    const tag = element.localName;
    const aria = function (name) { return element.getAttribute('aria-' + name); };

    if (isDisabled(element)) result.disabled = true;
    if (element === focused) result.focused = true;

    const expanded = aria('expanded');
    if (expanded === 'true' || expanded === 'false') result.expanded = expanded === 'true';
    else if (tag === 'details') result.expanded = element.open;
    else if (tag === 'summary' && element.parentElement && element.parentElement.localName === 'details') result.expanded = element.parentElement.open;

    if (tag === 'option') { if (element.selected) result.selected = true; }
    else if (aria('selected') === 'true') result.selected = true;

    if (tag === 'input' && (element.type === 'checkbox' || element.type === 'radio')) result.checked = element.indeterminate ? 'mixed' : element.checked;
    else if (role === 'checkbox' || role === 'radio' || role === 'switch' || role === 'menuitemcheckbox' || role === 'menuitemradio') {
      const checked = tristate(aria('checked'));
      result.checked = checked === undefined ? false : checked;
    }
    const pressed = tristate(aria('pressed'));
    if (pressed !== undefined) result.pressed = pressed;

    if (tag === 'dialog' ? matchesSafely(element, ':modal') : aria('modal') === 'true') result.modal = true;
    if (tag === 'textarea' || aria('multiline') === 'true') result.multiline = true;
    if ((tag === 'select' && element.multiple) || aria('multiselectable') === 'true') result.multiselectable = true;
    if ((isTextControl(element) && element.readOnly) || aria('readonly') === 'true') result.readonly = true;
    if (element.required === true || aria('required') === 'true') result.required = true;

    if (role === 'heading') {
      const level = parseInt(aria('level') || (/^h([1-6])$/.exec(tag) || [])[1] || '2', 10);
      if (!isNaN(level)) result.level = level;
    } else if (aria('level')) {
      const level = parseInt(aria('level'), 10);
      if (!isNaN(level)) result.level = level;
    }

    if (tag === 'input' || tag === 'textarea') {
      const type = element.type;
      if (isTextControl(element) && type !== 'password' && element.value) result.value = clip(element.value, 1000);
      else if ((type === 'range' || type === 'number' || type === 'color') && element.value !== '') result.value = element.value;
      if (type === 'range' || type === 'number') {
        if (element.min !== '') result.valuemin = Number(element.min);
        if (element.max !== '') result.valuemax = Number(element.max);
      }
    } else if (tag === 'select') {
      const selected = element.selectedOptions && element.selectedOptions[0];
      if (selected) result.value = squash(selected.textContent);
    } else if (tag === 'option') {
      // The option value shown is its text, which is also what fill() matches.
      result.value = squash(element.textContent);
    } else if (tag === 'progress' || tag === 'meter') {
      result.value = element.value;
      if (tag === 'meter') result.valuemin = element.min;
      result.valuemax = element.max;
    } else if (isEditableContent(element) && role === 'textbox') {
      const text = squash(element.textContent);
      if (text) result.value = clip(text, 1000);
    }
    ['valuemin', 'valuemax', 'valuenow'].forEach(function (name) {
      const value = aria(name);
      if (value !== null && value !== '' && !isNaN(Number(value))) result[name === 'valuenow' ? 'value' : name] = Number(value);
    });
    if (aria('valuetext')) result.valuetext = aria('valuetext');

    const hasPopup = aria('haspopup');
    if (hasPopup && hasPopup !== 'false') result.haspopup = hasPopup === 'true' ? 'menu' : hasPopup;
    else if (tag === 'select' && role === 'combobox') result.haspopup = 'menu';
    const invalid = aria('invalid');
    if (invalid && invalid !== 'false') result.invalid = invalid;
    const autocomplete = aria('autocomplete');
    if (autocomplete && autocomplete !== 'none') result.autocomplete = autocomplete;
    const orientation = aria('orientation');
    if (orientation) result.orientation = orientation;
    if (aria('keyshortcuts')) result.keyshortcuts = aria('keyshortcuts');
    if (aria('roledescription')) result.roledescription = aria('roledescription');
    if (role === 'link' && element.href) result.url = String(element.href.baseVal !== undefined ? element.href.baseVal : element.href);
    return result;
  }

  // ---------------------------------------------------------------------------------------------------------------
  // Snapshot: the accessibility tree with stable element ids
  // ---------------------------------------------------------------------------------------------------------------

  // An id stays with its node across snapshots; the host passes a snapshot number that never repeats for a view, so an
  // id of an earlier document can never name a node of this one.
  const uidOfNode = new WeakMap();
  const nodeOfUid = new Map();
  const hasWeakRef = typeof WeakRef === 'function';

  function remember(node, snapshotId, counter) {
    let uid = uidOfNode.get(node);
    if (!uid) {
      uid = snapshotId + '_' + counter.value++;
      uidOfNode.set(node, uid);
    }
    nodeOfUid.set(uid, hasWeakRef ? new WeakRef(node) : node);
    return uid;
  }

  function nodeForUid(uid) {
    const reference = nodeOfUid.get(uid);
    if (!reference) return null;
    const node = hasWeakRef ? reference.deref() : reference;
    return node || null;
  }

  function fail(code, message) {
    const error = new Error(message);
    error.code = code;
    return error;
  }

  // Resolves an id to the element to act on; a text node stands for its parent element.
  function elementForUid(uid) {
    if (!nodeOfUid.size) throw fail('no-snapshot', 'No snapshot found. Use take_snapshot to capture one.');
    const node = nodeForUid(String(uid));
    // Both say what to do next: the caller is often a language model that only has this text to go by.
    const gone = function () { return fail('stale', 'Element with uid ' + uid + ' no longer exists on the page. Take a new snapshot with take_snapshot.'); };
    if (!node) {
      throw nodeOfUid.has(String(uid)) ? gone() : fail('unknown', 'Element uid "' + uid + '" not found on the page. Take a new snapshot with take_snapshot.');
    }
    const element = node.nodeType === 1 ? node : node.nodeType === 9 ? node.documentElement : node.parentElement;
    if (!element || !element.isConnected) throw gone();
    return element;
  }

  const landmarkRoles = { banner: 1, complementary: 1, contentinfo: 1, form: 1, main: 1, navigation: 1, region: 1, search: 1 };
  const containerRoles = { dialog: 1, alertdialog: 1, alert: 1, status: 1, log: 1, Iframe: 1 };

  function takeSnapshot(options) {
    const verbose = !!options.verbose;
    const snapshotId = String(options.snapshotId);
    const counter = { value: 0 };
    const focused = deepActiveElement(document);
    let count = 0;
    let truncated = false;

    function childNodesOf(element) {
      if (element.shadowRoot) return element.shadowRoot.childNodes;
      if (element.localName === 'slot' && element.assignedNodes && element.assignedNodes().length) return element.assignedNodes();
      return element.childNodes;
    }

    // Returns the nodes an element contributes to the tree: itself, or its children alone when it is left out, and
    // whether anything below it can take the focus.
    function build(element, insideControl) {
      const result = { nodes: [], focusable: false };
      if (count >= MAX_SNAPSHOT_NODES) { truncated = true; return result; }
      if (isHidden(element)) return result;
      const tag = element.localName;
      let role = roleOf(element);
      const editableRoot = isEditableContent(element) && !(element.parentElement && element.parentElement.isContentEditable);
      if (editableRoot && role === 'generic') role = 'textbox';
      const focusable = isFocusable(element) || role === 'option';
      const name = role === 'none' ? '' : clip(nameOf(element, role), 2000);
      const control = !!controlRoles[role];
      const plainTextField = isTextControl(element) || editableRoot || ((role === 'textbox' || role === 'searchbox') && !element.firstElementChild);
      const leafByRole = plainTextField || !!leafRoles[role] || tag === 'svg' || tag === 'canvas' || tag === 'video' || tag === 'audio' || tag === 'math';

      const children = [];
      let focusableBelow = false;
      if (!leafByRole) {
        let frameDocument = null;
        if (tag === 'iframe' || tag === 'frame') {
          try { frameDocument = element.contentDocument; } catch (error) { frameDocument = null; }
        }
        if (frameDocument && frameDocument.documentElement) {
          const inner = build(frameDocument.body || frameDocument.documentElement, false);
          focusableBelow = inner.focusable;
          children.push({
            node: frameDocument, r: 'RootWebArea', n: clip(squash(frameDocument.title), 2000),
            p: { url: String(frameDocument.URL) }, c: inner.nodes,
          });
        } else {
          const nodes = childNodesOf(element);
          for (let index = 0; index < nodes.length; index++) {
            const child = nodes[index];
            if (child.nodeType === 3) {
              const text = squash(child.nodeValue);
              // Text inside a control is part of the control, not a node of its own.
              if (text && (verbose || !(insideControl || control))) children.push({ node: child, r: 'StaticText', n: clip(text, 2000) });
            } else if (child.nodeType === 1) {
              const built = build(child, insideControl || control);
              if (built.focusable) focusableBelow = true;
              for (let inner = 0; inner < built.nodes.length; inner++) children.push(built.nodes[inner]);
            }
          }
        }
      }
      result.focusable = focusable || focusableBelow;

      // A node is a leaf when it has nothing below it, or when what is below only spells out its name: a named
      // control, link, or heading without anything focusable inside. A leaf is listed without children.
      const leaf = leafByRole || !children.length || (!focusableBelow && !!name && (focusable || role === 'heading'));
      let interesting = verbose;
      if (!interesting && role !== 'none') {
        // A landmark, a dialog, or a live region is listed for what it holds; an empty one says nothing.
        const container = (!!landmarkRoles[role] || !!containerRoles[role]) && (children.length !== 0 || !!name);
        interesting = focusable || control || editableRoot || container || (!insideControl && leaf && !!name);
      }
      if (!interesting) { result.nodes = children; return result; }

      count++;
      const node = { node: element, r: role };
      if (name) node.n = name;
      const properties = propertiesOf(element, role, focused);
      const description = descriptionOf(element, name);
      if (description) properties.description = clip(description, 1000);
      if (Object.keys(properties).length) node.p = properties;
      if (children.length && (verbose || !leaf)) node.c = children;
      result.nodes = [node];
      return result;
    }

    // Forget the ids of nodes that are gone, then number the tree.
    nodeOfUid.forEach(function (reference, uid) {
      const node = hasWeakRef ? reference.deref() : reference;
      if (!node || node.isConnected === false) nodeOfUid.delete(uid);
    });

    const body = document.body || document.documentElement;
    const root = {
      node: document, r: 'RootWebArea', n: clip(squash(document.title), 2000),
      p: { url: String(document.URL) }, c: body ? build(body, false).nodes : [],
    };

    // Number the nodes that made it into the tree, in document order. A node seen in an earlier snapshot keeps its id.
    (function number(node) {
      node.i = remember(node.node, snapshotId, counter);
      delete node.node;
      if (node.c) node.c.forEach(number);
    })(root);
    return { snapshotId: snapshotId, root: root, truncated: truncated, nodes: count };
  }


  // ---------------------------------------------------------------------------------------------------------------
  // Operations of the host. The host clicks, moves and types with the real input of the browser: here are only the
  // reads it needs for that (where an element is, what filling it takes) and what has to be done inside the page.
  // ---------------------------------------------------------------------------------------------------------------

  function scrollIntoViewIfNeeded(element) {
    const view = viewOf(element);
    const rect = element.getBoundingClientRect();
    const inside = rect.top >= 0 && rect.left >= 0 && rect.bottom <= view.innerHeight && rect.right <= view.innerWidth;
    if (!inside) {
      try { element.scrollIntoView({ block: 'center', inline: 'center', behavior: 'instant' }); }
      catch (error) { element.scrollIntoView(); }
    }
  }

  // The offset of the viewport of an element's document in the viewport of this frame: the same-origin frames between.
  function frameOffset(element) {
    let x = 0, y = 0;
    let view = viewOf(element);
    while (view !== window) {
      let frame = null;
      try { frame = view.frameElement; } catch (error) { frame = null; }
      if (!frame) break;
      const outer = frame.getBoundingClientRect();
      const style = styleOf(frame);
      x += outer.left + frame.clientLeft + (style ? parseFloat(style.paddingLeft) || 0 : 0);
      y += outer.top + frame.clientTop + (style ? parseFloat(style.paddingTop) || 0 : 0);
      view = viewOf(frame);
    }
    return { x: x, y: y };
  }

  function elementAt(root, x, y) {
    let element = root.elementFromPoint ? root.elementFromPoint(x, y) : null;
    while (element && element.shadowRoot && element.shadowRoot.elementFromPoint) {
      const inner = element.shadowRoot.elementFromPoint(x, y);
      if (!inner || inner === element) break;
      element = inner;
    }
    return element;
  }

  // The point a pointer lands on: the middle of the first box of the element that has an area, inside the viewport.
  function centerOf(element) {
    const view = viewOf(element);
    let rect = null;
    const rects = element.getClientRects();
    for (let index = 0; index < rects.length; index++) {
      if (rects[index].width > 0 && rects[index].height > 0) { rect = rects[index]; break; }
    }
    if (!rect) rect = element.getBoundingClientRect();
    if (!(rect.width > 0 && rect.height > 0)) throw fail('not-visible', 'The element ' + describe(element) + ' has no visible box to interact with.');
    const left = Math.max(rect.left, 0), right = Math.min(rect.right, view.innerWidth);
    const top = Math.max(rect.top, 0), bottom = Math.min(rect.bottom, view.innerHeight);
    return { x: right > left ? (left + right) / 2 : rect.left + rect.width / 2, y: bottom > top ? (top + bottom) / 2 : rect.top + rect.height / 2 };
  }

  // Where to put the pointer on an element, in the viewport of this frame, after scrolling it into view; and what
  // covers it there, which still gets the event, as it would under a real pointer.
  function pointOf(element, checkDisabled) {
    if (checkDisabled && isDisabled(element) && element.getAttribute('aria-disabled') !== 'true') throw fail('disabled', 'The element ' + describe(element) + ' is disabled.');
    scrollIntoViewIfNeeded(element);
    const local = centerOf(element);
    const hit = elementAt(element.ownerDocument, local.x, local.y);
    const offset = frameOffset(element);
    const result = { x: local.x + offset.x, y: local.y + offset.y };
    if (hit && !composedContains(element, hit) && !composedContains(hit, element)) result.obscuredBy = describe(hit);
    return result;
  }

  function point(options) {
    return pointOf(elementForUid(options.uid), !!options.checkDisabled);
  }

  // A click on an option of a closed list selects it in its select, as a user who opens the list and picks it.
  function prepareClick(options) {
    const element = elementForUid(options.uid);
    if (element.localName === 'option' && !options.dblClick) {
      const select = element.closest('select');
      if (select && !select.multiple && !isDisabled(select) && !element.disabled) {
        if (select.value !== element.value || !element.selected) {
          setNativeValue(select, element.value);
          fireInput(select);
          fireChange(select);
        }
        return { done: true };
      }
    }
    return pointOf(element, true);
  }

  // The origin of the content of a frame element in the viewport of this frame, for the input of its own document.
  function frameBox(options) {
    const element = elementForUid(options.uid);
    const rect = element.getBoundingClientRect();
    const offset = frameOffset(element);
    const style = styleOf(element);
    const padLeft = style ? parseFloat(style.paddingLeft) || 0 : 0;
    const padTop = style ? parseFloat(style.paddingTop) || 0 : 0;
    return { x: rect.left + element.clientLeft + padLeft + offset.x, y: rect.top + element.clientTop + padTop + offset.y };
  }

  // The box of an element in the viewport of this frame, after scrolling it into view, for a capture of it.
  function rectOf(options) {
    const element = elementForUid(options.uid);
    scrollIntoViewIfNeeded(element);
    const rect = element.getBoundingClientRect();
    const offset = frameOffset(element);
    return { x: rect.left + offset.x, y: rect.top + offset.y, width: rect.width, height: rect.height, scrollX: window.scrollX, scrollY: window.scrollY };
  }

  function element(options) {
    return elementForUid(options.uid);
  }

  function setNativeValue(element, value) {
    const view = viewOf(element);
    const prototype = element.localName === 'textarea' ? view.HTMLTextAreaElement.prototype : element.localName === 'select' ? view.HTMLSelectElement.prototype : view.HTMLInputElement.prototype;
    // The setter of the prototype goes around the value tracking that frameworks put on the element, so that the
    // input event that follows is seen as a change.
    const descriptor = Object.getOwnPropertyDescriptor(prototype, 'value');
    if (descriptor && descriptor.set) descriptor.set.call(element, value);
    else element.value = value;
  }

  function fireInput(element) {
    element.dispatchEvent(new (viewOf(element).Event)('input', { bubbles: true, composed: true }));
  }

  function fireChange(element) {
    element.dispatchEvent(new (viewOf(element).Event)('change', { bubbles: true }));
  }

  function isToggle(element) {
    if (element.localName === 'input') return element.type === 'checkbox' || element.type === 'radio';
    const role = element.getAttribute('role');
    return role === 'checkbox' || role === 'radio' || role === 'switch';
  }

  function isChecked(element) {
    return element.localName === 'input' ? element.checked : element.getAttribute('aria-checked') === 'true';
  }

  // Input types whose value a keyboard does not type, which a page reads from the value alone.
  const valueTypes = { date: 1, time: 1, 'datetime-local': 1, month: 1, week: 1, color: 1, range: 1 };

  function focusElement(element) {
    try { element.focus({ preventScroll: true }); } catch (error) { element.focus(); }
  }

  // What filling an element takes: a select, an input that takes a value, done here; a toggle, a click of the host if
  // its state differs; text, typed by the host after this selects what the element holds.
  function prepareFill(options) {
    let element = elementForUid(options.uid);
    const value = String(options.value);
    if (element.localName === 'label' && element.control) element = element.control;
    if (element.localName === 'option') {
      const owner = element.closest('select');
      if (owner) element = owner;
    }
    if (isDisabled(element) && element.getAttribute('aria-disabled') !== 'true') throw fail('disabled', 'The element ' + describe(element) + ' is disabled.');

    if (element.localName === 'select') {
      // An option is chosen by its text, as the snapshot shows it, or by its value.
      let match = null;
      for (let index = 0; index < element.options.length; index++) {
        const option = element.options[index];
        if (squash(option.textContent) === value || squash(option.label) === value) { match = option; break; }
      }
      if (!match) for (let index = 0; index < element.options.length; index++) if (element.options[index].value === value) { match = element.options[index]; break; }
      if (!match) throw fail('no-option', 'Could not find option with text "' + value + '"');
      focusElement(element);
      if (!match.selected || element.value !== match.value) {
        if (element.multiple) match.selected = true; else setNativeValue(element, match.value);
        fireInput(element);
        fireChange(element);
      }
      return { action: 'done' };
    }

    if (isToggle(element)) {
      if (value !== 'true' && value !== 'false') throw fail('bad-value', 'Checkboxes, radio boxes and toggles require "true" or "false" value, but ' + value + ' was used');
      if (isChecked(element) === (value === 'true')) return { action: 'done' };
      try { const at = pointOf(element, false); at.action = 'click'; return at; }
      catch (error) {
        // A control the page hides behind a styled box is still toggled, as a click on its label would.
        element.click();
        return { action: 'done' };
      }
    }

    if (element.localName === 'input' || element.localName === 'textarea') {
      if (element.type === 'file') throw fail('file-input', 'Use upload_file for a file input.');
      if (element.readOnly) throw fail('readonly', 'The element ' + describe(element) + ' is read-only.');
      scrollIntoViewIfNeeded(element);
      if (element.localName === 'input' && valueTypes[element.type]) {
        focusElement(element);
        setNativeValue(element, value);
        fireInput(element);
        fireChange(element);
        return { action: 'done' };
      }
      focusElement(element);
      try { element.select(); } catch (error) { /* an input type without a selection */ }
      try { if (isTextControl(element)) element.setSelectionRange(0, element.value.length); } catch (error) { /* see above */ }
      return { action: 'type', empty: element.value.length === 0 };
    }

    if (isEditableContent(element)) {
      scrollIntoViewIfNeeded(element);
      focusElement(element);
      const doc = element.ownerDocument;
      const selection = viewOf(element).getSelection();
      const range = doc.createRange();
      range.selectNodeContents(element);
      selection.removeAllRanges();
      selection.addRange(range);
      return { action: 'type', empty: (element.textContent || '').length === 0 };
    }

    throw fail('not-fillable', 'The element ' + describe(element) + ' is not an input, a text area, a select, a toggle, or editable content.');
  }

  function focus(options) {
    const element = elementForUid(options.uid);
    scrollIntoViewIfNeeded(element);
    focusElement(element);
    return {};
  }

  function findText(texts) {
    const body = document.body;
    if (!body) return null;
    const visible = body.innerText || body.textContent || '';
    for (let index = 0; index < texts.length; index++) if (texts[index] && visible.indexOf(texts[index]) >= 0) return texts[index];
    // A name that is not rendered text: a label, an alternative text, a placeholder, or the value of a button.
    const labelled = document.querySelectorAll('[aria-label],[alt],[title],[placeholder],input[type=button],input[type=submit],input[type=reset]');
    for (let index = 0; index < labelled.length; index++) {
      const item = labelled[index];
      if (isHidden(item)) continue;
      const names = [item.getAttribute('aria-label'), item.getAttribute('alt'), item.getAttribute('title'), item.getAttribute('placeholder'), item.localName === 'input' ? item.value : null];
      for (let position = 0; position < texts.length; position++) {
        for (let name = 0; name < names.length; name++) if (names[name] && texts[position] && names[name].indexOf(texts[position]) >= 0) return texts[position];
      }
    }
    return null;
  }

  function hasText(options) {
    return { text: findText((options.texts || []).map(String)) };
  }

  // Ends once the DOM has not changed for a moment, or after a deadline.
  function waitForStableDom(options) {
    const quiet = Number(options.quiet) || 100;
    const timeout = Number(options.timeout) || 3000;
    return new Promise(function (resolve) {
      const started = performance.now();
      let changed = started;
      let observer = null;
      try {
        observer = new MutationObserver(function () { changed = performance.now(); });
        observer.observe(document.documentElement || document, { childList: true, subtree: true, attributes: true });
      } catch (error) { observer = null; }
      const check = function () {
        const now = performance.now();
        if (now - changed >= quiet || now - started >= timeout) {
          if (observer) observer.disconnect();
          resolve({ stable: now - changed >= quiet });
        } else setTimeout(check, Math.max(10, Math.min(quiet - (now - changed), timeout - (now - started))));
      };
      setTimeout(check, quiet);
    });
  }

  function info() {
    return {
      url: String(document.URL), title: String(document.title), readyState: document.readyState,
      viewport: { width: window.innerWidth, height: window.innerHeight, scale: window.devicePixelRatio || 1, scrollX: window.scrollX, scrollY: window.scrollY },
      size: { width: Math.max(document.documentElement ? document.documentElement.scrollWidth : 0, document.body ? document.body.scrollWidth : 0),
        height: Math.max(document.documentElement ? document.documentElement.scrollHeight : 0, document.body ? document.body.scrollHeight : 0) },
    };
  }

  // The frame elements of the latest snapshot whose documents this agent cannot read: the host reads them with
  // the agent of their own documents.
  function remoteFrames() {
    const frames = [];
    nodeOfUid.forEach(function (reference, uid) {
      const node = hasWeakRef ? reference.deref() : reference;
      if (!node || node.nodeType !== 1 || (node.localName !== 'iframe' && node.localName !== 'frame')) return;
      let inner = null;
      try { inner = node.contentDocument; } catch (error) { inner = null; }
      if (!inner && !isHidden(node)) frames.push(uid);
    });
    return { uids: frames };
  }

  function frameWindow(options) {
    return elementForUid(options.uid).contentWindow;
  }

  const operations = {
    snapshot: takeSnapshot, point: point, prepareClick: prepareClick, frameBox: frameBox, rect: rectOf, prepareFill: prepareFill,
    focus: focus, hasText: hasText, waitForStableDom: waitForStableDom, info: info, remoteFrames: remoteFrames,
  };

  function errorResult(error) {
    const message = error && error.message !== undefined ? String(error.message) : String(error);
    return { ok: false, error: { message: message, code: (error && error.code) || 'error' } };
  }

  const agent = {
    version: 1,

    // Runs an operation and returns its result as JSON text; an operation that returns a Promise is awaited.
    call: function (name, options) {
      try {
        const operation = operations[name];
        if (!operation) throw fail('unknown-operation', 'Unknown agent operation ' + name + '.');
        const value = operation(options || {});
        if (value && typeof value.then === 'function') {
          return value.then(function (resolved) { return JSON.stringify({ ok: true, value: resolved === undefined ? null : resolved }); },
            function (error) { return JSON.stringify(errorResult(error)); });
        }
        return JSON.stringify({ ok: true, value: value === undefined ? null : value });
      } catch (error) {
        return JSON.stringify(errorResult(error));
      }
    },

    // An element of a snapshot, as an object, for the host to pass to the browser (files of an input, arguments).
    element: function (uid) { return element({ uid: uid }); },

    // The window of a frame element, which tells the host which document it holds.
    frameWindow: function (uid) { return frameWindow({ uid: uid }); },
  };

  try { Object.defineProperty(globalThis, KEY, { value: agent, enumerable: false, configurable: false, writable: false }); }
  catch (error) { globalThis[KEY] = agent; }
})();
//# sourceURL=codealta-browser-agent.js
