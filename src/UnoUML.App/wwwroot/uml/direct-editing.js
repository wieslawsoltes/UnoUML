// Transient handles and text fields live outside the diagram SVG. Their screen-space
// bounds follow the transformed scene, and they never appear in exported diagrams.
export class DirectEditing {
  constructor(view) {
    this.view = view;
    const handle = (kind, label, parent) => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'connection-port ' + kind;
      button.dataset.wire = kind;
      button.title = label + ' — drag or activate and choose a target';
      button.setAttribute('aria-label', label);
      button.hidden = true;
      button.addEventListener('keydown', e => {
        if (e.key !== ' ' && e.key !== 'Enter') return;
        e.preventDefault(); e.stopPropagation();
        this.startConnection(kind);
        view.surface.focus({preventScroll: true});
      });
      parent.append(button);
      return button;
    };
    this.port = handle('connect', 'Connect selected object', view.selection);
    this.from = handle('retargetFrom', 'Reattach source endpoint', view.surface);
    this.to = handle('retargetTo', 'Reattach target endpoint', view.surface);
    this.box = document.createElement('div');
    this.box.className = 'marquee';
    this.box.hidden = true;
    view.surface.append(this.box);
  }

  startConnection(action) {
    const hit = this.view.index?.hits.get(this.view.marked);
    if (!hit) return;
    this.view.linkStart = {hit, action};
    this.view.announcement.textContent = 'Choose the target object. Press Escape to cancel.';
  }

  setEndpoints(from, to) {
    const v = this.view;
    this.endpoints = null;
    this.from.hidden = this.to.hidden = true;
    if (!from || !to || from === to || !v.marked?.matches('.link,.message')) return;
    const a = v.index?.byId.get(from), b = v.index?.byId.get(to);
    if (!a || !b) return;
    // Both engines can emit separate marker paths and larger transparent hit
    // strokes. Select the longest connector and map its ends to lexical aliases;
    // reverse arrows and nested transforms therefore keep their original syntax.
    let stroke, length = 0;
    for (const shape of v.marked.querySelectorAll('line,polyline,path')) {
      if (shape.localName === 'path' && /z\s*$/i.test(shape.getAttribute('d') || '')) continue;
      const size = shape.getTotalLength();
      if (size > length) { stroke = shape; length = size; }
    }
    if (!stroke || length < 2) return;
    const transform = stroke.getScreenCTM();
    if (!transform) return;
    let start = stroke.getPointAtLength(0).matrixTransform(transform);
    let end = stroke.getPointAtLength(length).matrixTransform(transform);
    const cost = (point, rect) => (point.x - (rect.left + rect.width / 2)) ** 2 +
      (point.y - (rect.top + rect.height / 2)) ** 2;
    const ar = a.getBoundingClientRect(), br = b.getBoundingClientRect();
    if (cost(start, ar) + cost(end, br) > cost(start, br) + cost(end, ar)) [start, end] = [end, start];
    const r = v.rect();
    this.endpoints = [start, end].map(p => ({x: (p.x - r.left - v.x) / v.z, y: (p.y - r.top - v.y) / v.z}));
    this.from.hidden = this.to.hidden = false;
    this.positionEnds();
  }

  positionEnds() {
    if (!this.endpoints) return;
    const v = this.view;
    for (const [i, button] of [this.from, this.to].entries()) {
      const p = this.endpoints[i];
      button.style.left = (p.x * v.z + v.x) + 'px';
      button.style.top = (p.y * v.z + v.y) + 'px';
    }
  }

  beginLabel(label, revision, multiline = false, language = 'text') {
    const { surface } = this.view;
    const marked = language === 'ditaa' ? this.view.paper.querySelector('svg') : this.view.marked;
    if (!marked?.isConnected || !surface.clientWidth) return false;
    this.finishLabel(false);
    const input = document.createElement('textarea');
    input.className = 'canvas-label-editor';
    input.setAttribute('aria-label', 'Edit diagram label');
    input.setAttribute('enterkeyhint', 'done');
    input.spellcheck = false;
    input.rows = 1;
    input.maxLength = 1048576;
    input.wrap = language === 'ditaa' ? 'off' : 'soft';
    input.classList.toggle('canvas-block-editor', multiline || language !== 'text' && language !== 'creole');
    input.value = label;

    // Locate the actual glyphs where possible, including when the clicked hit is a
    // group spanning a complete connector, participant or class compartment.
    let anchor = marked;
    if (marked.localName !== 'text') {
      const texts = [...marked.querySelectorAll('text')];
      anchor = texts.find(el => el.textContent.trim() === label)
        || texts.find(el => el.textContent.trim().endsWith(label) && label.length > 0)
        || marked;
    }
    const panel = document.createElement('div');
    panel.className = 'canvas-edit-actions';
    panel.setAttribute('role', 'toolbar');
    panel.setAttribute('aria-label', 'Diagram editing tools');
    const command = (caption, title, action) => {
      const button = document.createElement('button');
      button.type = 'button'; button.textContent = caption; button.title = title;
      button.setAttribute('aria-label', title);
      button.addEventListener('pointerdown', event => { event.preventDefault(); event.stopPropagation(); });
      button.addEventListener('click', event => { event.preventDefault(); event.stopPropagation(); action(); });
      panel.append(button);
      return button;
    };
    const insert = template => {
      const selection = input.value.slice(input.selectionStart, input.selectionEnd);
      const text = template.replace('$', () => selection || 'x');
      input.setRangeText(text, input.selectionStart, input.selectionEnd, 'select');
      input.focus({preventScroll: true}); this.positionField();
    };
    const format = (open, close = open) => {
      const start = input.selectionStart, end = input.selectionEnd;
      const selection = input.value.slice(start, end);
      const wrapped = selection.startsWith(open) && selection.endsWith(close) && selection.length >= open.length + close.length;
      input.setRangeText(wrapped ? selection.slice(open.length, -close.length) : open + (selection || 'text') + close, start, end, 'select');
      input.focus({preventScroll:true}); this.positionField();
    };
    if (language === 'asciimath' || language === 'tex') {
      const tex = language === 'tex';
      command('a/b', 'Insert fraction', () => insert(tex ? '\\frac{$}{b}' : '($)/(b)'));
      command('√', 'Insert square root', () => insert(tex ? '\\sqrt{$}' : 'sqrt($)'));
      command('x²', 'Insert exponent', () => insert(tex ? '{$}^{2}' : '($)^2'));
      command('∑', 'Insert sum', () => insert(tex ? '\\sum_{i=1}^{n}{$}' : 'sum_(i=1)^n ($)'));
    } else if (language === 'ditaa') {
      command('□', 'Insert ASCII box', () => insert('+--------+\n| Box    |\n+--------+'));
      command('→', 'Insert ASCII connector', () => insert('---->'));
    } else if (language === 'creole') {
      command('B', 'Bold (Ctrl+B)', () => format('**')).style.fontWeight = 'bold';
      command('I', 'Italic (Ctrl+I)', () => format('//')).style.fontStyle = 'italic';
      command('U', 'Underline (Ctrl+U)', () => format('__')).style.textDecoration = 'underline';
      command('S', 'Strikethrough', () => format('--')).style.textDecoration = 'line-through';
      command('{}', 'Monospaced text', () => format('<font:monospaced>', '</font>'));
    }
    command('Apply', multiline ? 'Apply diagram content (Ctrl+Enter)' : 'Apply diagram label (Enter)', () => this.finishLabel(true, true));
    command('Cancel', 'Cancel diagram edit (Escape)', () => this.finishLabel(false, true));
    this.field = { input, panel, original: label, revision, anchor, multiline, language };
    const stop = e => e.stopPropagation();
    for (const type of ['pointerdown','pointerup','pointermove','click','dblclick','cut','copy','paste','keyup','wheel'])
      input.addEventListener(type, stop);
    input.addEventListener('keydown', e => {
      e.stopPropagation();
      if (e.isComposing || e.keyCode === 229) return;
      if (e.altKey && e.key === 'F10') {
        e.preventDefault(); panel.querySelector('button').focus(); return;
      }
      if (language === 'creole' && (e.ctrlKey || e.metaKey) && !e.altKey && ['b','i','u'].includes(e.key.toLowerCase())) {
        e.preventDefault(); format({b:'**',i:'//',u:'__'}[e.key.toLowerCase()]); return;
      }
      if (e.key === 'Tab' && language === 'ditaa') {
        e.preventDefault(); insert('    '); return;
      }
      if (e.key === 'Escape' || e.key === 'Enter' && (e.ctrlKey || e.metaKey || !multiline && !e.shiftKey) || e.key === 'Tab' && language !== 'ditaa') {
        e.preventDefault();
        this.finishLabel(e.key !== 'Escape', true);
      }
    });
    input.addEventListener('blur', event => {
      if (!panel.contains(event.relatedTarget)) this.finishLabel(true);
    });
    panel.addEventListener('keydown', event => {
      event.stopPropagation();
      if (event.key === 'Escape') { event.preventDefault(); this.finishLabel(false, true); }
    });
    panel.addEventListener('focusout', event => {
      if (event.relatedTarget !== input && !panel.contains(event.relatedTarget)) this.finishLabel(true);
    });
    input.addEventListener('input', () => this.positionField());
    surface.append(input);
    surface.append(panel);
    this.positionField();
    input.focus({preventScroll: true});
    input.select();
    return true;
  }

  finishLabel(commit, refocus = false) {
    const field = this.field;
    if (!field) return;
    this.field = null;
    const text = field.input.value;
    field.input.remove();
    field.panel.remove();
    if (refocus) this.view.surface.focus({preventScroll: true});
    if (commit && text !== field.original)
      this.view.emit('label', {revision: field.revision, text});
  }

  positionField() {
    if (!this.field) return;
    const { input, panel, anchor, multiline, language } = this.field;
    const bounds = this.view.rect(), target = anchor.getBoundingClientRect();
    const block = multiline || language !== 'text' && language !== 'creole' || input.value.includes('\n');
    const size = block ? 14 : Math.max(13, Math.min(24, target.height * .78));
    const lines = input.value.split('\n');
    let longest = 0;
    for (const line of lines) longest = Math.max(longest, line.length);
    const width = Math.max(1, Math.min(bounds.width - 8, Math.max(block ? 320 : 150, target.width + 18, longest * size * .6 + 24)));
    const height = Math.min(Math.max(40, bounds.height - 64), block ? Math.max(80, Math.min(320, lines.length * 20 + 20)) : Math.max(30, size + 14));
    const within = (n, max) => Math.max(4, Math.min(max - 4, n));
    Object.assign(input.style, {
      width: width + 'px', height: height + 'px', fontSize: size + 'px',
      left: within(target.left - bounds.left + (target.width - width) / 2, bounds.width - width) + 'px',
      top: within(target.top - bounds.top + (target.height - height) / 2, bounds.height - height) + 'px'
    });
    Object.assign(panel.style, { left: input.style.left, maxWidth: width + 'px' });
    panel.style.top = Math.max(4, Math.min(bounds.height - panel.getBoundingClientRect().height - 4, parseFloat(input.style.top) + height + 4)) + 'px';
  }

  marquee(drag, x, y) {
    const r = this.view.rect();
    const clamp = (n, min, max) => Math.max(min, Math.min(n, max));
    x = clamp(x, r.left, r.right); y = clamp(y, r.top, r.bottom);
    const left = Math.min(drag.x, x), top = Math.min(drag.y, y);
    const right = Math.max(drag.x, x), bottom = Math.max(drag.y, y);
    this.box.hidden = false;
    Object.assign(this.box.style, {left: left - r.left + 'px', top: top - r.top + 'px',
      width: right - left + 'px', height: bottom - top + 'px'});
    return {left, top, right, bottom};
  }

  selectRegion(drag, x, y) {
    const r = this.marquee(drag, x, y), chosen = new Set(), hits = [];
    // Read bounds once on release. Pointer moves only update the overlay rectangle.
    for (const el of this.view.objects || []) {
      const b = el.getBoundingClientRect();
      if (b.left < r.left || b.right > r.right || b.top < r.top || b.bottom > r.bottom) continue;
      if (chosen.has(el.parentElement?.closest('[data-uml-hit]'))) continue;
      chosen.add(el); hits.push(this.view.index.hits.get(el));
    }
    this.box.hidden = true;
    this.view.region(hits, drag.add);
    this.view.announcement.textContent = hits.length + ' objects inside the selection box';
  }
}
